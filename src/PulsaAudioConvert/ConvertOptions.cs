using Pulsa;

namespace PulsaAudioConvert;

public class ConvertOptions : IPulsaOptions
{
    /// <summary>감시할 디렉터리 경로</summary>
    public string WatchPath { get; set; } = ".";

    /// <summary>감시할 파일 패턴 (예: *.m4a, *.wav, *.flac)</summary>
    public string FilePattern { get; set; } = "*.m4a";

    /// <summary>출력 파일 확장자 (예: .mp3, .wav)</summary>
    public string OutputExtension { get; set; } = ".mp3";

    /// <summary>FFMpeg 오디오 코덱 (예: libmp3lame, aac, pcm_s16le)</summary>
    public string AudioCodec { get; set; } = "libmp3lame";

    /// <summary>오디오 비트레이트 (kbps)</summary>
    public int AudioBitrate { get; set; } = 192;

    /// <summary>변환 완료 후 원본 파일 삭제 여부</summary>
    public bool DeleteSource { get; set; } = false;

    /// <summary>파일 잠금 해제 대기 최대 횟수</summary>
    public int FileReadyRetries { get; set; } = 10;

    /// <summary>파일 잠금 해제 대기 간격 (밀리초)</summary>
    public int FileReadyRetryDelayMs { get; set; } = 500;

    /// <summary>미처리 파일 주기적 재스캔 간격 (초). 0이면 비활성화.</summary>
    public int RescanIntervalSeconds { get; set; } = 60;

    public string OutputWatchPattern => $"*{(OutputExtension.StartsWith('.') ? OutputExtension : $".{OutputExtension}")}";

    public string ResolveOutputPath(string filePath)
    {
        var ext = OutputExtension.StartsWith('.') ? OutputExtension : $".{OutputExtension}";
        return Path.ChangeExtension(filePath, ext);
    }

    public bool MatchesPattern(string filePath)
    {
        var patternExt = Path.GetExtension(FilePattern);
        return filePath.EndsWith(patternExt, StringComparison.OrdinalIgnoreCase);
    }
}
