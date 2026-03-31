using System.Data.Common;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AdoMcpServer.Services.Providers;

internal sealed class MySqlDbProvider(ILogger logger) : DbProviderBase(logger), IDbProvider
{
    public async Task<List<TableInfo>> ListTablesAsync(
        DbConnection conn, bool includeViews, string? nameFilter, string? schemaFilter, CancellationToken ct)
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
            WHERE ((@schemaFilter IS NULL AND TABLE_SCHEMA = DATABASE()) OR TABLE_SCHEMA LIKE @schemaFilter)
              AND {typeFilter}
              AND (@nameFilter IS NULL OR TABLE_NAME LIKE @nameFilter)
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;

        var param = new { nameFilter, schemaFilter };
        LogQuery(sql, param);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, param, cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = (string)r.Schema,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = string.IsNullOrWhiteSpace((string?)r.Comment) ? null : (string?)r.Comment,
        }).ToList();
    }

    public async Task<TableSchema> GetTableSchemaAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        const string tableCommentSql = """
            SELECT TABLE_COMMENT
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = COALESCE(@schema, DATABASE())
              AND TABLE_NAME = @table
            """;

        var tableParam = new { schema, table = tableName };
        LogQuery(tableCommentSql, tableParam);
        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(tableCommentSql, tableParam, cancellationToken: ct));

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

        LogQuery(colSql, tableParam);
        var cols = await conn.QueryAsync(
            new CommandDefinition(colSql, tableParam, cancellationToken: ct));

        return new TableSchema
        {
            Schema       = schema ?? string.Empty,
            TableName    = tableName,
            TableComment = string.IsNullOrWhiteSpace(tableComment) ? null : tableComment,
            Columns      = cols.Select(MapColumn).ToList(),
        };
    }

    public async Task<List<RoutineInfo>> ListRoutinesAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct)
    {
        const string sql = """
            SELECT
                ROUTINE_SCHEMA      AS `Schema`,
                ROUTINE_NAME        AS `Name`,
                ROUTINE_TYPE        AS `Type`,
                ROUTINE_DEFINITION  AS `Definition`,
                NULL                AS `Comment`
            FROM information_schema.ROUTINES
            WHERE ((@schemaFilter IS NULL AND ROUTINE_SCHEMA = DATABASE()) OR ROUTINE_SCHEMA LIKE @schemaFilter)
              AND (@nameFilter IS NULL OR ROUTINE_NAME LIKE @nameFilter)
            ORDER BY ROUTINE_SCHEMA, ROUTINE_NAME
            """;

        var param = new { nameFilter, schemaFilter };
        LogQuery(sql, param);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, param, cancellationToken: ct));
        return rows.Select(r => new RoutineInfo
        {
            Schema     = (string)r.Schema,
            Name       = (string)r.Name,
            Type       = (string)r.Type,
            Definition = r.Definition as string,
            Comment    = null,
        }).ToList();
    }

    public async Task<List<IndexInfo>> GetIndexesAsync(
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

        var param = new { schema, table = tableName };
        LogQuery(sql, param);
        return await AggregateIndexesAsync(conn, sql, param, ct);
    }
}
