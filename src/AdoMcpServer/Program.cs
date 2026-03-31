using System.CommandLine;
using AdoMcpServer.Models;
using AdoMcpServer.Services;
using AdoMcpServer.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────────────────────
// CLI definition (System.CommandLine)
// ─────────────────────────────────────────────────────────────────────────────

var httpOption = new Option<bool>("--http")
{
    Description = "Start in HTTP/SSE mode instead of the default stdio mode.",
};

// --stdio is accepted for backwards-compatibility (it is the default; this flag
// is a no-op but avoids errors when MCP clients pass it explicitly).
var stdioOption = new Option<bool>("--stdio")
{
    Description = "Start in stdio mode (default; accepted for compatibility).",
};

var allowAnySqlOption = new Option<bool>("--allow-any-sql")
{
    Description = "Enable the execute_sql MCP tool. Omit for read-only/safe mode.",
};

var rootCommand = new RootCommand(
    "AdoMCP Server — MCP server for database schema discovery and SQL execution.")
{
    httpOption,
    stdioOption,
    allowAnySqlOption,
};

// Allow unrecognised tokens to pass through to the ASP.NET Core host so that
// standard host arguments such as --urls or --environment still work.
rootCommand.TreatUnmatchedTokensAsErrors = false;

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
{
    // HTTP mode when --http is given or when the env-var says so.
    bool isHttp = parseResult.GetValue(httpOption)
        || string.Equals(
               Environment.GetEnvironmentVariable("ADOMCP_MODE"),
               "http",
               StringComparison.OrdinalIgnoreCase);

    bool allowAnySql = parseResult.GetValue(allowAnySqlOption);
    bool isStdio = !isHttp;

    // Forward any unrecognised tokens to ASP.NET Core (e.g. --urls, --environment).
    var aspNetArgs = parseResult.UnmatchedTokens.ToArray();

    // ─────────────────────────────────────────────────────────────────────────
    // Builder
    // ─────────────────────────────────────────────────────────────────────────
    var builder = WebApplication.CreateBuilder(aspNetArgs);

    // ── Configuration ─────────────────────────────────────────────────────────
    builder.Configuration
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
        .AddEnvironmentVariables("ADOMCP_")
        .AddUserSecrets<Program>(optional: true);

    // ── Logging ───────────────────────────────────────────────────────────────
    // In stdio mode the stdout stream is the MCP transport channel — ALL log
    // output MUST go to stderr so it never corrupts the JSON-RPC message stream.
    builder.Logging.ClearProviders();
    if (isStdio)
    {
        builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
    }
    else
    {
        builder.Logging.AddConsole();
    }

    // ── Kestrel: suppress HTTP server in stdio mode ───────────────────────────
    // In stdio mode there is no HTTP traffic; prevent Kestrel from binding any
    // port so we don't waste resources or conflict with other services.
    if (isStdio)
    {
        builder.WebHost.UseUrls(string.Empty);
    }

    // ── Database configs ──────────────────────────────────────────────────────
    builder.Services.Configure<List<DatabaseConfig>>(
        builder.Configuration.GetSection("Databases"));

    // ── App services ──────────────────────────────────────────────────────────
    builder.Services.AddSingleton<IDatabaseService, DatabaseService>();
    builder.Services.AddSingleton(new ServerOptions { AllowAnySql = allowAnySql });

    // ── MCP server ────────────────────────────────────────────────────────────
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

    // ─────────────────────────────────────────────────────────────────────────
    // App
    // ─────────────────────────────────────────────────────────────────────────
    var app = builder.Build();

    if (!isStdio)
    {
        app.MapMcp("/mcp");
    }

    await app.RunAsync();
});

var result = rootCommand.Parse(args);
return await result.InvokeAsync();
