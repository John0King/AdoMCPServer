using System.Data.Common;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AdoMcpServer.Services.Providers;

internal sealed class SqlServerDbProvider(ILogger logger) : DbProviderBase(logger), IDbProvider
{
    public async Task<List<TableInfo>> ListTablesAsync(
        DbConnection conn, bool includeViews, string? nameFilter, string? schemaFilter, CancellationToken ct)
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
              AND (@nameFilter IS NULL OR t.name LIKE @nameFilter)
              AND (@schemaFilter IS NULL OR s.name LIKE @schemaFilter)
            ORDER BY s.name, t.name
            """;

        var param = new { nameFilter, schemaFilter };
        LogQuery(sql, param);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, param, cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = (string)r.Schema,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = r.Comment as string,
        }).ToList();
    }

    public async Task<TableSchema> GetTableSchemaAsync(
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

        var tableParam = new { schema, table = tableName };
        LogQuery(tableCommentSql, tableParam);
        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(tableCommentSql, tableParam, cancellationToken: ct));

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

        LogQuery(colSql, tableParam);
        var cols = await conn.QueryAsync(
            new CommandDefinition(colSql, tableParam, cancellationToken: ct));

        return new TableSchema
        {
            Schema       = schema,
            TableName    = tableName,
            TableComment = tableComment,
            Columns      = cols.Select(MapColumn).ToList(),
        };
    }

    public async Task<List<RoutineInfo>> ListRoutinesAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct)
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
              AND (@nameFilter IS NULL OR o.name LIKE @nameFilter)
              AND (@schemaFilter IS NULL OR s.name LIKE @schemaFilter)
            ORDER BY s.name, o.name
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
            Comment    = r.Comment as string,
        }).ToList();
    }

    public async Task<List<IndexInfo>> GetIndexesAsync(
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

        var param = new { schema, table = tableName };
        LogQuery(sql, param);
        return await AggregateIndexesAsync(conn, sql, param, ct);
    }
}
