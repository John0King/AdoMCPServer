using AdoMcpServer.Models;
using AdoMcpServer.Services;
using AdoMcpServer.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────────────────────
// Determine transport mode:
//   --stdio  (or env ADOMCP_MODE=stdio)  → stdio transport  (default)
//   --http   (or env ADOMCP_MODE=http)   → HTTP/SSE transport
// ─────────────────────────────────────────────────────────────────────────────
var modeArg = args.FirstOrDefault(a =>
    a.Equals("--stdio", StringComparison.OrdinalIgnoreCase) ||
    a.Equals("--http",  StringComparison.OrdinalIgnoreCase));

var envMode = Environment.GetEnvironmentVariable("ADOMCP_MODE");

bool isHttp = modeArg?.Equals("--http", StringComparison.OrdinalIgnoreCase) == true
    || (modeArg is null && string.Equals(envMode, "http", StringComparison.OrdinalIgnoreCase));

// ─────────────────────────────────────────────────────────────────────────────
// Builder
// ─────────────────────────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(args);

// ── Configuration ────────────────────────────────────────────────────────────
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables("ADOMCP_")
    .AddUserSecrets<Program>(optional: true);

// ── Logging ──────────────────────────────────────────────────────────────────
// In stdio mode the stdout stream is the MCP transport channel — all logs MUST
// go to stderr only so they never corrupt the JSON-RPC stream.
builder.Logging.ClearProviders();
if (isHttp)
{
    builder.Logging.AddConsole();
}
else
{
    // Redirect all log output to stderr for stdio mode.
    builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
}

// ── Database configs ──────────────────────────────────────────────────────────
builder.Services.Configure<List<DatabaseConfig>>(
    builder.Configuration.GetSection("Databases"));

// ── App services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IDatabaseService, DatabaseService>();

// ── MCP server ────────────────────────────────────────────────────────────────
var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithToolsFromAssembly(typeof(DatabaseTools).Assembly);

if (isHttp)
{
    mcpBuilder.WithHttpTransport();
}
else
{
    mcpBuilder.WithStdioServerTransport();
}

// ─────────────────────────────────────────────────────────────────────────────
// App
// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (isHttp)
{
    app.MapMcp("/mcp");
}

await app.RunAsync();
