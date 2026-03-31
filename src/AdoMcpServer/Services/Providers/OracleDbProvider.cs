using System.Data.Common;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AdoMcpServer.Services.Providers;

/// <summary>
/// Oracle database provider.
/// <para>
/// Key implementation notes:
/// <list type="bullet">
///   <item>Oracle ODP.NET defaults to positional parameter binding.  To avoid
///         binding mismatches, every named parameter must appear <em>exactly once</em>
///         in each SQL statement, and anonymous-object properties must be declared
///         in the same order the corresponding placeholders appear in the SQL.</item>
///   <item>Nullable filter parameters use <c>NVL(:param, '%')</c> so the placeholder
///         appears once while still matching everything when the value is <c>NULL</c>.</item>
///   <item>The primary-key sub-query in <c>GetTableSchemaAsync</c> is restructured to
///         include <c>TABLE_NAME</c>/<c>OWNER</c> in its result set, eliminating the
///         need to repeat <c>:tableName</c>/<c>:ownerName</c> inside the sub-query.</item>
///   <item>When no schema is supplied, USER_* views are used (no special privileges
///         required) and no OWNER filter is applied at all.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class OracleDbProvider(ILogger logger) : DbProviderBase(logger), IDbProvider
{
    // ─────────────────────────────────────────────────────────────────────────
    // list all DB objects
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<TableInfo>> ListDbObjectsAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct)
    {
        // Oracle positional binding: each named parameter must appear exactly once per statement.
        // NVL(:nameFilter, '%') means "match everything when nameFilter is NULL".
        // When schemaFilter is null: return every object visible through ALL_OBJECTS
        //   (no owner restriction at all) so the caller sees the full picture.
        // When schemaFilter is specified: filter ALL_OBJECTS by owner using a LIKE pattern.
        // In both cases o.OWNER is returned as-is for the Schema column.
        string sql;
        object paramObj;

        if (schemaFilter is null)
        {
            sql = """
                SELECT
                    o.OWNER             AS "Schema",
                    o.OBJECT_NAME       AS "Name",
                    o.OBJECT_TYPE       AS "Type",
                    c.COMMENTS          AS "Comment"
                FROM ALL_OBJECTS o
                LEFT JOIN ALL_TAB_COMMENTS c
                    ON c.OWNER = o.OWNER
                    AND c.TABLE_NAME = o.OBJECT_NAME
                    AND c.TABLE_TYPE = o.OBJECT_TYPE
                WHERE o.OBJECT_TYPE IN (
                          'TABLE','VIEW','PROCEDURE','FUNCTION',
                          'PACKAGE','TRIGGER','SEQUENCE','SYNONYM')
                  AND UPPER(o.OBJECT_NAME) LIKE UPPER(NVL(:nameFilter, '%'))
                ORDER BY o.OWNER, o.OBJECT_TYPE, o.OBJECT_NAME
                """;
            paramObj = new { nameFilter };
        }
        else
        {
            sql = """
                SELECT
                    o.OWNER             AS "Schema",
                    o.OBJECT_NAME       AS "Name",
                    o.OBJECT_TYPE       AS "Type",
                    c.COMMENTS          AS "Comment"
                FROM ALL_OBJECTS o
                LEFT JOIN ALL_TAB_COMMENTS c
                    ON c.OWNER = o.OWNER
                    AND c.TABLE_NAME = o.OBJECT_NAME
                    AND c.TABLE_TYPE = o.OBJECT_TYPE
                WHERE o.OBJECT_TYPE IN (
                          'TABLE','VIEW','PROCEDURE','FUNCTION',
                          'PACKAGE','TRIGGER','SEQUENCE','SYNONYM')
                  AND UPPER(o.OWNER) LIKE UPPER(:schemaFilter)
                  AND UPPER(o.OBJECT_NAME) LIKE UPPER(NVL(:nameFilter, '%'))
                ORDER BY o.OWNER, o.OBJECT_TYPE, o.OBJECT_NAME
                """;
            // Properties in SQL-appearance order: schemaFilter first, nameFilter second.
            paramObj = new { schemaFilter, nameFilter };
        }

        LogQuery(sql, paramObj);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, paramObj, cancellationToken: ct));
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

            // The PK sub-query includes TABLE_NAME in its SELECT so that :tableName
            // is referenced only once (in the outer WHERE), not twice.
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
                    ON cc.TABLE_NAME  = col.TABLE_NAME
                    AND cc.COLUMN_NAME = col.COLUMN_NAME
                LEFT JOIN (
                    SELECT acc.TABLE_NAME, acc.COLUMN_NAME
                    FROM USER_CONSTRAINTS ac
                    JOIN USER_CONS_COLUMNS acc ON acc.CONSTRAINT_NAME = ac.CONSTRAINT_NAME
                    WHERE ac.CONSTRAINT_TYPE = 'P'
                ) pk ON pk.TABLE_NAME = col.TABLE_NAME
                       AND pk.COLUMN_NAME = col.COLUMN_NAME
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
                  AND OWNER      = :ownerName
                """;

            // Again the PK sub-query includes OWNER/TABLE_NAME to avoid repeating
            // the bind parameters inside the sub-query.
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
                    ON cc.OWNER        = col.OWNER
                    AND cc.TABLE_NAME  = col.TABLE_NAME
                    AND cc.COLUMN_NAME = col.COLUMN_NAME
                LEFT JOIN (
                    SELECT acc.OWNER, acc.TABLE_NAME, acc.COLUMN_NAME
                    FROM ALL_CONSTRAINTS ac
                    JOIN ALL_CONS_COLUMNS acc
                        ON acc.OWNER           = ac.OWNER
                        AND acc.CONSTRAINT_NAME = ac.CONSTRAINT_NAME
                    WHERE ac.CONSTRAINT_TYPE = 'P'
                ) pk ON pk.OWNER       = col.OWNER
                       AND pk.TABLE_NAME  = col.TABLE_NAME
                       AND pk.COLUMN_NAME = col.COLUMN_NAME
                WHERE col.TABLE_NAME = :tableName
                  AND col.OWNER      = :ownerName
                ORDER BY col.COLUMN_ID
                """;

            // Properties in SQL-appearance order: tableName first, ownerName second.
            paramObj = new { tableName = tableUpper, ownerName = ownerUpper };
        }

        LogQuery(tableCommentSql, paramObj);
        var tableComment = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(tableCommentSql, paramObj, cancellationToken: ct));

        LogQuery(colSql, paramObj);
        var cols = await conn.QueryAsync(
            new CommandDefinition(colSql, paramObj, cancellationToken: ct));

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
                  AND UPPER(p.OBJECT_NAME) LIKE UPPER(NVL(:nameFilter, '%'))
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
                WHERE UPPER(p.OWNER)       LIKE UPPER(:schemaFilter)
                  AND UPPER(p.OBJECT_NAME) LIKE UPPER(NVL(:nameFilter, '%'))
                  AND p.OBJECT_TYPE IN ('PROCEDURE','FUNCTION','PACKAGE')
                ORDER BY p.OWNER, p.OBJECT_NAME
                """;
            // Properties in SQL-appearance order: schemaFilter first, nameFilter second.
            paramObj = new { schemaFilter, nameFilter };
        }

        LogQuery(sql, paramObj);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, paramObj, cancellationToken: ct));
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
                WHERE ic.TABLE_NAME  = :tableName
                  AND ic.TABLE_OWNER = :ownerName
                ORDER BY ic.INDEX_NAME, ic.COLUMN_POSITION
                """;
            // Properties in SQL-appearance order: tableName first, ownerName second.
            paramObj = new { tableName = tableUpper, ownerName = ownerUpper };
        }

        LogQuery(sql, paramObj);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, paramObj, cancellationToken: ct));

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
