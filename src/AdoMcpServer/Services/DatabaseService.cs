using System.Data;
using System.Data.Common;
using System.Text;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using DbType = AdoMcpServer.Models.DbType;

namespace AdoMcpServer.Services;

public class DatabaseService(
    IOptions<List<DatabaseConfig>> options,
    ILogger<DatabaseService> logger) : IDatabaseService
{
    private readonly List<DatabaseConfig> _configs = options.Value;

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<DatabaseConfig> GetConfigurations() => _configs.AsReadOnly();

    public async Task<List<TableInfo>> ListTablesAsync(
        string connectionName, bool includeViews = true, CancellationToken ct = default)
    {
        var cfg = GetConfig(connectionName);
        await using var conn = CreateConnection(cfg);
        await conn.OpenAsync(ct);

        return cfg.DbType switch
        {
            DbType.SqlServer   => await ListTablesSqlServerAsync(conn, includeViews, ct),
            DbType.MySql       => await ListTablesMySqlAsync(conn, includeViews, ct),
            DbType.PostgreSql  => await ListTablesPostgreSqlAsync(conn, includeViews, ct),
            DbType.Sqlite      => await ListTablesSqliteAsync(conn, includeViews, ct),
            _                  => throw new NotSupportedException($"DbType '{cfg.DbType}' is not supported.")
        };
    }

    public async Task<TableSchema> GetTableSchemaAsync(
        string connectionName, string tableName, string? schema = null, CancellationToken ct = default)
    {
        var cfg = GetConfig(connectionName);
        await using var conn = CreateConnection(cfg);
        await conn.OpenAsync(ct);

        return cfg.DbType switch
        {
            DbType.SqlServer   => await GetTableSchemaSqlServerAsync(conn, tableName, schema, ct),
            DbType.MySql       => await GetTableSchemaMySqlAsync(conn, tableName, schema, ct),
            DbType.PostgreSql  => await GetTableSchemaPostgreSqlAsync(conn, tableName, schema, ct),
            DbType.Sqlite      => await GetTableSchemaSqliteAsync(conn, tableName, ct),
            _                  => throw new NotSupportedException($"DbType '{cfg.DbType}' is not supported.")
        };
    }

    public async Task<List<RoutineInfo>> ListRoutinesAsync(
        string connectionName, CancellationToken ct = default)
    {
        var cfg = GetConfig(connectionName);
        await using var conn = CreateConnection(cfg);
        await conn.OpenAsync(ct);

        return cfg.DbType switch
        {
            DbType.SqlServer   => await ListRoutinesSqlServerAsync(conn, ct),
            DbType.MySql       => await ListRoutinesMySqlAsync(conn, ct),
            DbType.PostgreSql  => await ListRoutinesPostgreSqlAsync(conn, ct),
            DbType.Sqlite      => [],   // SQLite has no stored procedures
            _                  => throw new NotSupportedException($"DbType '{cfg.DbType}' is not supported.")
        };
    }

    public async Task<List<IndexInfo>> GetTableIndexesAsync(
        string connectionName, string tableName, string? schema = null, CancellationToken ct = default)
    {
        var cfg = GetConfig(connectionName);
        await using var conn = CreateConnection(cfg);
        await conn.OpenAsync(ct);

        return cfg.DbType switch
        {
            DbType.SqlServer   => await GetIndexesSqlServerAsync(conn, tableName, schema, ct),
            DbType.MySql       => await GetIndexesMySqlAsync(conn, tableName, schema, ct),
            DbType.PostgreSql  => await GetIndexesPostgreSqlAsync(conn, tableName, schema, ct),
            DbType.Sqlite      => await GetIndexesSqliteAsync(conn, tableName, ct),
            _                  => throw new NotSupportedException($"DbType '{cfg.DbType}' is not supported.")
        };
    }

    public async Task<QueryResult> ExecuteSqlAsync(
        string connectionName, string sql, int maxRows = 200, CancellationToken ct = default)
    {
        var cfg = GetConfig(connectionName);
        await using var conn = CreateConnection(cfg);
        await conn.OpenAsync(ct);

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

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private DatabaseConfig GetConfig(string name)
    {
        var cfg = _configs.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return cfg ?? throw new KeyNotFoundException(
            $"No database configuration named '{name}' was found. " +
            $"Available: {string.Join(", ", _configs.Select(c => c.Name))}");
    }

    private static DbConnection CreateConnection(DatabaseConfig cfg) => cfg.DbType switch
    {
        DbType.SqlServer  => new SqlConnection(cfg.ConnectionString),
        DbType.MySql      => new MySqlConnection(cfg.ConnectionString),
        DbType.PostgreSql => new NpgsqlConnection(cfg.ConnectionString),
        DbType.Sqlite     => new SqliteConnection(cfg.ConnectionString),
        _                 => throw new NotSupportedException($"DbType '{cfg.DbType}' is not supported.")
    };

    // ─────────────────────────────────────────────────────────────────────────
    // SQL Server – list tables
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<TableInfo>> ListTablesSqlServerAsync(
        DbConnection conn, bool includeViews, CancellationToken ct)
    {
        var typeFilter = includeViews ? "('U','V')" : "('U')";
        var sql = $"""
            SELECT
                s.name          AS [Schema],
                t.name          AS [Name],
                CASE t.type WHEN 'U' THEN 'TABLE' ELSE 'VIEW' END AS [Type],
                ep.value        AS [Comment]
            FROM sys.objects t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.extended_properties ep
                ON ep.major_id = t.object_id
                AND ep.minor_id = 0
                AND ep.name = 'MS_Description'
                AND ep.class = 1
            WHERE t.type IN {typeFilter}
            ORDER BY s.name, t.name
            """;

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = (string)r.Schema,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = r.Comment as string,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQL Server – table schema
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<TableSchema> GetTableSchemaSqlServerAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        schema ??= "dbo";

        const string tableCommentSql = """
            SELECT ep.value
            FROM sys.objects t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            LEFT JOIN sys.extended_properties ep
                ON ep.major_id = t.object_id AND ep.minor_id = 0
                AND ep.name = 'MS_Description' AND ep.class = 1
            WHERE s.name = @schema AND t.name = @table
            """;

        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(tableCommentSql,
                new { schema, table = tableName }, cancellationToken: ct));

        const string colSql = """
            SELECT
                c.name                                              AS Name,
                tp.name                                             AS DataType,
                c.is_nullable                                       AS IsNullable,
                CAST(CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsPrimaryKey,
                OBJECT_DEFINITION(c.default_object_id)             AS DefaultValue,
                c.max_length                                        AS MaxLength,
                ep.value                                            AS Comment
            FROM sys.columns c
            JOIN sys.objects t   ON t.object_id  = c.object_id
            JOIN sys.schemas s   ON s.schema_id  = t.schema_id
            JOIN sys.types   tp  ON tp.user_type_id = c.user_type_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            LEFT JOIN sys.extended_properties ep
                ON ep.major_id = c.object_id AND ep.minor_id = c.column_id
                AND ep.name = 'MS_Description' AND ep.class = 1
            WHERE s.name = @schema AND t.name = @table
            ORDER BY c.column_id
            """;

        var cols = await conn.QueryAsync(
            new CommandDefinition(colSql,
                new { schema, table = tableName }, cancellationToken: ct));

        return new TableSchema
        {
            Schema       = schema,
            TableName    = tableName,
            TableComment = tableComment,
            Columns      = cols.Select(MapColumn).ToList(),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MySQL – list tables
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<TableInfo>> ListTablesMySqlAsync(
        DbConnection conn, bool includeViews, CancellationToken ct)
    {
        var typeFilter = includeViews
            ? "TABLE_TYPE IN ('BASE TABLE','VIEW')"
            : "TABLE_TYPE = 'BASE TABLE'";

        var sql = $"""
            SELECT
                TABLE_SCHEMA    AS `Schema`,
                TABLE_NAME      AS `Name`,
                CASE TABLE_TYPE WHEN 'BASE TABLE' THEN 'TABLE' ELSE 'VIEW' END AS `Type`,
                TABLE_COMMENT   AS `Comment`
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND {typeFilter}
            ORDER BY TABLE_NAME
            """;

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = (string)r.Schema,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = string.IsNullOrWhiteSpace((string?)r.Comment) ? null : (string?)r.Comment,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MySQL – table schema
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<TableSchema> GetTableSchemaMySqlAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        const string tableCommentSql = """
            SELECT TABLE_COMMENT
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = COALESCE(@schema, DATABASE())
              AND TABLE_NAME = @table
            """;

        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(tableCommentSql,
                new { schema, table = tableName }, cancellationToken: ct));

        const string colSql = """
            SELECT
                c.COLUMN_NAME                                   AS Name,
                c.COLUMN_TYPE                                   AS DataType,
                (c.IS_NULLABLE = 'YES')                         AS IsNullable,
                (c.COLUMN_KEY = 'PRI')                          AS IsPrimaryKey,
                c.COLUMN_DEFAULT                                AS DefaultValue,
                c.CHARACTER_MAXIMUM_LENGTH                      AS MaxLength,
                c.COLUMN_COMMENT                                AS Comment
            FROM information_schema.COLUMNS c
            WHERE c.TABLE_SCHEMA = COALESCE(@schema, DATABASE())
              AND c.TABLE_NAME   = @table
            ORDER BY c.ORDINAL_POSITION
            """;

        var cols = await conn.QueryAsync(
            new CommandDefinition(colSql,
                new { schema, table = tableName }, cancellationToken: ct));

        return new TableSchema
        {
            Schema       = schema ?? string.Empty,
            TableName    = tableName,
            TableComment = string.IsNullOrWhiteSpace(tableComment) ? null : tableComment,
            Columns      = cols.Select(MapColumn).ToList(),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PostgreSQL – list tables
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<TableInfo>> ListTablesPostgreSqlAsync(
        DbConnection conn, bool includeViews, CancellationToken ct)
    {
        var typeFilter = includeViews
            ? "c.relkind IN ('r','v','m')"
            : "c.relkind = 'r'";

        var sql = $"""
            SELECT
                n.nspname                                           AS "Schema",
                c.relname                                           AS "Name",
                CASE c.relkind
                    WHEN 'r' THEN 'TABLE'
                    WHEN 'v' THEN 'VIEW'
                    WHEN 'm' THEN 'MATERIALIZED VIEW'
                END                                                 AS "Type",
                obj_description(c.oid, 'pg_class')                 AS "Comment"
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE {typeFilter}
              AND n.nspname NOT IN ('pg_catalog','information_schema')
            ORDER BY n.nspname, c.relname
            """;

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = (string)r.Schema,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = r.Comment as string,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PostgreSQL – table schema
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<TableSchema> GetTableSchemaPostgreSqlAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        schema ??= "public";

        const string tableCommentSql = """
            SELECT obj_description(c.oid, 'pg_class')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """;

        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(tableCommentSql,
                new { schema, table = tableName }, cancellationToken: ct));

        const string colSql = """
            SELECT
                a.attname                                           AS "Name",
                format_type(a.atttypid, a.atttypmod)               AS "DataType",
                NOT a.attnotnull                                    AS "IsNullable",
                COALESCE(pk.is_pk, false)                           AS "IsPrimaryKey",
                pg_get_expr(d.adbin, d.adrelid)                    AS "DefaultValue",
                CASE WHEN a.atttypmod > 4 THEN a.atttypmod - 4 ELSE NULL END AS "MaxLength",
                col_description(a.attrelid, a.attnum)              AS "Comment"
            FROM pg_attribute a
            JOIN pg_class     c  ON c.oid = a.attrelid
            JOIN pg_namespace n  ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d
                ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN (
                SELECT i.indrelid, unnest(i.indkey) AS attnum, true AS is_pk
                FROM pg_index i WHERE i.indisprimary
            ) pk ON pk.indrelid = a.attrelid AND pk.attnum = a.attnum
            WHERE n.nspname = @schema
              AND c.relname = @table
              AND a.attnum  > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """;

        var cols = await conn.QueryAsync(
            new CommandDefinition(colSql,
                new { schema, table = tableName }, cancellationToken: ct));

        return new TableSchema
        {
            Schema       = schema,
            TableName    = tableName,
            TableComment = tableComment,
            Columns      = cols.Select(MapColumn).ToList(),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQLite – list tables
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<TableInfo>> ListTablesSqliteAsync(
        DbConnection conn, bool includeViews, CancellationToken ct)
    {
        var typeFilter = includeViews ? "type IN ('table','view')" : "type = 'table'";
        var sql = $"""
            SELECT
                '' AS Schema,
                name AS Name,
                UPPER(type) AS Type,
                NULL AS Comment
            FROM sqlite_master
            WHERE {typeFilter}
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = string.Empty,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = null,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SQLite – table schema (PRAGMA)
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<TableSchema> GetTableSchemaSqliteAsync(
        DbConnection conn, string tableName, CancellationToken ct)
    {
        var sql = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));

        var columns = rows.Select(r => new ColumnInfo
        {
            Name         = (string)r.name,
            DataType     = (string)(r.type ?? "TEXT"),
            IsNullable   = (long)r.notnull == 0,
            IsPrimaryKey = (long)r.pk > 0,
            DefaultValue = r.dflt_value as string,
            Comment      = null,
        }).ToList();

        return new TableSchema
        {
            Schema       = string.Empty,
            TableName    = tableName,
            TableComment = null,
            Columns      = columns,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Routines – SQL Server
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<RoutineInfo>> ListRoutinesSqlServerAsync(
        DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                s.name                      AS [Schema],
                o.name                      AS [Name],
                CASE o.type
                    WHEN 'P'  THEN 'PROCEDURE'
                    WHEN 'FN' THEN 'FUNCTION'
                    WHEN 'TF' THEN 'TABLE-VALUED FUNCTION'
                    ELSE o.type
                END                         AS [Type],
                OBJECT_DEFINITION(o.object_id) AS [Definition],
                ep.value                    AS [Comment]
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.extended_properties ep
                ON ep.major_id = o.object_id AND ep.minor_id = 0
                AND ep.name = 'MS_Description' AND ep.class = 1
            WHERE o.type IN ('P','FN','TF','IF')
            ORDER BY s.name, o.name
            """;

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new RoutineInfo
        {
            Schema     = (string)r.Schema,
            Name       = (string)r.Name,
            Type       = (string)r.Type,
            Definition = r.Definition as string,
            Comment    = r.Comment as string,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Routines – MySQL
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<RoutineInfo>> ListRoutinesMySqlAsync(
        DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                ROUTINE_SCHEMA      AS `Schema`,
                ROUTINE_NAME        AS `Name`,
                ROUTINE_TYPE        AS `Type`,
                ROUTINE_DEFINITION  AS `Definition`,
                NULL                AS `Comment`
            FROM information_schema.ROUTINES
            WHERE ROUTINE_SCHEMA = DATABASE()
            ORDER BY ROUTINE_NAME
            """;

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new RoutineInfo
        {
            Schema     = (string)r.Schema,
            Name       = (string)r.Name,
            Type       = (string)r.Type,
            Definition = r.Definition as string,
            Comment    = null,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Routines – PostgreSQL
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<RoutineInfo>> ListRoutinesPostgreSqlAsync(
        DbConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                n.nspname                                   AS "Schema",
                p.proname                                   AS "Name",
                CASE p.prokind
                    WHEN 'f' THEN 'FUNCTION'
                    WHEN 'p' THEN 'PROCEDURE'
                    WHEN 'a' THEN 'AGGREGATE'
                    ELSE 'FUNCTION'
                END                                         AS "Type",
                pg_get_functiondef(p.oid)                   AS "Definition",
                obj_description(p.oid, 'pg_proc')           AS "Comment"
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema')
            ORDER BY n.nspname, p.proname
            """;

        var rows = await conn.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        return rows.Select(r => new RoutineInfo
        {
            Schema     = (string)r.Schema,
            Name       = (string)r.Name,
            Type       = (string)r.Type,
            Definition = r.Definition as string,
            Comment    = r.Comment as string,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indexes – SQL Server
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<IndexInfo>> GetIndexesSqlServerAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        schema ??= "dbo";
        const string sql = """
            SELECT
                i.name          AS IndexName,
                i.is_unique     AS IsUnique,
                i.is_primary_key AS IsPrimaryKey,
                c.name          AS ColumnName
            FROM sys.indexes i
            JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c
                ON c.object_id = i.object_id AND c.column_id = ic.column_id
            JOIN sys.objects t  ON t.object_id = i.object_id
            JOIN sys.schemas s  ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table
            ORDER BY i.name, ic.key_ordinal
            """;

        return await AggregateIndexesAsync(conn, sql, new { schema, table = tableName }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indexes – MySQL
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<IndexInfo>> GetIndexesMySqlAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        const string sql = """
            SELECT
                INDEX_NAME              AS IndexName,
                (NON_UNIQUE = 0)        AS IsUnique,
                (INDEX_NAME = 'PRIMARY') AS IsPrimaryKey,
                COLUMN_NAME             AS ColumnName
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = COALESCE(@schema, DATABASE())
              AND TABLE_NAME   = @table
            ORDER BY INDEX_NAME, SEQ_IN_INDEX
            """;

        return await AggregateIndexesAsync(conn, sql, new { schema, table = tableName }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indexes – PostgreSQL
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<IndexInfo>> GetIndexesPostgreSqlAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        schema ??= "public";
        const string sql = """
            SELECT
                i.relname                   AS "IndexName",
                ix.indisunique              AS "IsUnique",
                ix.indisprimary             AS "IsPrimaryKey",
                a.attname                   AS "ColumnName"
            FROM pg_index ix
            JOIN pg_class t  ON t.oid = ix.indrelid
            JOIN pg_class i  ON i.oid = ix.indexrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_attribute a
                ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
            WHERE n.nspname = @schema AND t.relname = @table
            ORDER BY i.relname, a.attnum
            """;

        return await AggregateIndexesAsync(conn, sql, new { schema, table = tableName }, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Indexes – SQLite
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<IndexInfo>> GetIndexesSqliteAsync(
        DbConnection conn, string tableName, CancellationToken ct)
    {
        var indexListSql = $"PRAGMA index_list(\"{tableName.Replace("\"", "\"\"")}\")";
        var indexList = (await conn.QueryAsync(
            new CommandDefinition(indexListSql, cancellationToken: ct))).ToList();

        var result = new List<IndexInfo>();
        foreach (var idx in indexList)
        {
            var infoSql = $"PRAGMA index_info(\"{((string)idx.name).Replace("\"", "\"\"")}\")";
            var infoCols = await conn.QueryAsync(
                new CommandDefinition(infoSql, cancellationToken: ct));

            result.Add(new IndexInfo
            {
                IndexName  = (string)idx.name,
                IsUnique   = (long)idx.unique == 1,
                IsPrimaryKey = ((string)idx.origin) == "pk",
                Columns    = infoCols.Select(c => (string)c.name).ToList(),
            });
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<List<IndexInfo>> AggregateIndexesAsync(
        DbConnection conn, string sql, object param, CancellationToken ct)
    {
        var rows = await conn.QueryAsync(new CommandDefinition(sql, param, cancellationToken: ct));

        return rows
            .GroupBy(r => new { IndexName = (string)r.IndexName })
            .Select(g =>
            {
                var first = g.First();
                return new IndexInfo
                {
                    IndexName    = g.Key.IndexName,
                    IsUnique     = (bool)first.IsUnique,
                    IsPrimaryKey = (bool)first.IsPrimaryKey,
                    Columns      = g.Select(r => (string)r.ColumnName).ToList(),
                };
            })
            .ToList();
    }

    private static ColumnInfo MapColumn(dynamic r) => new()
    {
        Name         = (string)r.Name,
        DataType     = (string)r.DataType,
        IsNullable   = (bool)r.IsNullable,
        IsPrimaryKey = (bool)r.IsPrimaryKey,
        DefaultValue = r.DefaultValue as string,
        MaxLength    = r.MaxLength is long l ? (int?)l
                     : r.MaxLength is int  i ? (int?)i
                     : null,
        Comment      = r.Comment as string,
    };
}
