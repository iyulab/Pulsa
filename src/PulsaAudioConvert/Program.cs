using Microsoft.Extensions.Hosting;
using Pulsa;
using PulsaAudioConvert;
using PulsaAudioConvert.Workers;
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
        Path.Combine(logsPath, "pulsa-audio-convert-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));
builder.Services.Configure<ConvertOptions>(builder.Configuration.GetSection("Convert"));
builder.Services.AddSingleton<FileQueue>();
builder.Services.AddHostedService<UpdateService>();
builder.Services.AddHostedService<FileWatcherWorker<ConvertOptions>>();
builder.Services.AddHostedService<ConvertWorker>();

var host = builder.Build();
host.Run();
