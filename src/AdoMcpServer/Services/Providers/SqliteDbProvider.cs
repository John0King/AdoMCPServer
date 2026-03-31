using System.Data.Common;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AdoMcpServer.Services.Providers;

internal sealed class SqliteDbProvider(ILogger logger) : DbProviderBase(logger), IDbProvider
{
    public async Task<List<TableInfo>> ListTablesAsync(
        DbConnection conn, bool includeViews, string? nameFilter, string? schemaFilter, CancellationToken ct)
    {
        // SQLite has no schema concept; schemaFilter is ignored.
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
              AND (@nameFilter IS NULL OR name LIKE @nameFilter)
            ORDER BY name
            """;

        var param = new { nameFilter };
        LogQuery(sql, param);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, param, cancellationToken: ct));
        return rows.Select(r => new TableInfo
        {
            Schema  = string.Empty,
            Name    = (string)r.Name,
            Type    = (string)r.Type,
            Comment = null,
        }).ToList();
    }

    public async Task<TableSchema> GetTableSchemaAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        // SQLite uses PRAGMA rather than information_schema; no schema or comments available.
        var sql = $"PRAGMA table_info(\"{tableName.Replace("\"", "\"\"")}\")";
        LogQuery(sql);
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

    public Task<List<RoutineInfo>> ListRoutinesAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct)
    {
        // SQLite does not support stored procedures or functions.
        return Task.FromResult(new List<RoutineInfo>());
    }

    public async Task<List<IndexInfo>> GetIndexesAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct)
    {
        var indexListSql = $"PRAGMA index_list(\"{tableName.Replace("\"", "\"\"")}\")";
        LogQuery(indexListSql);
        var indexList = (await conn.QueryAsync(
            new CommandDefinition(indexListSql, cancellationToken: ct))).ToList();

        var result = new List<IndexInfo>();
        foreach (var idx in indexList)
        {
            var infoSql = $"PRAGMA index_info(\"{((string)idx.name).Replace("\"", "\"\"")}\")";
            LogQuery(infoSql);
            var infoCols = await conn.QueryAsync(
                new CommandDefinition(infoSql, cancellationToken: ct));

            result.Add(new IndexInfo
            {
                IndexName    = (string)idx.name,
                IsUnique     = (long)idx.unique == 1,
                IsPrimaryKey = ((string)idx.origin) == "pk",
                Columns      = infoCols.Select(c => (string)c.name).ToList(),
            });
        }

        return result;
    }
}
