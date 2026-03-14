using IndexThinking.Client;
using IndexThinking.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using OpenAI;
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

builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("Update"));
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection("LLM"));
builder.Services.AddSingleton<FileQueue>();
builder.Services.AddHostedService<UpdateService>();
builder.Services.AddHostedService<FileWatcherWorker<LlmOptions>>();
builder.Services.AddHostedService<LlmWorker>();

// IndexThinking agent services
builder.Services.AddIndexThinkingAgents();

// Resolve provider options (config + prompt frontmatter overrides)
var llmSection = builder.Configuration.GetSection("LLM");
var baseProviderOpts = new ProviderOptions();
llmSection.GetSection("Provider").Bind(baseProviderOpts);

var promptFile = llmSection["PromptFile"] ?? "SUMMARIZE-PROMPT.md";
var promptPath = Path.IsPathRooted(promptFile)
    ? promptFile
    : Path.Combine(AppContext.BaseDirectory, promptFile);
// Fallback to current directory (deployed layout puts prompt next to exe,
// but dev/test layout has it in the working directory)
if (!File.Exists(promptPath))
    promptPath = Path.Combine(Directory.GetCurrentDirectory(), promptFile);

if (File.Exists(promptPath))
{
    var promptData = PromptLoader.Parse(File.ReadAllText(promptPath));
    baseProviderOpts = PromptLoader.ApplyOverrides(baseProviderOpts, promptData.Frontmatter);
}

// Thinking models (e.g. Qwen3) require temperature > 0 for reasoning separation.
// With temperature=0.0, enable_thinking is ignored and reasoning leaks into output text.
if (baseProviderOpts.Model.Contains("thinking", StringComparison.OrdinalIgnoreCase)
    && baseProviderOpts.Temperature is 0.0f)
{
    baseProviderOpts.Temperature = 0.6f;
    Console.Error.WriteLine(
        "[WRN] Thinking model with temperature=0.0 detected; adjusted to 0.6 for reasoning separation.");
}

// Make resolved provider options (with frontmatter overrides) available to workers
builder.Services.AddSingleton(baseProviderOpts);

// Token counter for proactive input truncation
builder.Services.AddSingleton<ITokenCounter>(TokenCounter.Default());

// Register IChatClient with IndexThinking pipeline
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var opts = baseProviderOpts;
    IChatClient innerClient = opts.Type.ToLowerInvariant() switch
    {
        "openai" => CreateOpenAiClient(opts),
        "openai-compatible" => CreateOpenAiCompatibleClient(opts),
        _ => throw new InvalidOperationException(
            $"Local provider requires async initialization. Use openai or openai-compatible."),
    };

    return new ChatClientBuilder(innerClient)
        .UseIndexThinking(thinkingOpts =>
        {
            thinkingOpts.EnableReasoning = false;
            thinkingOpts.EnableContextTracking = false;
            thinkingOpts.EnableContextInjection = false;
            thinkingOpts.DefaultContinuation = new()
            {
                // Auto-cap prevents HTTP 400; reasoning is disabled on
                // continuation requests so thinking content won't leak.
                MaxContinuations = 3,
                // Use model's actual context window for auto-cap
                MaxContextTokens = opts.ContextWindow > 0 ? opts.ContextWindow : null,
            };
            // Qwen3 thinking models on vLLM/GPUStack require enable_thinking: true
            // to return reasoning in a separate reasoning_content field.
            thinkingOpts.ReasoningRequestSettings = new()
            {
                UseAlternativeQwenField = true,
            };
        })
        .Build(sp);
});

var host = builder.Build();
host.Run();

static IChatClient CreateOpenAiClient(ProviderOptions opts)
{
    var clientOptions = new OpenAIClientOptions
    {
        NetworkTimeout = TimeSpan.FromMinutes(10),
    };
    var credential = new System.ClientModel.ApiKeyCredential(opts.ApiKey);
    var openAiClient = new OpenAIClient(credential, clientOptions);
    return openAiClient.GetChatClient(opts.Model).AsIChatClient();
}

static IChatClient CreateOpenAiCompatibleClient(ProviderOptions opts)
{
    var endpoint = new Uri(opts.Host.TrimEnd('/') + opts.PathPrefix);
    var credential = new System.ClientModel.ApiKeyCredential(opts.ApiKey);
    var clientOptions = new OpenAIClientOptions
    {
        Endpoint = endpoint,
        NetworkTimeout = TimeSpan.FromMinutes(10),
    };
    var openAiClient = new OpenAIClient(credential, clientOptions);
    return openAiClient.GetChatClient(opts.Model).AsIChatClient();
}
