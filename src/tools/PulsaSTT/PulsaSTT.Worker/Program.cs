using Microsoft.Extensions.Hosting;
using Pulsa;
using PulsaSTT;
using PulsaSTT.Workers;
using System.Text;
using Serilog;

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
        Path.Combine(logsPath, "pulsa-stt-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

var tasks = builder.Configuration
    .GetSection("Tasks")
    .Get<List<SttTaskOptions>>() ?? [];

if (tasks.Count == 0)
{
    Log.Fatal("No tasks configured. Add at least one task to 'Tasks' in appsettings.json.");
    return;
}

builder.Services.AddSingleton<IReadOnlyList<SttTaskOptions>>(tasks);
builder.Services.AddSingleton<IReadOnlyList<IPulsaOptions>>(tasks.Cast<IPulsaOptions>().ToList());
builder.Services.AddSingleton<FileQueue>();
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));
builder.Services.AddHostedService<UpdateService>();
builder.Services.AddHostedService<FileWatcherWorker>();
builder.Services.AddHostedService<TranscribeWorker>();

var host = builder.Build();
host.Run();
