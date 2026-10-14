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
                [--aspect-ratio 16:9|9:16] [--title-image <path> --title-text <text>] [--outro-image <path> --outro-text <text>]
                [--font <font name>] [--fonts-dir <dir>]
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
    var sceneDuration = double.Parse(options["scene-duration"][0], System.Globalization.CultureInfo.InvariantCulture);
    // Text flags may arrive as one quoted argument or as several words; either way they are one string.
    string? Joined(string key) => options.TryGetValue(key, out var v) && v.Length > 0 ? string.Join(' ', v) : null;

    var font = Joined("font");
    var fontsDirectory = Joined("fonts-dir");
    var composeOptions = new VideoComposeOptions(
        Captions: font is null ? null : CaptionStyle.DefaultCaptions with { FontName = font },
        Cards: font is null ? null : CaptionStyle.DefaultCards with { FontName = font },
        FontsDirectory: fontsDirectory);

    var composer = new FfmpegVideoComposer(ffmpegDir, composeOptions);
    var result = await composer.ComposeAsync(new ComposeVideoRequest(
        images, captions, sceneDuration, outputPath,
        AspectRatio: Joined("aspect-ratio") ?? "16:9",
        TitleImagePath: Joined("title-image"),
        TitleText: Joined("title-text"),
        OutroImagePath: Joined("outro-image"),
        OutroText: Joined("outro-text")));

    if (!result.Success)
    {
        Console.Error.WriteLine($"compose failed: {result.Error}");
        return 1;
    }
    if (result.FontSubstitutions is null)
        Console.Error.WriteLine("warning: could not check whether the named fonts were found");
    foreach (var substitution in result.FontSubstitutions ?? [])
        Console.Error.WriteLine(
            $"warning: font '{substitution.RequestedFamily}'{(substitution.Bold ? " (bold)" : "")} was not found; the text was drawn in {substitution.UsedFont}");
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
