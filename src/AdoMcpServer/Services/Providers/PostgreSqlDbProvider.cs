using System.Data.Common;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AdoMcpServer.Services.Providers;

internal sealed class PostgreSqlDbProvider(ILogger logger) : DbProviderBase(logger), IDbProvider
{
    public async Task<List<TableInfo>> ListDbObjectsAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct)
    {
        // UNION pg_class (tables, views, sequences) and pg_proc (functions, procedures)
        // to give a complete picture of user-defined schema objects.
        var sql = $"""
            SELECT
                n.nspname                                           AS "Schema",
                c.relname                                           AS "Name",
                CASE c.relkind
                    WHEN 'r' THEN 'TABLE'
                    WHEN 'v' THEN 'VIEW'
                    WHEN 'm' THEN 'MATERIALIZED VIEW'
                    WHEN 'S' THEN 'SEQUENCE'
                    WHEN 'p' THEN 'TABLE'
                END                                                 AS "Type",
                obj_description(c.oid, 'pg_class')                 AS "Comment"
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r','v','m','S','p')
              AND n.nspname NOT IN ('pg_catalog','information_schema')
              AND (@nameFilter IS NULL OR c.relname ILIKE @nameFilter)
              AND (@schemaFilter IS NULL OR n.nspname ILIKE @schemaFilter)

            UNION ALL

            SELECT
                n.nspname                                           AS "Schema",
                p.proname                                           AS "Name",
                CASE p.prokind
                    WHEN 'f' THEN 'FUNCTION'
                    WHEN 'p' THEN 'PROCEDURE'
                    WHEN 'a' THEN 'AGGREGATE'
                    ELSE 'FUNCTION'
                END                                                 AS "Type",
                obj_description(p.oid, 'pg_proc')                  AS "Comment"
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname NOT IN ('pg_catalog','information_schema')
              AND (@nameFilter IS NULL OR p.proname ILIKE @nameFilter)
              AND (@schemaFilter IS NULL OR n.nspname ILIKE @schemaFilter)

            ORDER BY "Schema", "Name"
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
        schema ??= "public";

        const string tableCommentSql = """
            SELECT obj_description(c.oid, 'pg_class')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """;

        var tableParam = new { schema, table = tableName };
        LogQuery(tableCommentSql, tableParam);
        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(tableCommentSql, tableParam, cancellationToken: ct));

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
              AND (@nameFilter IS NULL OR p.proname ILIKE @nameFilter)
              AND (@schemaFilter IS NULL OR n.nspname ILIKE @schemaFilter)
            ORDER BY n.nspname, p.proname
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

        var param = new { schema, table = tableName };
        LogQuery(sql, param);
        return await AggregateIndexesAsync(conn, sql, param, ct);
    }
}
