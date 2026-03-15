# Pulsa

파일 감시 기반 자동화 도구 모음. 지정된 폴더를 감시하며 새 파일이 감지되면 자동으로 처리합니다.

## Apps

| App | 설명 | 입력 | 출력 | 릴리즈 태그 |
|-----|------|------|------|------------|
| **PulsaAudioConvert** | 오디오 파일 포맷 변환 | `*.m4a` | `*.mp3` | `audio-convert-v*` |
| **PulsaSTT** | 음성 파일을 텍스트로 변환 | `*.mp3` | `*.stt.txt`, `*.vtt`, `*.srt` | `stt-v*` |
| **PulsaLLM** | LLM 기반 텍스트 처리 | `*.stt.txt` | `*.{prompt}.md` | `llm-v*` |

### 파이프라인 예시

같은 폴더를 감시하도록 설정하면 자동으로 파이프라인이 구성됩니다:

```
*.m4a → [PulsaAudioConvert] → *.mp3 → [PulsaSTT] → *.stt.txt → [PulsaLLM] → *.summarize.md
                                                   → *.vtt
                                                   → *.srt
```

## 멀티 태스크

각 앱은 하나의 프로세스에서 여러 작업을 동시에 처리할 수 있습니다. `Tasks` 배열에 작업을 정의하면 각 작업마다 독립적으로 폴더를 감시하고, 공유 큐를 통해 순차적으로 처리합니다.

```json
{
  "Tasks": [
    { "Name": "meetings", "WatchPath": "D:/recordings/meetings", "FilePattern": "*.mp3" },
    { "Name": "lectures", "WatchPath": "D:/recordings/lectures", "FilePattern": "*.mp3" }
  ]
}
```

## 프로젝트 구조

```
src/
├── Pulsa/                  # 공유 라이브러리 (FileWatcher, UpdateService, FileQueue 등)
├── PulsaAudioConvert/      # 오디오 변환 앱
├── PulsaSTT/               # 음성 인식 앱 (Whisper ONNX + GPU)
└── PulsaLLM/               # LLM 텍스트 처리 앱
```

## 설치

[Releases](https://github.com/iyulab/Pulsa/releases) 페이지에서 앱별 zip 파일을 다운로드하여 원하는 경로에 압축 해제합니다.

## 설정

각 앱의 `appsettings.json`에서 설정을 변경합니다. 자세한 설정은 각 모듈의 README를 참고하세요.

- [PulsaAudioConvert](src/PulsaAudioConvert/README.md)
- [PulsaSTT](src/PulsaSTT/README.md)
- [PulsaLLM](src/PulsaLLM/README.md)

### PulsaAudioConvert

```json
{
  "Tasks": [
    {
      "Name": "default",
      "WatchPath": ".",
      "FilePattern": "*.m4a",
      "OutputExtension": ".mp3",
      "AudioCodec": "libmp3lame",
      "AudioBitrate": 192,
      "DeleteSource": false
    }
  ]
}
```

### PulsaSTT

```json
{
  "Tasks": [
    {
      "Name": "default",
      "WatchPath": ".",
      "FilePattern": "*.mp3",
      "OutputFormat": "all",
      "Model": "large",
      "Language": "ko"
    }
  ]
}
```

### PulsaLLM

```json
{
  "Provider": {
    "Type": "openai-compatible",
    "Host": "http://localhost:1234",
    "Model": "local-model"
  },
  "Tasks": [
    {
      "Name": "default",
      "WatchPath": ".",
      "FilePattern": "*.stt.txt",
      "PromptFile": "SUMMARIZE-PROMPT.md"
    }
  ]
}
```

Provider 종류: `openai`, `openai-compatible`

OpenAI 사용 시:
```json
{
  "Provider": {
    "Type": "openai",
    "Model": "gpt-4o",
    "ApiKey": "sk-..."
  }
}
```

## 자동 업데이트

각 앱은 시작 시 GitHub Releases를 확인하여 새 버전이 있으면 자동으로 업데이트합니다. `appsettings.json`에서 비활성화할 수 있습니다.

```json
{
  "Update": {
    "Enabled": false
  }
}
```

## 빌드

```bash
dotnet build src/PulsaAudioConvert/PulsaAudioConvert.csproj -c Release
dotnet build src/PulsaSTT/PulsaSTT.csproj -c Release
dotnet build src/PulsaLLM/PulsaLLM.csproj -c Release
```

## License

[MIT](LICENSE)
