using System.Data.Common;
using AdoMcpServer.Models;
using AdoMcpServer.Services.Providers;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using DbType = AdoMcpServer.Models.DbType;

namespace AdoMcpServer.Services;

public class DatabaseService(
    IOptions<List<DatabaseConfig>> options,
    ILogger<DatabaseService> logger) : IDatabaseService
{
    // ─────────────────────────────────────────────────────────────────────────
    // Global configs (from appsettings.json) – read-only, shared by all callers.
    // ─────────────────────────────────────────────────────────────────────────
    private readonly IReadOnlyList<DatabaseConfig> _globalConfigs =
        options.Value.ToList().AsReadOnly();

    // ─────────────────────────────────────────────────────────────────────────
    // Runtime dynamic connections – a single global list shared by all callers.
    // Dynamic connections added via add_connection are visible to every client.
    // ─────────────────────────────────────────────────────────────────────────
    private readonly List<DatabaseConfig> _dynamicConfigs = [];
    private readonly Lock _lock = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Per-engine providers (stateless; instantiated once per service lifetime)
    // ─────────────────────────────────────────────────────────────────────────
    private readonly Dictionary<DbType, IDbProvider> _providers = new()
    {
        [DbType.SqlServer]  = new SqlServerDbProvider(logger),
        [DbType.MySql]      = new MySqlDbProvider(logger),
        [DbType.PostgreSql] = new PostgreSqlDbProvider(logger),
        [DbType.Sqlite]     = new SqliteDbProvider(logger),
        [DbType.Oracle]     = new OracleDbProvider(logger),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<DatabaseConfig> GetConfigurations()
    {
        List<DatabaseConfig> dynamic;
        lock (_lock) dynamic = [.._dynamicConfigs];

        // Dynamic connections appear first; global ones fill the rest.
        var dynamicNames = new HashSet<string>(
            dynamic.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return dynamic
            .Concat(_globalConfigs.Where(c => !dynamicNames.Contains(c.Name)))
            .ToList()
            .AsReadOnly();
    }

    public async Task<string?> AddConnectionAsync(
        DatabaseConfig config, bool testFirst = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return "连接名称不能为空。";
        if (string.IsNullOrWhiteSpace(config.ConnectionString))
            return "连接字符串不能为空。";

        if (testFirst)
        {
            try
            {
                await using var conn = CreateConnection(config);
                await conn.OpenAsync(ct);
            }
            catch (Exception ex)
            {
                return $"连接测试失败: {ex.Message}";
            }
        }

        lock (_lock)
        {
            _dynamicConfigs.RemoveAll(c =>
                string.Equals(c.Name, config.Name, StringComparison.OrdinalIgnoreCase));
            _dynamicConfigs.Add(config);
        }
        logger.LogInformation(
            "Connection '{Name}' ({DbType}) added to global dynamic store.",
            config.Name, config.DbType);
        return null; // success
    }

    public bool RemoveConnection(string name)
    {
        bool removed;
        lock (_lock)
            removed = _dynamicConfigs.RemoveAll(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            logger.LogInformation("Connection '{Name}' removed from global dynamic store.", name);
        return removed;
    }

    public async Task<List<TableInfo>> ListDbObjectsAsync(
        string connectionName, string? nameFilter = null,
        string? schemaFilter = null, CancellationToken ct = default)
    {
        var (cfg, conn) = await OpenAsync(connectionName, ct);
        await using (conn)
            return await GetProvider(cfg).ListDbObjectsAsync(conn, nameFilter, schemaFilter, ct);
    }

    public async Task<TableSchema> GetTableSchemaAsync(
        string connectionName, string tableName, string? schema = null, CancellationToken ct = default)
    {
        var (cfg, conn) = await OpenAsync(connectionName, ct);
        await using (conn)
            return await GetProvider(cfg).GetTableSchemaAsync(conn, tableName, schema, ct);
    }

    public async Task<List<RoutineInfo>> ListRoutinesAsync(
        string connectionName, string? nameFilter = null, string? schemaFilter = null,
        CancellationToken ct = default)
    {
        var (cfg, conn) = await OpenAsync(connectionName, ct);
        await using (conn)
            return await GetProvider(cfg).ListRoutinesAsync(conn, nameFilter, schemaFilter, ct);
    }

    public async Task<List<IndexInfo>> GetTableIndexesAsync(
        string connectionName, string tableName, string? schema = null, CancellationToken ct = default)
    {
        var (cfg, conn) = await OpenAsync(connectionName, ct);
        await using (conn)
            return await GetProvider(cfg).GetIndexesAsync(conn, tableName, schema, ct);
    }

    public async Task<QueryResult> ExecuteSqlAsync(
        string connectionName, string sql, int maxRows = 200, CancellationToken ct = default)
    {
        var (_, conn) = await OpenAsync(connectionName, ct);
        await using (conn)
        {
            logger.LogDebug("ExecuteSQL on '{Connection}': {Sql}", connectionName, sql);

            var result = new QueryResult();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.CommandTimeout = 30;

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                // Collect column names
                for (var i = 0; i < reader.FieldCount; i++)
                    result.Columns.Add(reader.GetName(i));

                var rowCount = 0;
                while (await reader.ReadAsync(ct) && rowCount < maxRows)
                {
                    var row = new QueryRow();
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

                    result.Rows.Add(row);
                    rowCount++;
                }

                result.RowsAffected = reader.RecordsAffected;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SQL execution error for connection '{Name}'", connectionName);
                result.ErrorMessage = ex.Message;
            }

            return result;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private IDbProvider GetProvider(DatabaseConfig cfg) =>
        _providers.TryGetValue(cfg.DbType, out var provider)
            ? provider
            : throw new NotSupportedException($"DbType '{cfg.DbType}' is not supported.");

    private async Task<(DatabaseConfig cfg, DbConnection conn)> OpenAsync(
        string connectionName, CancellationToken ct)
    {
        var cfg  = GetConfig(connectionName);
        var conn = CreateConnection(cfg);
        await conn.OpenAsync(ct);
        return (cfg, conn);
    }

    private DatabaseConfig GetConfig(string name)
    {
        List<DatabaseConfig> dynamic;
        lock (_lock) dynamic = [.._dynamicConfigs];

        // Dynamic connections shadow global ones with the same name.
        var cfg = dynamic.FirstOrDefault(c =>
                      string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? _globalConfigs.FirstOrDefault(c =>
                      string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        return cfg ?? throw new KeyNotFoundException(
            $"No database configuration named '{name}' was found. " +
            $"Available: {string.Join(", ", dynamic.Concat(_globalConfigs).Select(c => c.Name).Distinct())}");
    }

    private static DbConnection CreateConnection(DatabaseConfig cfg) => cfg.DbType switch
    {
        DbType.SqlServer  => new SqlConnection(cfg.ConnectionString),
        DbType.MySql      => new MySqlConnection(cfg.ConnectionString),
        DbType.PostgreSql => new NpgsqlConnection(cfg.ConnectionString),
        DbType.Sqlite     => new SqliteConnection(cfg.ConnectionString),
        DbType.Oracle     => new OracleConnection(cfg.ConnectionString),
        _                 => throw new NotSupportedException($"DbType '{cfg.DbType}' is not supported.")
    };
}
