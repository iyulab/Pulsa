# PulsaAudioConvert

파일 감시 기반 오디오 포맷 변환 앱. FFmpeg를 사용하여 오디오 파일을 변환합니다.

## 사용법

1. `appsettings.json`에서 감시 폴더와 변환 옵션을 설정
2. 앱 실행

```
PulsaAudioConvert.exe
```

감시 폴더에 오디오 파일이 생성되면 자동으로 지정된 포맷으로 변환합니다.

## 요구사항

- [FFmpeg](https://ffmpeg.org/)가 PATH에 설치되어 있어야 합니다.

## 설정

```json
{
  "Convert": {
    "WatchPath": ".",
    "FilePattern": "*.m4a",
    "OutputExtension": ".mp3",
    "AudioCodec": "libmp3lame",
    "AudioBitrate": 192,
    "DeleteSource": false,
    "FileReadyRetries": 10,
    "FileReadyRetryDelayMs": 500,
    "RescanIntervalSeconds": 60
  }
}
```

| 설정 | 설명 | 기본값 |
|------|------|--------|
| `WatchPath` | 감시할 디렉터리 경로 | `.` |
| `FilePattern` | 입력 파일 패턴 | `*.m4a` |
| `OutputExtension` | 출력 파일 확장자 | `.mp3` |
| `AudioCodec` | FFmpeg 오디오 코덱 | `libmp3lame` |
| `AudioBitrate` | 오디오 비트레이트 (kbps) | `192` |
| `DeleteSource` | 변환 후 원본 파일 삭제 여부 | `false` |
| `RescanIntervalSeconds` | 미처리 파일 재스캔 간격 (초) | `60` |

## CLI 오버라이드

```bash
PulsaAudioConvert --Convert:WatchPath=/path/to/audio --Convert:AudioBitrate=320
```
