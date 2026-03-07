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

    public string OutputWatchPattern => OutputPattern.Replace("{name}", "*").Replace("{ext}", "*");

    public string ResolveOutputPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath).TrimStart('.');
        var fileName = OutputPattern
            .Replace("{name}", name)
            .Replace("{ext}", ext);
        return Path.Combine(dir, fileName);
    }

    public bool MatchesPattern(string filePath)
    {
        var patternExt = Path.GetExtension(FilePattern);
        return filePath.EndsWith(patternExt, StringComparison.OrdinalIgnoreCase);
    }
}
