using System.Data.Common;
using AdoMcpServer.Models;

namespace AdoMcpServer.Services.Providers;

/// <summary>
/// Abstracts per-engine schema-discovery and metadata operations.
/// Each database provider implements this interface so that
/// <see cref="DatabaseService"/> contains no engine-specific SQL.
/// </summary>
internal interface IDbProvider
{
    /// <summary>
    /// Lists all database objects (tables, views, stored procedures, functions, triggers,
    /// synonyms, sequences, etc.) visible to the current connection.
    /// </summary>
    Task<List<TableInfo>> ListDbObjectsAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct);

    /// <summary>Returns the full column-level schema for a single table.</summary>
    Task<TableSchema> GetTableSchemaAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct);

    /// <summary>Lists stored procedures and functions.</summary>
    Task<List<RoutineInfo>> ListRoutinesAsync(
        DbConnection conn, string? nameFilter, string? schemaFilter, CancellationToken ct);

    /// <summary>Returns all indexes defined on a single table.</summary>
    Task<List<IndexInfo>> GetIndexesAsync(
        DbConnection conn, string tableName, string? schema, CancellationToken ct);
}
