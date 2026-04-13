namespace Pulsa;

public class UpdateOptions
{
    /// <summary>자동 업데이트 활성화 여부</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>GitHub 저장소 (owner/repo)</summary>
    public string Repository { get; set; } = "";

    /// <summary>릴리스 태그 접두사 (예: stt-v). 이 접두사로 시작하는 릴리스만 대상.</summary>
    public string TagPrefix { get; set; } = "";

    /// <summary>릴리스 에셋 파일명 패턴</summary>
    public string AssetPattern { get; set; } = "";
}
