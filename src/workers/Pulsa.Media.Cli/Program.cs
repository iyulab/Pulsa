using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Audio;
using PulsaRedact;
using PulsaTranscribe;
using PulsaTranscript;
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
    case "transcribe":
        return await RunTranscribeAsync(args[1..]);
    case "score":
        return await RunScoreAsync(args[1..]);
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
          PulsaMedia.Cli redact --input <path> --output <path> --region <x>,<y>,<width>,<height>
                [--start <seconds>] [--end <seconds>] [--ffmpeg-dir <dir>]
          PulsaMedia.Cli transcribe --input <audio or video> --openai-key <key> [--output <file.vtt>]
                [--openai-model <model>] [--openai-endpoint <url>] [--language <bcp47>]
                [--noise-reduction] [--no-loudness] [--max-segment <seconds>] [--min-silence <seconds>]
                [--vocabulary <file.md>] [--glossary <file.md> --refine-model <model>] [--ffmpeg-dir <dir>]
          PulsaMedia.Cli score --reference <file.vtt|file.txt> --hypothesis <file.vtt|file.txt>
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
    var region = Single("region");
    if (input is null || output is null || region is null)
    {
        Console.Error.WriteLine("redact needs --input, --output and --region <x>,<y>,<width>,<height>");
        PrintUsage();
        return 1;
    }

    var parts = region.Split(',', StringSplitOptions.TrimEntries);
    var numbers = new int[4];
    if (parts.Length != 4 || !parts.Select((p, i) => int.TryParse(p, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out numbers[i])).All(ok => ok))
    {
        Console.Error.WriteLine($"--region must be four whole numbers <x>,<y>,<width>,<height>, not '{region}'");
        return 1;
    }

    double? Seconds(string key) => Single(key) is { } text
        ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
        : null;

    // No --ffmpeg-dir: ffmpeg is looked up on PATH.
    var redactor = new MediaRedactor(Single("ffmpeg-dir") ?? string.Empty);
    var result = await redactor.RedactAsync(new RedactRequest(
        input, output, numbers[0], numbers[1], numbers[2], numbers[3],
        StartTime: Seconds("start"), EndTime: Seconds("end")));

    if (!result.Success)
    {
        Console.Error.WriteLine($"redact failed: {result.Error}");
        return 1;
    }
    Console.WriteLine($"Wrote {result.OutputPath}");
    return 0;
}

#pragma warning disable MEAI001 // the speech-to-text abstraction is an evaluation API
static async Task<int> RunTranscribeAsync(string[] args)
{
    var options = ParseFlags(args);
    string? Single(string key) => options.TryGetValue(key, out var v) && v.Length > 0 ? string.Join(' ', v) : null;
    double? Seconds(string key) => Single(key) is { } text
        ? double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
        : null;

    var input = Single("input");
    var apiKey = Single("openai-key");
    if (input is null || apiKey is null)
    {
        Console.Error.WriteLine("transcribe needs --input and --openai-key");
        PrintUsage();
        return 1;
    }
    var glossaryPath = Single("glossary");
    var refineModel = Single("refine-model");
    if ((glossaryPath is null) != (refineModel is null))
    {
        Console.Error.WriteLine("--glossary and --refine-model go together: refinement needs both");
        return 1;
    }

    // The command line is a host: it picks the models and takes the credential; the facade only receives clients.
    var clientOptions = new OpenAIClientOptions();
    if (Single("openai-endpoint") is { } endpoint) clientOptions.Endpoint = new Uri(endpoint);
    var openAi = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions);
    var recognizer = openAi.GetAudioClient(Single("openai-model") ?? "gpt-4o-mini-transcribe").AsISpeechToTextClient();

    // Terms the recognizer is told to expect: --vocabulary, else the refinement glossary. OpenAI takes them as a prompt.
    var vocabularyPath = Single("vocabulary") ?? glossaryPath;
    var vocabulary = vocabularyPath is null ? null : Glossary.FromMarkdown(await File.ReadAllTextAsync(vocabularyPath)).Terms;
    var recognition = vocabulary is { Count: > 0 }
        ? new SpeechToTextOptions
        {
            RawRepresentationFactory = _ => new AudioTranscriptionOptions { Prompt = string.Join(", ", vocabulary) },
        }
        : null;

    var request = new TranscribeRequest(input)
    {
        Language = Single("language"),
        Recognition = recognition,
        Prepare = new PrepareOptions(
            NoiseReduction: options.ContainsKey("noise-reduction"),
            NormalizeLoudness: !options.ContainsKey("no-loudness")),
        Glossary = glossaryPath is null ? null : Glossary.FromMarkdown(await File.ReadAllTextAsync(glossaryPath)),
    };
    if (Seconds("max-segment") is { } max) request = request with { MaximumSegment = TimeSpan.FromSeconds(max) };
    if (Seconds("min-silence") is { } min) request = request with { MinimumSilence = TimeSpan.FromSeconds(min) };
    var stages = new TranscribeStages
    {
        Preparer = new FfmpegAudioPreparer(Single("ffmpeg-dir") ?? string.Empty), // empty: ffmpeg on PATH
        Refiner = refineModel is null ? null : openAi.GetChatClient(refineModel).AsIChatClient(),
    };

    TranscribeResult result;
    try
    {
        result = await Transcriber.TranscribeAsync(recognizer, request, stages);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"transcribe failed: {ex.Message}");
        return 1;
    }

    var vtt = WebVtt.Write(result.Cues);
    if (Single("output") is { } output)
    {
        await File.WriteAllTextAsync(output, vtt);
        Console.Error.WriteLine($"Wrote {output} ({result.Cues.Count} cues)");
    }
    else
    {
        Console.Write(vtt);
    }
    foreach (var warning in result.Warnings) Console.Error.WriteLine($"warning: {warning}");
    foreach (var change in result.Rejected)
        Console.Error.WriteLine($"refused correction, cue {change.Index}: \"{change.Original}\" -> \"{change.Refined}\"");
    return 0;
}
#pragma warning restore MEAI001

static async Task<int> RunScoreAsync(string[] args)
{
    var options = ParseFlags(args);
    string? Single(string key) => options.TryGetValue(key, out var v) && v.Length > 0 ? string.Join(' ', v) : null;
    var referencePath = Single("reference");
    var hypothesisPath = Single("hypothesis");
    if (referencePath is null || hypothesisPath is null)
    {
        Console.Error.WriteLine("score needs --reference and --hypothesis");
        PrintUsage();
        return 1;
    }

    // A .vtt file is scored by its cue text; anything else as plain text.
    static async Task<string> ReadText(string path)
    {
        var content = await File.ReadAllTextAsync(path);
        return content.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal)
            ? TranscriptScore.Text(WebVtt.Parse(content))
            : content;
    }

    var reference = await ReadText(referencePath);
    var hypothesis = await ReadText(hypothesisPath);
    static string Line(string name, ErrorRate r) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{name} {r.Rate:0.0000}  (substitutions {r.Substitutions}, deletions {r.Deletions}, insertions {r.Insertions}, reference {r.ReferenceLength})");
    Console.WriteLine(Line("WER", TranscriptScore.WordErrorRate(reference, hypothesis)));
    Console.WriteLine(Line("CER", TranscriptScore.CharacterErrorRate(reference, hypothesis)));
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
