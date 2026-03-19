using Pulsa;
using PulsaPDFDiff;
using Serilog;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB
});

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
builder.Services.AddSingleton(new SettingsManager(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<VisionComparer>();
builder.Services.AddSingleton<PdfSessionStore>();

builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));
builder.Services.AddHostedService<UpdateService>();

var app = builder.Build();

app.UseStaticFiles();

// POST /api/upload — Upload a PDF and get session ID + page count
app.MapPost("/api/upload", async (
    HttpRequest request,
    PdfSessionStore store,
    CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var file = form.Files["file"];

    if (file is null)
        return Results.BadRequest(new { error = "A 'file' PDF is required." });

    using var stream = file.OpenReadStream();
    var images = PdfImageConverter.ConvertToBase64Images(stream);
    var session = store.Create(images);

    return Results.Ok(new { id = session.Id, pageCount = session.PageCount });
});

// POST /api/compare-page — Compare a single page pair
app.MapPost("/api/compare-page", async (
    HttpRequest request,
    VisionComparer comparer,
    PdfSessionStore store,
    PromptManager prompts,
    SettingsManager settings,
    IConfiguration config,
    CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var refId = form["refId"].FirstOrDefault();
    var tgtId = form["tgtId"].FirstOrDefault();
    var refPageStr = form["refPage"].FirstOrDefault();
    var tgtPageStr = form["tgtPage"].FirstOrDefault();
    var promptName = form["prompt"].FirstOrDefault();
    var customPrompt = form["customPrompt"].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(refId) || string.IsNullOrWhiteSpace(tgtId))
        return Results.BadRequest(new { error = "Both 'refId' and 'tgtId' are required." });

    if (!int.TryParse(refPageStr, out var refPage) || !int.TryParse(tgtPageStr, out var tgtPage))
        return Results.BadRequest(new { error = "Valid 'refPage' and 'tgtPage' numbers are required." });

    var refSession = store.Get(refId);
    var tgtSession = store.Get(tgtId);
    if (refSession is null || tgtSession is null)
        return Results.BadRequest(new { error = "Session expired or not found. Please re-upload files." });

    if (refPage < 1 || refPage > refSession.PageCount)
        return Results.BadRequest(new { error = $"refPage must be between 1 and {refSession.PageCount}." });
    if (tgtPage < 1 || tgtPage > tgtSession.PageCount)
        return Results.BadRequest(new { error = $"tgtPage must be between 1 and {tgtSession.PageCount}." });

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

    var result = await comparer.ComparePageAsync(
        opts,
        refSession.Images[refPage - 1],
        tgtSession.Images[tgtPage - 1],
        refPage, tgtPage,
        systemPrompt, ct);

    return Results.Ok(new
    {
        text = result.Text,
        promptTokens = result.PromptTokens,
        completionTokens = result.CompletionTokens,
        totalTokens = result.TotalTokens
    });
});

// POST /api/compare (legacy full-document comparison, now returns tokens)
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

    var result = await comparer.CompareAsync(opts, refImages, tgtImages, systemPrompt, ct);

    return Results.Text(result.Text, "text/markdown; charset=utf-8");
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

// GET /api/models
app.MapGet("/api/models", async (SettingsManager settings, IConfiguration config, CancellationToken ct) =>
{
    var opts = settings.GetSettings(config);
    if (string.IsNullOrWhiteSpace(opts.ApiKey))
        return Results.BadRequest(new { error = "API key not configured." });

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("Authorization", $"Bearer {opts.ApiKey}");
    var response = await http.GetAsync("https://api.openai.com/v1/models", ct);
    if (!response.IsSuccessStatusCode)
        return Results.StatusCode((int)response.StatusCode);

    var json = await response.Content.ReadAsStringAsync(ct);
    return Results.Text(json, "application/json");
});

// GET /api/settings
app.MapGet("/api/settings", (SettingsManager settings, IConfiguration config) =>
    Results.Ok(settings.GetMaskedSettings(config)));

// PUT /api/settings
app.MapPut("/api/settings", async (OpenAIOptions newSettings, SettingsManager settings, IConfiguration config, CancellationToken ct) =>
{
    await settings.UpdateAsync(newSettings, config, ct);
    return Results.Ok(new { message = "Settings saved." });
});

// Fallback to index.html for SPA
app.MapFallbackToFile("index.html");

app.Run();
