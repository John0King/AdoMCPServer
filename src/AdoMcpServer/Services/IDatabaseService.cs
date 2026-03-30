using AdoMcpServer.Models;

namespace AdoMcpServer.Services;

/// <summary>Provides database metadata and query execution capabilities.</summary>
public interface IDatabaseService
{
    /// <summary>Lists all configured database connections (pre-configured + dynamically added).</summary>
    IReadOnlyList<DatabaseConfig> GetConfigurations();

    /// <summary>
    /// Adds (or replaces) a named connection at runtime. If <paramref name="testFirst"/> is true,
    /// the connection is opened briefly to verify the credentials before storing.
    /// Returns <c>null</c> on success, or an error message on failure.
    /// </summary>
    Task<string?> AddConnectionAsync(DatabaseConfig config, bool testFirst = true, CancellationToken ct = default);

    /// <summary>
    /// Removes a dynamically-added connection by name.
    /// Pre-configured connections from appsettings.json cannot be removed.
    /// </summary>
    bool RemoveConnection(string name);

    /// <summary>Lists tables (and optionally views) in the target database, including their comments.</summary>
    Task<List<TableInfo>> ListTablesAsync(string connectionName, bool includeViews = true, string? nameFilter = null, CancellationToken ct = default);

    /// <summary>Returns the full schema of a table, including column types and comments.</summary>
    Task<TableSchema> GetTableSchemaAsync(string connectionName, string tableName, string? schema = null, CancellationToken ct = default);

    /// <summary>Lists stored procedures and functions in the target database.</summary>
    Task<List<RoutineInfo>> ListRoutinesAsync(string connectionName, string? nameFilter = null, CancellationToken ct = default);

    /// <summary>Lists indexes defined on a specific table.</summary>
    Task<List<IndexInfo>> GetTableIndexesAsync(string connectionName, string tableName, string? schema = null, CancellationToken ct = default);

    /// <summary>Executes a SQL statement and returns the result set (SELECT) or rows-affected count (DML).</summary>
    Task<QueryResult> ExecuteSqlAsync(string connectionName, string sql, int maxRows = 200, CancellationToken ct = default);
}
