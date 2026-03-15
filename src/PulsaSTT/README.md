# PulsaSTT

파일 감시 기반 음성 인식(STT) 앱. Whisper 모델(ONNX Runtime)을 사용하며 GPU가 있으면 자동으로 CUDA 가속을 활용합니다.

## 사용법

1. `appsettings.json`에서 감시 폴더와 옵션을 설정
2. 앱 실행

```
PulsaSTT.exe
```

감시 폴더에 오디오 파일이 생성되면 자동으로 텍스트 변환 후 결과를 저장합니다.

## 설정

`Tasks` 배열에 하나 이상의 작업을 정의합니다. 하나의 프로세스에서 여러 폴더/설정을 동시에 감시할 수 있습니다.

```json
{
  "Tasks": [
    {
      "Name": "default",
      "WatchPath": ".",
      "FilePattern": "*.mp3",
      "OutputPattern": "{name}.stt.txt",
      "OutputFormat": "text",
      "Model": "large",
      "Language": "ko",
      "NoSpeechThreshold": 0.6
    }
  ]
}
```

| 설정 | 설명 | 기본값 |
|------|------|--------|
| `Name` | 작업 이름 (로그 식별용, 선택) | |
| `WatchPath` | 감시할 디렉터리 경로 | `.` |
| `FilePattern` | 입력 파일 패턴 | `*.mp3` |
| `OutputPattern` | 텍스트 출력 파일명 패턴 (`{name}` = 원본 파일명) | `{name}.stt.txt` |
| `OutputFormat` | 출력 포맷 (아래 참조) | `text` |
| `Model` | Whisper 모델명 | `large` |
| `Language` | 언어 코드 (빈 문자열이면 자동 감지) | `""` |
| `NoSpeechThreshold` | 묵음 감지 임계값 (0.0 ~ 1.0) | `0.8` |
| `RescanIntervalSeconds` | 미처리 파일 재스캔 간격 (초, 0이면 비활성화) | `60` |

### 멀티 태스크 예시

```json
{
  "Tasks": [
    {
      "Name": "meetings-ko",
      "WatchPath": "D:/recordings/meetings",
      "FilePattern": "*.mp3",
      "OutputFormat": "all",
      "Model": "large",
      "Language": "ko"
    },
    {
      "Name": "english-content",
      "WatchPath": "D:/recordings/english",
      "FilePattern": "*.mp3",
      "OutputFormat": "text",
      "Model": "large",
      "Language": "en"
    }
  ]
}
```

같은 모델을 사용하는 작업들은 모델 인스턴스를 자동으로 공유합니다.

## 출력 포맷

`OutputFormat` 설정으로 출력 형식을 지정합니다. STT는 한 번만 실행되고 지정된 포맷으로 각각 저장됩니다.

| OutputFormat | 출력 파일 | 설명 |
|---|---|---|
| `text` | `{name}.stt.txt` | 순수 텍스트 (타임스탬프 없음) |
| `vtt` | `{name}.vtt` | WebVTT 자막 |
| `srt` | `{name}.srt` | SubRip 자막 |

### 복수 포맷 동시 출력

쉼표로 여러 포맷을 지정하거나 `all`로 전체 출력할 수 있습니다:

```json
"OutputFormat": "all"           // text + vtt + srt 동시 출력
"OutputFormat": "text,vtt"      // text + vtt만
"OutputFormat": "vtt,srt"       // vtt + srt만
"OutputFormat": "vtt"           // vtt만
```

### VTT 출력 예시

```
WEBVTT

00:00:00.000 --> 00:00:30.000
제목을 3개, 3문 좀 어려운 말로 했습니다...

00:00:30.000 --> 00:01:00.000
세 번째 문을 열고 나니까 마지막 세계가 나오는데...

00:03:30.000 --> 00:03:45.139
바울은 최고의 학벌을 가지고 있고, 최고의 명예를 가지고 있고...
```

세그먼트 경계는 Whisper의 WordTimestamps 기능으로 음성의 자연스러운 호흡/문장 단위로 분할됩니다.

## 모델

| 모델 | 파라미터 | 크기 | 속도 | 설명 |
|------|---------|------|------|------|
| `tiny` | 39M | ~150MB | 가장 빠름 | 빠른 테스트용 |
| `base` | 74M | ~290MB | 빠름 | 가벼운 용도 |
| `small` | 244M | ~970MB | 보통 | 균형 |
| `large` | 1550M | ~6.4GB | 느림 | 최고 품질 |
| `turbo` | 809M | ~3.2GB | 빠름 | Large V3 Turbo |
| `large-ko` | 809M | ~3.2GB | 빠름 | 한국어 특화 Turbo |

모델은 첫 실행 시 자동으로 다운로드됩니다 (HuggingFace Hub 캐시).

## GPU 가속

CUDA 지원 GPU가 있으면 자동으로 GPU 가속이 활성화됩니다. 로그에서 확인:

```
STT model loaded. GPU: True [CUDAExecutionProvider, CPUExecutionProvider]
```

## CLI 오버라이드

`appsettings.json` 설정을 커맨드라인에서 오버라이드할 수 있습니다:

```bash
PulsaSTT --Tasks:0:WatchPath=/path/to/audio --Tasks:0:OutputFormat=all --Tasks:0:Language=ko
```
