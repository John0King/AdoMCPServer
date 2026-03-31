using System.Data;
using System.Data.Common;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;

namespace AdoMcpServer.Services.Providers;

/// <summary>
/// Oracle database provider.
/// <para>
/// Key implementation notes:
/// <list type="bullet">
///   <item>All queries use <see cref="OracleDynamicParameters"/> so that
///         <see cref="OracleCommand.BindByName"/> is set to <c>true</c>.
///         Oracle ODP.NET defaults to positional binding; when the same named
///         parameter appears more than once in a SQL statement, positional mode
///         requires a separate parameter object for each occurrence, which causes
///         <c>ORA-01745</c>.  Named binding avoids the problem entirely.</item>
///   <item>Parameters are never named after Oracle reserved words.
///         <c>:table</c> and <c>:schema</c> are renamed to <c>:tableName</c>
///         and <c>:ownerName</c> to avoid potential parse errors.</item>
///   <item>When no schema is supplied the USER_* views are used so that the
///         query never requires special DBA privileges and no owner filter is
///         applied at all – matching the behaviour of
///         <c>ListTablesAsync</c> / <c>ListRoutinesAsync</c>.
///         <c>COALESCE(:schema, SYS_CONTEXT(...))</c> is NOT used because it
///         still adds an owner condition; the correct behaviour is to omit the
///         condition when the caller did not specify a schema.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class OracleDbProvider(ILogger logger) : DbProviderBase(logger), IDbProvider
{
    // ─────────────────────────────────────────────────────────────────────────
    // OracleDynamicParameters – sets BindByName = true before Dapper binds
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps <see cref="DynamicParameters"/> and sets <see cref="OracleCommand.BindByName"/>
    /// to <c>true</c> before parameters are bound.  This allows the same named
    /// parameter (e.g. <c>:tableName</c>) to appear multiple times in a single
    /// SQL statement without requiring duplicate parameter objects.
    /// </summary>
    private sealed class OracleDynamicParameters(object template) : SqlMapper.IDynamicParameters
    {
        private readonly DynamicParameters _inner = new(template);

        public void AddParameters(IDbCommand command, SqlMapper.Identity identity)
        {
            if (command is OracleCommand oracleCmd)
                oracleCmd.BindByName = true;
            ((SqlMapper.IDynamicParameters)_inner).AddParameters(command, identity);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // list tables
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<TableInfo>> ListTablesAsync(
        DbConnection conn, bool includeViews, string? nameFilter, string? schemaFilter, CancellationToken ct)
    {
        // When no schema filter is given, use USER_* views (no special privileges required).
        // When a schema (owner) is specified, fall back to ALL_* views.
        string sql;
        object paramObj;

        if (schemaFilter is null)
        {
            var typeFilter = includeViews ? "o.OBJECT_TYPE IN ('TABLE','VIEW')" : "o.OBJECT_TYPE = 'TABLE'";
            sql = $"""
                SELECT
                    USER                AS "Schema",
                    o.OBJECT_NAME       AS "Name",
                    o.OBJECT_TYPE       AS "Type",
                    c.COMMENTS          AS "Comment"
                FROM USER_OBJECTS o
                LEFT JOIN USER_TAB_COMMENTS c ON c.TABLE_NAME = o.OBJECT_NAME
                WHERE {typeFilter}
                  AND (:nameFilter IS NULL OR UPPER(o.OBJECT_NAME) LIKE UPPER(:nameFilter))
                ORDER BY o.OBJECT_NAME
                """;
            paramObj = new { nameFilter };
        }
        else
        {
            var typeFilter = includeViews ? "o.OBJECT_TYPE IN ('TABLE','VIEW')" : "o.OBJECT_TYPE = 'TABLE'";
            sql = $"""
                SELECT
                    o.OWNER             AS "Schema",
                    o.OBJECT_NAME       AS "Name",
                    o.OBJECT_TYPE       AS "Type",
                    c.COMMENTS          AS "Comment"
                FROM ALL_OBJECTS o
                LEFT JOIN ALL_TAB_COMMENTS c
                    ON c.OWNER = o.OWNER AND c.TABLE_NAME = o.OBJECT_NAME
                WHERE {typeFilter}
                  AND UPPER(o.OWNER) LIKE UPPER(:schemaFilter)
                  AND (:nameFilter IS NULL OR UPPER(o.OBJECT_NAME) LIKE UPPER(:nameFilter))
                ORDER BY o.OWNER, o.OBJECT_NAME
                """;
            paramObj = new { nameFilter, schemaFilter };
        }

        LogQuery(sql, paramObj);
        var rows = await conn.QueryAsync(
            new CommandDefinition(sql, new OracleDynamicParameters(paramObj), cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = (string)r.Schema,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = r.Comment as string,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // table schema
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<TableSchema> GetTableSchemaAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        // Oracle data-dictionary stores names in UPPERCASE by default.
        var tableUpper = tableName.ToUpperInvariant();
        var ownerUpper = schema?.ToUpperInvariant();

        string tableCommentSql;
        string colSql;
        object paramObj;

        if (ownerUpper is null)
        {
            // No schema supplied → query USER_* views (current user's objects only).
            tableCommentSql = """
                SELECT COMMENTS
                FROM USER_TAB_COMMENTS
                WHERE TABLE_NAME = :tableName
                """;

            colSql = """
                SELECT
                    col.COLUMN_NAME                                         AS "Name",
                    col.DATA_TYPE                                           AS "DataType",
                    CASE col.NULLABLE WHEN 'Y' THEN 1 ELSE 0 END           AS "IsNullable",
                    CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END  AS "IsPrimaryKey",
                    col.DATA_DEFAULT                                        AS "DefaultValue",
                    col.CHAR_LENGTH                                         AS "MaxLength",
                    cc.COMMENTS                                             AS "Comment"
                FROM USER_TAB_COLUMNS col
                LEFT JOIN USER_COL_COMMENTS cc
                    ON cc.TABLE_NAME = col.TABLE_NAME
                    AND cc.COLUMN_NAME = col.COLUMN_NAME
                LEFT JOIN (
                    SELECT acc.COLUMN_NAME
                    FROM USER_CONSTRAINTS ac
                    JOIN USER_CONS_COLUMNS acc
                        ON acc.CONSTRAINT_NAME = ac.CONSTRAINT_NAME
                    WHERE ac.CONSTRAINT_TYPE = 'P'
                      AND ac.TABLE_NAME = :tableName
                ) pk ON pk.COLUMN_NAME = col.COLUMN_NAME
                WHERE col.TABLE_NAME = :tableName
                ORDER BY col.COLUMN_ID
                """;

            paramObj = new { tableName = tableUpper };
        }
        else
        {
            // Schema supplied → query ALL_* views (accessible objects across owners).
            tableCommentSql = """
                SELECT COMMENTS
                FROM ALL_TAB_COMMENTS
                WHERE TABLE_NAME = :tableName
                  AND OWNER = :ownerName
                """;

            colSql = """
                SELECT
                    col.COLUMN_NAME                                         AS "Name",
                    col.DATA_TYPE                                           AS "DataType",
                    CASE col.NULLABLE WHEN 'Y' THEN 1 ELSE 0 END           AS "IsNullable",
                    CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END  AS "IsPrimaryKey",
                    col.DATA_DEFAULT                                        AS "DefaultValue",
                    col.CHAR_LENGTH                                         AS "MaxLength",
                    cc.COMMENTS                                             AS "Comment"
                FROM ALL_TAB_COLUMNS col
                LEFT JOIN ALL_COL_COMMENTS cc
                    ON cc.OWNER = col.OWNER
                    AND cc.TABLE_NAME = col.TABLE_NAME
                    AND cc.COLUMN_NAME = col.COLUMN_NAME
                LEFT JOIN (
                    SELECT acc.COLUMN_NAME
                    FROM ALL_CONSTRAINTS ac
                    JOIN ALL_CONS_COLUMNS acc
                        ON acc.OWNER = ac.OWNER
                        AND acc.CONSTRAINT_NAME = ac.CONSTRAINT_NAME
                    WHERE ac.CONSTRAINT_TYPE = 'P'
                      AND ac.TABLE_NAME = :tableName
                      AND ac.OWNER = :ownerName
                ) pk ON pk.COLUMN_NAME = col.COLUMN_NAME
                WHERE col.TABLE_NAME = :tableName
                  AND col.OWNER = :ownerName
                ORDER BY col.COLUMN_ID
                """;

            paramObj = new { tableName = tableUpper, ownerName = ownerUpper };
        }

        LogQuery(tableCommentSql, paramObj);
        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                tableCommentSql,
                new OracleDynamicParameters(paramObj),
                cancellationToken: ct));

        LogQuery(colSql, paramObj);
        var cols = await conn.QueryAsync(
            new CommandDefinition(
                colSql,
                new OracleDynamicParameters(paramObj),
                cancellationToken: ct));

        return new TableSchema
        {
            Schema       = schema ?? string.Empty,
            TableName    = tableName,
            TableComment = tableComment,
            Columns      = cols.Select(r => new ColumnInfo
            {
                Name         = (string)r.Name,
                DataType     = (string)r.DataType,
                IsNullable   = (int)r.IsNullable == 1,
                IsPrimaryKey = (int)r.IsPrimaryKey == 1,
                DefaultValue = r.DefaultValue as string,
                MaxLength    = r.MaxLength is decimal d && d > 0 ? (int?)Convert.ToInt32(d) : null,
                Comment      = r.Comment as string,
            }).ToList(),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // list routines
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<RoutineInfo>> ListRoutinesAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct)
    {
        string sql;
        object paramObj;

        if (schemaFilter is null)
        {
            sql = """
                SELECT
                    USER            AS "Schema",
                    p.OBJECT_NAME   AS "Name",
                    p.OBJECT_TYPE   AS "Type",
                    NULL            AS "Comment"
                FROM USER_PROCEDURES p
                WHERE p.OBJECT_TYPE IN ('PROCEDURE','FUNCTION','PACKAGE')
                  AND (:nameFilter IS NULL OR UPPER(p.OBJECT_NAME) LIKE UPPER(:nameFilter))
                ORDER BY p.OBJECT_NAME
                """;
            paramObj = new { nameFilter };
        }
        else
        {
            sql = """
                SELECT
                    p.OWNER         AS "Schema",
                    p.OBJECT_NAME   AS "Name",
                    p.OBJECT_TYPE   AS "Type",
                    NULL            AS "Comment"
                FROM ALL_PROCEDURES p
                WHERE UPPER(p.OWNER) LIKE UPPER(:schemaFilter)
                  AND p.OBJECT_TYPE IN ('PROCEDURE','FUNCTION','PACKAGE')
                  AND (:nameFilter IS NULL OR UPPER(p.OBJECT_NAME) LIKE UPPER(:nameFilter))
                ORDER BY p.OWNER, p.OBJECT_NAME
                """;
            paramObj = new { nameFilter, schemaFilter };
        }

        LogQuery(sql, paramObj);
        var rows = await conn.QueryAsync(
            new CommandDefinition(sql, new OracleDynamicParameters(paramObj), cancellationToken: ct));
        return rows.Select(r => new RoutineInfo
        {
            Schema     = (string)r.Schema,
            Name       = (string)r.Name,
            Type       = (string)r.Type,
            Definition = null,  // Oracle source retrieval requires per-object calls; omit for brevity
            Comment    = null,
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // indexes
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<IndexInfo>> GetIndexesAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        var tableUpper = tableName.ToUpperInvariant();
        var ownerUpper = schema?.ToUpperInvariant();

        string sql;
        object paramObj;

        if (ownerUpper is null)
        {
            // No schema supplied → use USER_* views (current user's objects only).
            sql = """
                SELECT
                    ic.INDEX_NAME                                           AS "IndexName",
                    CASE i.UNIQUENESS WHEN 'UNIQUE' THEN 1 ELSE 0 END      AS "IsUnique",
                    CASE WHEN c.CONSTRAINT_TYPE = 'P' THEN 1 ELSE 0 END    AS "IsPrimaryKey",
                    ic.COLUMN_NAME                                          AS "ColumnName"
                FROM USER_IND_COLUMNS ic
                JOIN USER_INDEXES i ON i.INDEX_NAME = ic.INDEX_NAME
                LEFT JOIN USER_CONSTRAINTS c
                    ON c.INDEX_NAME = i.INDEX_NAME AND c.CONSTRAINT_TYPE = 'P'
                WHERE ic.TABLE_NAME = :tableName
                ORDER BY ic.INDEX_NAME, ic.COLUMN_POSITION
                """;
            paramObj = new { tableName = tableUpper };
        }
        else
        {
            // Schema supplied → use ALL_* views.
            sql = """
                SELECT
                    ic.INDEX_NAME                                           AS "IndexName",
                    CASE i.UNIQUENESS WHEN 'UNIQUE' THEN 1 ELSE 0 END      AS "IsUnique",
                    CASE WHEN c.CONSTRAINT_TYPE = 'P' THEN 1 ELSE 0 END    AS "IsPrimaryKey",
                    ic.COLUMN_NAME                                          AS "ColumnName"
                FROM ALL_IND_COLUMNS ic
                JOIN ALL_INDEXES i
                    ON i.OWNER = ic.INDEX_OWNER AND i.INDEX_NAME = ic.INDEX_NAME
                LEFT JOIN ALL_CONSTRAINTS c
                    ON c.OWNER = i.OWNER AND c.INDEX_NAME = i.INDEX_NAME
                    AND c.CONSTRAINT_TYPE = 'P'
                WHERE ic.TABLE_NAME = :tableName
                  AND ic.TABLE_OWNER = :ownerName
                ORDER BY ic.INDEX_NAME, ic.COLUMN_POSITION
                """;
            paramObj = new { tableName = tableUpper, ownerName = ownerUpper };
        }

        LogQuery(sql, paramObj);
        var rows = await conn.QueryAsync(
            new CommandDefinition(sql, new OracleDynamicParameters(paramObj), cancellationToken: ct));

        return rows
            .GroupBy(r => new { IndexName = (string)r.IndexName })
            .Select(g =>
            {
                var first = g.First();
                return new IndexInfo
                {
                    IndexName    = g.Key.IndexName,
                    IsUnique     = (int)first.IsUnique == 1,
                    IsPrimaryKey = (int)first.IsPrimaryKey == 1,
                    Columns      = g.Select(r => (string)r.ColumnName).ToList(),
                };
            })
            .ToList();
    }
}
