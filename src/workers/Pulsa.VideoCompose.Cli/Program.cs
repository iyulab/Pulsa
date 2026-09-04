// plugins/pulsa/src/workers/Pulsa.VideoCompose.Cli/Program.cs
using Microsoft.Extensions.AI;
using OpenAI;
using PulsaVideoCompose;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

switch (args[0])
{
    case "compose":
        return await RunComposeAsync(args[1..]);
    case "draft-captions":
        return await RunDraftCaptionsAsync(args[1..]);
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
          PulsaVideoCompose.Cli compose --images <path...> --captions <text...> --scene-duration <seconds> --output <path> --ffmpeg-dir <dir>
          PulsaVideoCompose.Cli draft-captions --images <path...> --intro <text> --openai-key <key> [--openai-model <model>]
        """);
}

static async Task<int> RunComposeAsync(string[] args)
{
    var options = ParseFlags(args);
    var images = options["images"];
    var captions = options["captions"];
    var ffmpegDir = options["ffmpeg-dir"][0];
    var outputPath = options["output"][0];
    var sceneDuration = double.Parse(options["scene-duration"][0]);

    var composer = new FfmpegVideoComposer(ffmpegDir);
    var result = await composer.ComposeAsync(new ComposeVideoRequest(images, captions, sceneDuration, outputPath));

    if (!result.Success)
    {
        Console.Error.WriteLine($"compose failed: {result.Error}");
        return 1;
    }
    Console.WriteLine($"Wrote {result.OutputPath} (+ {result.SrtPath})");
    return 0;
}

static async Task<int> RunDraftCaptionsAsync(string[] args)
{
    var options = ParseFlags(args);
    var images = options["images"];
    var introText = options["intro"][0];
    var apiKey = options["openai-key"][0];
    var model = options.TryGetValue("openai-model", out var m) ? m[0] : "gpt-4o-mini";

    // Pulsa's own standalone execution part supplies its own concrete IChatClient — this is the
    // ONE place in the whole Pulsa repo allowed to know about a concrete provider/credential;
    // CaptionDrafter itself (SDK) never does. Not a revived Pulsa.LLM.SDK.ChatClientFactory
    // (archived in Task 1) — deliberately small and scoped to only this CLI's own needs.
    var openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey));
    IChatClient chatClient = openAiClient.GetChatClient(model).AsIChatClient();

    var captions = await CaptionDrafter.DraftAsync(chatClient, new DraftCaptionsRequest(images, introText));
    foreach (var caption in captions) Console.WriteLine(caption);
    return 0;
}

static Dictionary<string, string[]> ParseFlags(string[] args)
{
    var result = new Dictionary<string, string[]>();
    var i = 0;
    while (i < args.Length)
    {
        if (!args[i].StartsWith("--")) { i++; continue; }
        var key = args[i][2..];
        var values = new List<string>();
        i++;
        while (i < args.Length && !args[i].StartsWith("--"))
        {
            values.Add(args[i]);
            i++;
        }
        result[key] = values.ToArray();
    }
    return result;
}
