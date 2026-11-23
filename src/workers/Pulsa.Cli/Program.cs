using System.Globalization;
using Microsoft.Extensions.AI;
using OpenAI;
using PulsaExtract;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

switch (args[0])
{
    case "extract":
        return await RunExtractAsync(args[1..], verifyOnly: false);
    case "verify":
        return await RunExtractAsync(args[1..], verifyOnly: true);
    default:
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        Usage:
          Pulsa.Cli extract --text <file> --schema <schema.json> [--openai-key <key>] [--openai-model <model>]
                [--openai-endpoint <url>] [--fuzzy] [--threshold <0-1>] [--output <result.json>]
          Pulsa.Cli verify --text <file> --schema <schema.json> --candidates <candidates.json>
                [--fuzzy] [--threshold <0-1>] [--output <result.json>]
        """);
}

// extract asks a chat model for the schema's fields; verify checks values someone already proposed. Both then prove
// every value against the text, and write the grounded values and the refused ones as JSON (standard output, or --output).
static async Task<int> RunExtractAsync(string[] args, bool verifyOnly)
{
    var options = ParseFlags(args);
    string? Single(string key) => options.TryGetValue(key, out var v) && v.Length > 0 ? string.Join(' ', v) : null;

    var command = verifyOnly ? "verify" : "extract";
    var textPath = Single("text");
    var schemaPath = Single("schema");
    var candidatesPath = Single("candidates");
    var apiKey = Single("openai-key");
    var endpoint = Single("openai-endpoint");
    var missingInput = textPath is null || schemaPath is null || (verifyOnly && candidatesPath is null);
    if (missingInput || (!verifyOnly && apiKey is null && endpoint is null))
    {
        Console.Error.WriteLine(verifyOnly
            ? "verify needs --text, --schema and --candidates"
            : "extract needs --text, --schema and --openai-key (the key may be left out when --openai-endpoint names a server that takes none)");
        PrintUsage();
        return 1;
    }

    var threshold = 0.75;
    if (Single("threshold") is { } thresholdText &&
        (!double.TryParse(thresholdText, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold) || threshold is < 0 or > 1))
    {
        Console.Error.WriteLine("--threshold must be a number from 0 to 1");
        return 1;
    }
    var extractOptions = new ExtractOptions { Fuzzy = options.ContainsKey("fuzzy"), Threshold = threshold };

    var outputPath = Single("output");
    if (outputPath is not null && File.Exists(outputPath))
    {
        Console.Error.WriteLine($"{outputPath} already exists");
        return 1;
    }

    try
    {
        var text = await File.ReadAllTextAsync(textPath!);
        var schema = ExtractJson.ReadSchema(await File.ReadAllTextAsync(schemaPath!));

        ExtractResult result;
        if (verifyOnly)
        {
            var candidates = ExtractJson.ReadCandidates(await File.ReadAllTextAsync(candidatesPath!));
            result = Extractor.Verify(text, schema, candidates, extractOptions);
        }
        else
        {
            // The command line is a host: it picks the model and takes the credential; the capability only receives a client.
            var clientOptions = new OpenAIClientOptions();
            if (endpoint is not null) clientOptions.Endpoint = new Uri(endpoint);
            // The client library needs some credential; a server that takes none ignores this placeholder.
            var chat = new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey ?? "no-key"), clientOptions)
                .GetChatClient(Single("openai-model") ?? "gpt-4o-mini").AsIChatClient();
            result = await Extractor.ExtractAsync(chat, text, schema, extractOptions);
        }

        var json = ExtractJson.Write(result);
        if (outputPath is null)
            Console.WriteLine(json);
        else
            await File.WriteAllTextAsync(outputPath, json);

        foreach (var refused in result.Rejected)
            Console.Error.WriteLine($"refused: {refused.Name} ({refused.Reason}): {refused.Detail}");
        if (outputPath is not null)
            Console.Error.WriteLine($"Wrote {outputPath} ({result.Fields.Count} grounded, {result.Rejected.Count} refused)");
        return 0;
    }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException
                                   or HttpRequestException or System.ClientModel.ClientResultException)
    {
        Console.Error.WriteLine($"{command} failed: {ex.Message}");
        return 1;
    }
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
