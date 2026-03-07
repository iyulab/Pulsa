using LMSupply.Transcriber;
using System.Diagnostics;

Trace.Listeners.Add(new ConsoleTraceListener());

var audioPath = @"D:\data\Pulsa\test-5min.mp3";

Console.WriteLine("=== STT Transcription Test ===");
Console.WriteLine($"Audio: {audioPath}");

var sw = Stopwatch.StartNew();
await using var transcriber = await LocalTranscriber.LoadAsync("large");
Console.WriteLine($"Model loaded in {sw.Elapsed.TotalSeconds:F1}s");

sw.Restart();
var result = await transcriber.TranscribeAsync(audioPath,
    new TranscribeOptions { Language = "ko", NoSpeechThreshold = 1.0f });
Console.WriteLine($"Transcription completed in {sw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine($"Segments: {result.Segments.Count}");
Console.WriteLine($"Language: {result.Language}");
Console.WriteLine($"Text length: {result.Text.Length}");

if (result.Text.Length > 0)
{
    Console.WriteLine($"\n=== First 500 chars ===");
    Console.WriteLine(result.Text[..Math.Min(500, result.Text.Length)]);
}
else
{
    Console.WriteLine("\n*** EMPTY RESULT ***");
}

Console.WriteLine("\nDone.");
