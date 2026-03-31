using System.CommandLine;
using AdoMcpServer.Models;
using AdoMcpServer.Services;
using AdoMcpServer.Tools;
using Microsoft.AspNetCore.Builder;
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

// Allow unrecognised tokens to pass through so that standard host arguments
// such as --urls or --environment still work.
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

    // Forward any unrecognised tokens (e.g. --urls, --environment).
    var extraArgs = parseResult.UnmatchedTokens.ToArray();

    if (isHttp)
    {
        // ─────────────────────────────────────────────────────────────────────
        // HTTP / SSE mode – full ASP.NET Core web application
        // ─────────────────────────────────────────────────────────────────────
        var builder = WebApplication.CreateBuilder(extraArgs);

        ConfigureConfiguration(builder.Configuration, builder.Environment.EnvironmentName);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        ConfigureServices(builder.Services, builder.Configuration, allowAnySql);
        builder.Services
            .AddMcpServer()
            .WithToolsFromAssembly(typeof(DatabaseTools).Assembly)
            .WithHttpTransport();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.RunAsync(ct);
    }
    else
    {
        // ─────────────────────────────────────────────────────────────────────
        // stdio mode – pure console host (no Kestrel / web server started).
        // stdout is the MCP JSON-RPC transport channel; ALL logging MUST go
        // to stderr so it never corrupts the message stream.
        // ─────────────────────────────────────────────────────────────────────
        var hostBuilder = Host.CreateApplicationBuilder(extraArgs);

        ConfigureConfiguration(hostBuilder.Configuration, hostBuilder.Environment.EnvironmentName);
        hostBuilder.Logging.ClearProviders();
        hostBuilder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
        ConfigureServices(hostBuilder.Services, hostBuilder.Configuration, allowAnySql);
        hostBuilder.Services
            .AddMcpServer()
            .WithToolsFromAssembly(typeof(DatabaseTools).Assembly)
            .WithStdioServerTransport();

        var host = hostBuilder.Build();
        await host.RunAsync(ct);
    }
});

var result = rootCommand.Parse(args);
return await result.InvokeAsync();

// ─────────────────────────────────────────────────────────────────────────────
// Shared setup helpers
// ─────────────────────────────────────────────────────────────────────────────

static void ConfigureConfiguration(IConfigurationBuilder config, string environmentName)
{
    config.Sources.Clear();
    config
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
        .AddEnvironmentVariables("ADOMCP_")
        .AddUserSecrets<Program>(optional: true);
}

static void ConfigureServices(
    IServiceCollection services, IConfiguration configuration, bool allowAnySql)
{
    services.Configure<List<DatabaseConfig>>(configuration.GetSection("Databases"));
    services.AddSingleton<IDatabaseService, DatabaseService>();
    services.AddSingleton(new ServerOptions { AllowAnySql = allowAnySql });
}
