using Pulsa;

namespace PulsaSTT;

public class SttOptions : IPulsaOptions
{
    /// <summary>감시할 디렉터리 경로</summary>
    public string WatchPath { get; set; } = ".";

    /// <summary>감시할 파일 패턴 (예: *.mp3, *.wav)</summary>
    public string FilePattern { get; set; } = "*.mp3";

    /// <summary>
    /// 출력 파일 이름 패턴.
    /// {name} = 원본 파일명(확장자 제외), {ext} = 원본 확장자
    /// 출력 위치는 입력 파일과 동일한 디렉터리.
    /// </summary>
    public string OutputPattern { get; set; } = "{name}.stt.txt";

    /// <summary>
    /// 출력 포맷: text (기본), vtt (WebVTT), srt (SubRip).
    /// 쉼표로 여러 포맷 지정 가능 (예: "text,vtt,srt"), "all"은 전체 출력.
    /// </summary>
    public string OutputFormat { get; set; } = "text";

    /// <summary>LMSupply 모델명 (예: tiny, base, small, medium, large)</summary>
    public string Model { get; set; } = "large";

    /// <summary>전사 언어 코드 (예: ko, en, ja). 빈 문자열이면 자동 감지.</summary>
    public string Language { get; set; } = "";

    /// <summary>묵음 감지 임계값 (0.0 ~ 1.0)</summary>
    public float NoSpeechThreshold { get; set; } = 0.8f;

    /// <summary>파일 잠금 해제 대기 최대 횟수</summary>
    public int FileReadyRetries { get; set; } = 20;

    /// <summary>파일 잠금 해제 대기 간격 (밀리초)</summary>
    public int FileReadyRetryDelayMs { get; set; } = 500;

    /// <summary>미처리 파일 주기적 재스캔 간격 (초). 0이면 비활성화.</summary>
    public int RescanIntervalSeconds { get; set; } = 60;

    private static readonly string[] AllFormats = ["text", "vtt", "srt"];

    /// <summary>파싱된 출력 포맷 목록</summary>
    public IReadOnlyList<string> OutputFormats => ParseFormats();

    public string OutputWatchPattern => ResolvePatternForFormat(OutputFormats[0]).Replace("{name}", "*").Replace("{ext}", "*");

    /// <summary>첫 번째 포맷 기준 출력 경로 (존재 여부 판단용)</summary>
    public string ResolveOutputPath(string filePath)
        => ResolveOutputPath(filePath, OutputFormats[0]);

    /// <summary>특정 포맷의 출력 경로</summary>
    public string ResolveOutputPath(string filePath, string format)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).TrimStart('.');
        var fileName = ResolvePatternForFormat(format)
            .Replace("{name}", name)
            .Replace("{ext}", ext);
        return Path.Combine(dir, fileName);
    }

    private static string ResolvePatternForFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "vtt" => "{name}.vtt",
            "srt" => "{name}.srt",
            _ => "{name}.stt.txt",
        };
    }

    private IReadOnlyList<string> ParseFormats()
    {
        var raw = OutputFormat.Trim().ToLowerInvariant();
        if (raw is "all" or "*")
            return AllFormats;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => AllFormats.Contains(f))
            .Distinct()
            .ToArray() is { Length: > 0 } result ? result : ["text"];
    }

    public bool MatchesPattern(string filePath)
    {
        var patternExt = Path.GetExtension(FilePattern);
        return filePath.EndsWith(patternExt, StringComparison.OrdinalIgnoreCase);
    }
}
