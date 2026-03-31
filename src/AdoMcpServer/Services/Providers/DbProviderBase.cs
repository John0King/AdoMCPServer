using System.Reflection;
using AdoMcpServer.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AdoMcpServer.Services.Providers;

/// <summary>Shared helpers used by all provider implementations.</summary>
internal abstract class DbProviderBase(ILogger logger)
{
    protected readonly ILogger Logger = logger;

    // ─────────────────────────────────────────────────────────────────────────
    // SQL logging
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Logs the SQL statement and its parameters at <see cref="LogLevel.Debug"/> level.</summary>
    protected void LogQuery(string sql, object? parameters = null)
    {
        if (!Logger.IsEnabled(LogLevel.Debug)) return;
        Logger.LogDebug("Executing SQL:\n{Sql}\nParameters: {Params}", sql, FormatParams(parameters));
    }

    private static string FormatParams(object? parameters)
    {
        if (parameters is null) return "(none)";
        try
        {
            var props = parameters.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance);
            return props.Length == 0
                ? "(no properties)"
                : string.Join(", ", props.Select(p => $"{p.Name}={FormatValue(p.GetValue(parameters))}"));
        }
        catch
        {
            return "(could not format)";
        }
    }

    private static string FormatValue(object? value) =>
        value is null ? "NULL" : $"'{value}'";

    // ─────────────────────────────────────────────────────────────────────────
    // Shared helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Groups flat index-column rows (IndexName, IsUnique, IsPrimaryKey, ColumnName)
    /// into <see cref="IndexInfo"/> objects.
    /// </summary>
    protected static async Task<List<IndexInfo>> AggregateIndexesAsync(
        System.Data.Common.DbConnection conn, string sql, object param, CancellationToken ct)
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

    /// <summary>Maps a Dapper dynamic row (Name, DataType, IsNullable, …) to a <see cref="ColumnInfo"/>.</summary>
    protected static ColumnInfo MapColumn(dynamic r) => new()
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
