using AdoMcpServer.Models;
using AdoMcpServer.Services;
using AdoMcpServer.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────────────────────
// Transport-mode detection
//
// The idiomatic approach for MCP servers:
//   • When stdin is redirected (piped) the process is being driven by an MCP
//     client → use stdio transport.
//   • When running interactively (e.g. started from a terminal) → HTTP/SSE.
//
// Manual overrides are still honoured for scripting / CI:
//   --stdio   force stdio mode
//   --http    force HTTP mode
//   ADOMCP_MODE=stdio|http  env-var override
// ─────────────────────────────────────────────────────────────────────────────
var modeArg = args.FirstOrDefault(a =>
    a.Equals("--stdio", StringComparison.OrdinalIgnoreCase) ||
    a.Equals("--http",  StringComparison.OrdinalIgnoreCase));

// ─────────────────────────────────────────────────────────────────────────────
// Feature flags
//
//   --allow-any-sql   Enables the execute_sql MCP tool.
//                     Omit this flag to run in a read-only/safe mode where the
//                     LLM cannot execute arbitrary SQL against your databases.
// ─────────────────────────────────────────────────────────────────────────────
bool allowAnySql = args.Any(a =>
    a.Equals("--allow-any-sql", StringComparison.OrdinalIgnoreCase));

var envMode = Environment.GetEnvironmentVariable("ADOMCP_MODE");

bool isStdio =
    // Explicit flag
    modeArg?.Equals("--stdio", StringComparison.OrdinalIgnoreCase) == true
    // Env-var override
    || string.Equals(envMode, "stdio", StringComparison.OrdinalIgnoreCase)
    // Auto-detect: stdin is being piped → launched by an MCP client
    || (modeArg is null
        && !string.Equals(envMode, "http", StringComparison.OrdinalIgnoreCase)
        && Console.IsInputRedirected);

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
// In stdio mode the stdout stream is the MCP transport channel — ALL log output
// MUST go to stderr so it never corrupts the JSON-RPC message stream.
builder.Logging.ClearProviders();
if (isStdio)
{
    builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
}
else
{
    builder.Logging.AddConsole();
}

// ── Kestrel: suppress HTTP server in stdio mode ───────────────────────────────
// In stdio mode there is no HTTP traffic; prevent Kestrel from binding any port
// so we don't waste resources or conflict with other services.
if (isStdio)
{
    builder.WebHost.UseUrls(string.Empty);
}

// ── Database configs ──────────────────────────────────────────────────────────
builder.Services.Configure<List<DatabaseConfig>>(
    builder.Configuration.GetSection("Databases"));

// ── App services ──────────────────────────────────────────────────────────────
builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
builder.Services.AddSingleton(new ServerOptions { AllowAnySql = allowAnySql });

// ── MCP server ────────────────────────────────────────────────────────────────
var mcpBuilder = builder.Services
    .AddMcpServer()
    .WithToolsFromAssembly(typeof(DatabaseTools).Assembly);

if (isStdio)
{
    mcpBuilder.WithStdioServerTransport();
}
else
{
    mcpBuilder.WithHttpTransport();
}

// ─────────────────────────────────────────────────────────────────────────────
// App
// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (!isStdio)
{
    app.MapMcp("/mcp");
}

await app.RunAsync();
