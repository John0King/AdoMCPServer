using AdoMcpServer.Models;

namespace AdoMcpServer.Services;

/// <summary>Provides database metadata and query execution capabilities.</summary>
public interface IDatabaseService
{
    /// <summary>
    /// Lists all configured database connections visible to the given session
    /// (session-local dynamically-added connections + global pre-configured ones).
    /// </summary>
    /// <param name="sessionKey">
    /// The MCP session identifier (<see cref="ModelContextProtocol.McpSession.SessionId"/>).
    /// Use an empty string for stdio / single-session mode.
    /// </param>
    IReadOnlyList<DatabaseConfig> GetConfigurations(string sessionKey);

    /// <summary>
    /// Adds (or replaces) a named connection in the specified session's store at runtime.
    /// If <paramref name="testFirst"/> is true, the connection is opened briefly to verify
    /// the credentials before storing.
    /// Returns <c>null</c> on success, or an error message on failure.
    /// </summary>
    /// <param name="sessionKey">The MCP session identifier; empty string for stdio mode.</param>
    Task<string?> AddConnectionAsync(DatabaseConfig config, string sessionKey, bool testFirst = true, CancellationToken ct = default);

    /// <summary>
    /// Removes a dynamically-added (session-local) connection by name.
    /// Pre-configured connections from appsettings.json cannot be removed.
    /// </summary>
    /// <param name="sessionKey">The MCP session identifier; empty string for stdio mode.</param>
    bool RemoveConnection(string name, string sessionKey);

    /// <summary>Lists tables (and optionally views) in the target database, including their comments.</summary>
    /// <param name="sessionKey">The MCP session identifier; empty string for stdio mode.</param>
    Task<List<TableInfo>> ListTablesAsync(string connectionName, string sessionKey, bool includeViews = true, string? nameFilter = null, CancellationToken ct = default);

    /// <summary>Returns the full schema of a table, including column types and comments.</summary>
    /// <param name="sessionKey">The MCP session identifier; empty string for stdio mode.</param>
    Task<TableSchema> GetTableSchemaAsync(string connectionName, string sessionKey, string tableName, string? schema = null, CancellationToken ct = default);

    /// <summary>Lists stored procedures and functions in the target database.</summary>
    /// <param name="sessionKey">The MCP session identifier; empty string for stdio mode.</param>
    Task<List<RoutineInfo>> ListRoutinesAsync(string connectionName, string sessionKey, string? nameFilter = null, CancellationToken ct = default);

    /// <summary>Lists indexes defined on a specific table.</summary>
    /// <param name="sessionKey">The MCP session identifier; empty string for stdio mode.</param>
    Task<List<IndexInfo>> GetTableIndexesAsync(string connectionName, string sessionKey, string tableName, string? schema = null, CancellationToken ct = default);

    /// <summary>Executes a SQL statement and returns the result set (SELECT) or rows-affected count (DML).</summary>
    /// <param name="sessionKey">The MCP session identifier; empty string for stdio mode.</param>
    Task<QueryResult> ExecuteSqlAsync(string connectionName, string sessionKey, string sql, int maxRows = 200, CancellationToken ct = default);
}
