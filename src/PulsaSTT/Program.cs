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

builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));
builder.Services.Configure<SttOptions>(builder.Configuration.GetSection("Stt"));
builder.Services.AddSingleton<FileQueue>();
builder.Services.AddHostedService<UpdateService>();
builder.Services.AddHostedService<FileWatcherWorker<SttOptions>>();
builder.Services.AddHostedService<TranscribeWorker>();

var host = builder.Build();
host.Run();
