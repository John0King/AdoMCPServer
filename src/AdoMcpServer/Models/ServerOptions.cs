namespace AdoMcpServer.Models;

/// <summary>Runtime options parsed from command-line arguments at startup.</summary>
public class ServerOptions
{
    /// <summary>
    /// When <c>true</c>, the <c>execute_sql</c> MCP tool is enabled.
    /// Requires the <c>--allow-any-sql</c> startup argument.
    /// </summary>
    public bool AllowAnySql { get; init; }
}
