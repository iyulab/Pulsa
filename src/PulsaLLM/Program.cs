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

if (File.Exists(promptPath))
{
    var promptData = PromptLoader.Parse(File.ReadAllText(promptPath));
    baseProviderOpts = PromptLoader.ApplyOverrides(baseProviderOpts, promptData.Frontmatter);
}

// Make resolved provider options (with frontmatter overrides) available to workers
builder.Services.AddSingleton(baseProviderOpts);

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
            thinkingOpts.EnableReasoning = true;
            thinkingOpts.EnableContextTracking = false;
            thinkingOpts.EnableContextInjection = false;
            thinkingOpts.DefaultContinuation = new()
            {
                // Disable continuation — thinking models leak reasoning
                // into continuation responses, polluting the output.
                MaxContinuations = 0,
                MaxContextTokens = 32768,
            };
        })
        .Build(sp);
});

var host = builder.Build();
host.Run();

static IChatClient CreateOpenAiClient(ProviderOptions opts)
{
    var openAiClient = new OpenAIClient(opts.ApiKey);
    return openAiClient.GetChatClient(opts.Model).AsIChatClient();
}

static IChatClient CreateOpenAiCompatibleClient(ProviderOptions opts)
{
    var endpoint = new Uri(opts.Host.TrimEnd('/') + opts.PathPrefix);
    var credential = new System.ClientModel.ApiKeyCredential(opts.ApiKey);
    var clientOptions = new OpenAIClientOptions { Endpoint = endpoint };
    var openAiClient = new OpenAIClient(credential, clientOptions);
    return openAiClient.GetChatClient(opts.Model).AsIChatClient();
}
