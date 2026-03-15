using IndexThinking.Extensions;
using Microsoft.Extensions.Hosting;
using Pulsa;
using PulsaLLM;
using PulsaLLM.Workers;
using System.Text;
using Serilog;
using TokenMeter;

Console.OutputEncoding = Encoding.UTF8;

var builder = new HostApplicationBuilder(args);

var logsPath = builder.Configuration["LogsPath"] ?? "logs";
Directory.CreateDirectory(logsPath);

builder.Services.AddSerilog(cfg => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logsPath, "pulsa-llm-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

var tasks = builder.Configuration
    .GetSection("Tasks")
    .Get<List<LlmTaskOptions>>() ?? [];

if (tasks.Count == 0)
{
    Log.Fatal("No tasks configured. Add at least one task to 'Tasks' in appsettings.json.");
    return;
}

// Global provider options (can be overridden per-task via prompt frontmatter)
var globalProvider = new ProviderOptions();
builder.Configuration.GetSection("Provider").Bind(globalProvider);

builder.Services.AddSingleton<IReadOnlyList<LlmTaskOptions>>(tasks);
builder.Services.AddSingleton<IReadOnlyList<IPulsaOptions>>(tasks.Cast<IPulsaOptions>().ToList());
builder.Services.AddSingleton(globalProvider);
builder.Services.AddSingleton<FileQueue>();
builder.Services.AddSingleton<ChatClientFactory>();
builder.Services.AddSingleton<ITokenCounter>(TokenCounter.Default());

// IndexThinking agent services
builder.Services.AddIndexThinkingAgents();

builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));
builder.Services.AddHostedService<UpdateService>();
builder.Services.AddHostedService<FileWatcherWorker>();
builder.Services.AddHostedService<LlmWorker>();

var host = builder.Build();
host.Run();
