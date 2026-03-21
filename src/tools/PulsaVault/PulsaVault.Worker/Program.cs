using FileFlux;
using FluxIndex.Extensions.FileVault.Extensions;
using FluxIndex.Providers.LMSupply.Extensions;
using FluxIndex.Providers.OpenAI.Extensions;
using FluxIndex.Storage.PostgreSQL;
using FluxIndex.Storage.SQLite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Pulsa;
using PulsaVault;
using PulsaVault.Tools;
using PulsaVault.Workers;
using Serilog;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

// Determine transport mode from command-line arguments
var mcpStdio = args.Contains("--mcp-stdio");
var mcpSse = args.Contains("--mcp-sse");

if (mcpSse)
{
    await RunWithSseTransport(args);
}
else if (mcpStdio)
{
    await RunWithStdioTransport(args);
}
else
{
    RunSyncOnly(args);
}

return;

// ── Sync-only mode (no MCP) ──────────────────────────────────────────────

static void RunSyncOnly(string[] args)
{
    var builder = new HostApplicationBuilder(args);
    ConfigureCommonServices(builder.Services, builder.Configuration);
    builder.Services.AddHostedService<VaultSyncWorker>();

    var host = builder.Build();
    host.Run();
}

// ── MCP stdio transport ──────────────────────────────────────────────────

static async Task RunWithStdioTransport(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // Redirect logging to stderr (stdout is reserved for MCP protocol)
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace;
    });

    ConfigureCommonServices(builder.Services, builder.Configuration);
    builder.Services.AddHostedService<VaultSyncWorker>();

    var mcpSection = builder.Configuration.GetSection("Mcp");

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = mcpSection["Name"] ?? "PulsaVault",
                Version = mcpSection["Version"] ?? "0.1.0"
            };
            options.ServerInstructions = mcpSection["Instructions"]
                ?? "Knowledge base search server for local documents";
        })
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(SearchTool).Assembly);

    var host = builder.Build();
    await host.RunAsync();
}

// ── MCP SSE/HTTP transport ───────────────────────────────────────────────

static async Task RunWithSseTransport(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    ConfigureCommonServices(builder.Services, builder.Configuration);
    builder.Services.AddHostedService<VaultSyncWorker>();

    var mcpSection = builder.Configuration.GetSection("Mcp");
    var port = 3200;
    var portIndex = Array.FindIndex(args, a => a == "--port");
    if (portIndex >= 0 && portIndex + 1 < args.Length
        && int.TryParse(args[portIndex + 1], out var parsedPort))
    {
        port = parsedPort;
    }

    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = mcpSection["Name"] ?? "PulsaVault",
                Version = mcpSection["Version"] ?? "0.1.0"
            };
            options.ServerInstructions = mcpSection["Instructions"]
                ?? "Knowledge base search server for local documents";
        })
        .WithHttpTransport()
        .WithToolsFromAssembly(typeof(SearchTool).Assembly);

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenLocalhost(port);
    });

    var app = builder.Build();
    app.MapMcp("/mcp");

    Console.WriteLine($"PulsaVault MCP Server (SSE) starting on http://localhost:{port}");
    Console.WriteLine($"  MCP Endpoint: http://localhost:{port}/mcp");

    await app.RunAsync();
}

// ── Common service configuration ─────────────────────────────────────────

static void ConfigureCommonServices(IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
{
    // ── Serilog ──
    var logsPath = configuration["LogsPath"] ?? "logs";
    Directory.CreateDirectory(logsPath);

    services.AddSerilog(cfg => cfg
        .ReadFrom.Configuration(configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            Path.Combine(logsPath, "pulsa-vault-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

    // ── Update service ──
    services.Configure<UpdateOptions>(configuration.GetSection("Update"));
    services.AddHostedService<UpdateService>();

    // ── Vault folder options ──
    services.Configure<PulsaVaultOptions>(configuration.GetSection("Vault"));

    // ── FileFlux (document extraction + chunking) ──
    services.AddFileFlux();

    // ── FileVault with FluxIndex (extraction + chunking + memorization) ──
    services.AddFileVaultWithFluxIndex(options =>
    {
        configuration.GetSection("FileVault").Bind(options);
    });

    // ── Vector store (provider switch) ──
    var vectorStoreProvider = configuration["VectorStore:Provider"] ?? "SQLite";
    switch (vectorStoreProvider)
    {
        case "PostgreSQL":
            var pgConn = configuration["VectorStore:PostgreSQL:ConnectionString"]
                ?? throw new InvalidOperationException("PostgreSQL ConnectionString is required.");
            services.AddPostgreSQLVectorStore(pgConn);
            break;

        case "SQLite":
        default:
            var sqlitePath = configuration["VectorStore:SQLite:DatabasePath"]
                ?? "./data/vault-vectors.db";
            services.AddSQLiteVecVectorStore(sqlitePath);
            break;
    }

    // ── Embedding provider (provider switch) ──
    var embeddingProvider = configuration["Embedding:Provider"] ?? "";
    switch (embeddingProvider)
    {
        case "OpenAI":
            var endpoint = configuration["Embedding:OpenAI:Endpoint"]
                ?? "https://api.openai.com/v1";
            var apiKey = configuration["Embedding:OpenAI:ApiKey"];
            var model = configuration["Embedding:OpenAI:Model"]
                ?? "text-embedding-3-small";
            var dimension = int.TryParse(configuration["Embedding:OpenAI:Dimension"], out var d)
                ? d : 1536;
            services.AddOpenAICompatibleEmbedding(endpoint, apiKey, model, dimension);
            break;

        case "LMSupply":
        default:
            var modelId = configuration["Embedding:LMSupply:ModelId"] ?? "default";
            services.AddLMSupplyEmbedding(modelId);
            break;
    }
}
