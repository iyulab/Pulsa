using Pulsa;
using PulsaPDFDiff;
using Serilog;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: true);

var logsPath = builder.Configuration["LogsPath"] ?? "logs";
Directory.CreateDirectory(logsPath);

builder.Services.AddSerilog(cfg => cfg
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(logsPath, "pulsa-pdfdiff-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

var promptsDir = Path.Combine(AppContext.BaseDirectory, "prompts");
builder.Services.AddSingleton(new PromptManager(promptsDir));
builder.Services.AddSingleton(new SettingsManager(AppContext.BaseDirectory));
builder.Services.AddSingleton<VisionComparer>();

builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));
builder.Services.AddHostedService<UpdateService>();

var app = builder.Build();

app.UseStaticFiles();

// POST /api/compare
app.MapPost("/api/compare", async (
    HttpRequest request,
    VisionComparer comparer,
    PromptManager prompts,
    SettingsManager settings,
    IConfiguration config,
    CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var referenceFile = form.Files["reference"];
    var targetFile = form.Files["target"];

    if (referenceFile is null || targetFile is null)
        return Results.BadRequest(new { error = "Both 'reference' and 'target' PDF files are required." });

    var promptName = form["prompt"].FirstOrDefault();
    var customPrompt = form["customPrompt"].FirstOrDefault();

    string systemPrompt;
    if (!string.IsNullOrWhiteSpace(customPrompt))
    {
        systemPrompt = customPrompt;
    }
    else if (!string.IsNullOrWhiteSpace(promptName))
    {
        systemPrompt = await prompts.GetAsync(promptName, ct)
            ?? throw new InvalidOperationException($"Prompt not found: {promptName}");
    }
    else
    {
        return Results.BadRequest(new { error = "Either 'prompt' or 'customPrompt' is required." });
    }

    var opts = settings.GetSettings(config);
    if (string.IsNullOrWhiteSpace(opts.ApiKey))
        return Results.BadRequest(new { error = "OpenAI API key not configured. Set it in Settings." });

    using var refStream = referenceFile.OpenReadStream();
    using var tgtStream = targetFile.OpenReadStream();

    var refImages = PdfImageConverter.ConvertToBase64Images(refStream);
    var tgtImages = PdfImageConverter.ConvertToBase64Images(tgtStream);

    var report = await comparer.CompareAsync(opts, refImages, tgtImages, systemPrompt, ct);

    return Results.Text(report, "text/markdown; charset=utf-8");
});

// GET /api/prompts
app.MapGet("/api/prompts", (PromptManager prompts) => Results.Ok(prompts.List()));

// GET /api/prompts/{name}
app.MapGet("/api/prompts/{name}", async (string name, PromptManager prompts, CancellationToken ct) =>
{
    var content = await prompts.GetAsync(name, ct);
    return content is not null ? Results.Text(content) : Results.NotFound();
});

// PUT /api/prompts/{name}
app.MapPut("/api/prompts/{name}", async (string name, HttpRequest request, PromptManager prompts, CancellationToken ct) =>
{
    using var reader = new StreamReader(request.Body);
    var content = await reader.ReadToEndAsync(ct);
    await prompts.SaveAsync(name, content, ct);
    return Results.Ok();
});

// GET /api/settings
app.MapGet("/api/settings", (SettingsManager settings, IConfiguration config) =>
    Results.Ok(settings.GetMaskedSettings(config)));

// PUT /api/settings
app.MapPut("/api/settings", async (OpenAIOptions newSettings, SettingsManager settings, CancellationToken ct) =>
{
    await settings.UpdateAsync(newSettings, ct);
    return Results.Ok(new { message = "Settings saved. Restart app to apply." });
});

// Fallback to index.html for SPA
app.MapFallbackToFile("index.html");

app.Run();
