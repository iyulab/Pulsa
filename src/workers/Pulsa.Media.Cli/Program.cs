using Microsoft.Extensions.AI;
using OpenAI;
using PulsaRedact;
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
    case "redact":
        return await RunRedactAsync(args[1..]);
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
          PulsaMedia.Cli compose --images <path...> --captions <text...> --scene-duration <seconds> --output <path> --ffmpeg-dir <dir>
                [--aspect-ratio 16:9|9:16] [--title-image <path> --title-text <text>] [--outro-image <path> --outro-text <text>]
                [--font <font name>] [--fonts-dir <dir>]
          PulsaMedia.Cli draft-captions --images <path...> --intro <text> --openai-key <key> [--openai-model <model>]
          PulsaMedia.Cli redact --input <path> --output <path> --region <x>,<y>,<width>,<height> [<x>,<y>,<width>,<height> ...]
                [--start <seconds>] [--end <seconds>] [--ffmpeg-dir <dir>]
          PulsaMedia.Cli redact --input <image> --output <path> --detect <what to hide> --openai-key <key>
                [--openai-model <model>] [--openai-endpoint <url>] [--min-confidence <0-1>] [--region ...] [--ffmpeg-dir <dir>]
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

    // The command line is a host, so it is the one place that picks a concrete chat client and
    // takes its credential; CaptionDrafter itself only receives an IChatClient.
    var openAiClient = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey));
    IChatClient chatClient = openAiClient.GetChatClient(model).AsIChatClient();

    var captions = await CaptionDrafter.DraftAsync(chatClient, new DraftCaptionsRequest(images, introText));
    foreach (var caption in captions) Console.WriteLine(caption);
    return 0;
}

static async Task<int> RunRedactAsync(string[] args)
{
    var options = ParseFlags(args);
    string? Single(string key) => options.TryGetValue(key, out var v) && v.Length > 0 ? string.Join(' ', v) : null;

    var input = Single("input");
    var output = Single("output");
    var regionTexts = options.TryGetValue("region", out var r) ? r : [];
    var detect = Single("detect");
    if (input is null || output is null || (regionTexts.Length == 0 && detect is null))
    {
        Console.Error.WriteLine("redact needs --input, --output and --region <x>,<y>,<width>,<height> or --detect <what to hide>");
        PrintUsage();
        return 1;
    }

    var regions = new List<RedactRegion>();
    foreach (var region in regionTexts)
    {
        var parts = region.Split(',', StringSplitOptions.TrimEntries);
        var numbers = new int[4];
        if (parts.Length != 4 || !parts.Select((p, i) => int.TryParse(p, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out numbers[i])).All(ok => ok))
        {
            Console.Error.WriteLine($"--region must be four whole numbers <x>,<y>,<width>,<height>, not '{region}'");
            return 1;
        }
        regions.Add(new RedactRegion(numbers[0], numbers[1], numbers[2], numbers[3]));
    }

    double? Number(string key) => Single(key) is { } text
        ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
        : null;

    RedactDetection? detection = null;
    if (detect is not null)
    {
        if (Single("openai-key") is not { } apiKey)
        {
            Console.Error.WriteLine("--detect needs --openai-key: detection asks a vision model");
            return 1;
        }
        // The command line is a host: it picks the vision model and takes the credential.
        var clientOptions = new OpenAIClientOptions();
        if (Single("openai-endpoint") is { } endpoint) clientOptions.Endpoint = new Uri(endpoint);
        var vision = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions)
            .GetChatClient(Single("openai-model") ?? "gpt-4o-mini").AsIChatClient();
        detection = new RedactDetection(new ChatRegionDetector(vision, detect), Number("min-confidence") ?? 0.5);
    }

    // No --ffmpeg-dir: ffmpeg is looked up on PATH.
    var redactor = new MediaRedactor(Single("ffmpeg-dir") ?? string.Empty);
    var result = await redactor.RedactAsync(
        new RedactRequest(input, output, regions, StartTime: Number("start"), EndTime: Number("end")), detection);

    foreach (var found in result.Detected)
        Console.Error.WriteLine($"hidden: {found.Label} at {found.Region.X},{found.Region.Y} {found.Region.Width}x{found.Region.Height} (confidence {found.Confidence:0.00})");
    foreach (var doubtful in result.Review)
        Console.Error.WriteLine($"review: {doubtful.Label} at {doubtful.Region.X},{doubtful.Region.Y} {doubtful.Region.Width}x{doubtful.Region.Height} (confidence {doubtful.Confidence:0.00}) — not hidden");
    foreach (var note in result.Notes) Console.Error.WriteLine($"note: {note}");
    if (!result.Success)
    {
        Console.Error.WriteLine($"redact failed: {result.Error}");
        return 1;
    }
    Console.WriteLine($"Wrote {result.OutputPath}");
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
