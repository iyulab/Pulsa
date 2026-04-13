# Pulsa

[![Build](https://github.com/iyulab/Pulsa/actions/workflows/build.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/build.yml)
[![NuGet](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml)

문서 처리 도구 플랫폼. 파일 감시 기반 자동화 도구와 웹 기반 인터랙티브 도구를 함께 제공합니다.

### NuGet Packages

| Package | Version |
|---------|---------|
| Pulsa | [![NuGet](https://img.shields.io/nuget/v/Pulsa.svg)](https://www.nuget.org/packages/Pulsa) |
| PulsaSTT.SDK | [![NuGet](https://img.shields.io/nuget/v/PulsaSTT.SDK.svg)](https://www.nuget.org/packages/PulsaSTT.SDK) |
| PulsaLLM.SDK | [![NuGet](https://img.shields.io/nuget/v/PulsaLLM.SDK.svg)](https://www.nuget.org/packages/PulsaLLM.SDK) |
| PulsaAudioConvert.SDK | [![NuGet](https://img.shields.io/nuget/v/PulsaAudioConvert.SDK.svg)](https://www.nuget.org/packages/PulsaAudioConvert.SDK) |
| PulsaPDFDiff.SDK | [![NuGet](https://img.shields.io/nuget/v/PulsaPDFDiff.SDK.svg)](https://www.nuget.org/packages/PulsaPDFDiff.SDK) |

## Apps

| App | 유형 | 설명 | 입력 | 출력 | 태그 |
|-----|------|------|------|------|------|
| **PulsaAudioConvert** | 자동화 | 오디오 파일 포맷 변환 | `*.m4a` | `*.mp3` | `audio-convert-v*` |
| **PulsaSTT** | 자동화 | 음성 파일을 텍스트로 변환 | `*.mp3` | `*.stt.txt`, `*.vtt`, `*.srt` | `stt-v*` |
| **PulsaLLM** | 자동화 | LLM 기반 텍스트 처리 | `*.stt.txt` | `*.{prompt}.md` | `llm-v*` |
| **PulsaVault** | 자동화+MCP | 문서 자동 인덱싱 및 시맨틱 검색 | `*.md`, `*.pdf`, `*.docx` 등 | 벡터 인덱스 | `vault-v*` |
| **PulsaPDFDiff** | 웹 | PDF 비교 (Vision LLM) | PDF × 2 | 마크다운 리포트 | `pdfdiff-v*` |

### 자동화 파이프라인

같은 폴더를 감시하도록 설정하면 자동으로 파이프라인이 구성됩니다:

```
*.m4a → [AudioConvert] → *.mp3 → [STT] → *.stt.txt → [LLM] → *.summarize.md
                                          → *.vtt
                                          → *.srt
```

### MCP 통합

PulsaVault는 MCP(Model Context Protocol) 서버로 동작하여 Claude Desktop, Claude Code 등에서 로컬 문서를 검색할 수 있습니다:

```
문서 폴더 → [PulsaVault] → 벡터 인덱스 → MCP search_knowledge_base → LLM
```

## 설치

[Releases](https://github.com/iyulab/Pulsa/releases) 페이지에서 앱별 zip 파일을 다운로드하여 압축 해제합니다. .NET 설치 없이 바로 실행 가능합니다 (self-contained).

각 앱은 시작 시 GitHub Releases를 확인하여 새 버전이 있으면 자동으로 업데이트합니다.

## 프로젝트 구조

```
src/
├── Pulsa/                                  # 중앙 SDK (FileWatcher, UpdateService 등)
└── tools/
    ├── PulsaAudioConvert/
    │   ├── PulsaAudioConvert.SDK/           # 오디오 변환 SDK
    │   └── PulsaAudioConvert.Worker/        # 자동화 앱
    ├── PulsaSTT/
    │   ├── PulsaSTT.SDK/                    # 음성인식 SDK
    │   └── PulsaSTT.Worker/                 # 자동화 앱
    ├── PulsaLLM/
    │   ├── PulsaLLM.SDK/                    # LLM 처리 SDK
    │   └── PulsaLLM.Worker/                 # 자동화 앱
    ├── PulsaVault/
    │   └── PulsaVault.Worker/               # 문서 인덱싱 + MCP 서버
    └── PulsaPDFDiff/
        ├── PulsaPDFDiff.SDK/                # PDF 비교 SDK
        └── PulsaPDFDiff.WebApp/             # 웹 앱
```

각 도구는 **SDK**(재사용 가능한 라이브러리)와 **App**(실행 앱)으로 분리됩니다:
- **SDK** — NuGet 패키지 배포 대상. 다른 프로젝트에서 참조하여 사용 가능
- **Worker** — 파일 감시 기반 자동화 실행 앱 (GitHub Releases 배포)
- **WebApp** — 웹 기반 사용자 인터랙션 앱 (GitHub Releases 배포)

## 앱별 문서

- [PulsaAudioConvert](src/workers/Pulsa.AudioConvert.Worker/README.md)
- [PulsaSTT](src/workers/Pulsa.STT.Worker/README.md)
- [PulsaLLM](src/workers/Pulsa.LLM.Worker/README.md)
- [PulsaVault](src/workers/Pulsa.Vault.Worker/README.md)
- [PulsaPDFDiff](docs/PulsaPDFDiff.md)

## 빠른 설정 예시

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
      "AudioBitrate": 192
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
    "Type": "openai",
    "Model": "gpt-4o",
    "ApiKey": "sk-..."
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

### PulsaVault

```json
{
  "Vault": {
    "Folders": [
      { "Path": "D:/documents", "IncludePatterns": ["*.md", "*.pdf", "*.docx"] }
    ]
  },
  "VectorStore": { "Provider": "SQLite" },
  "Embedding": { "Provider": "LMSupply" }
}
```

실행 모드:
```bash
PulsaVault                    # 백그라운드 sync만
PulsaVault --mcp-stdio        # Claude Desktop/Code용 MCP 서버
PulsaVault --mcp-sse          # HTTP/SSE MCP 서버
```

### PulsaPDFDiff

```bash
PulsaPDFDiff.WebApp.exe
# 브라우저에서 http://localhost:5000 접속
# 설정에서 OpenAI API Key 입력 → PDF 업로드 → 비교 실행
```

## 멀티 태스크 (자동화 앱)

자동화 앱들은 하나의 프로세스에서 여러 작업을 동시에 처리합니다:

```json
{
  "Tasks": [
    { "Name": "meetings", "WatchPath": "D:/recordings/meetings", "FilePattern": "*.mp3" },
    { "Name": "lectures", "WatchPath": "D:/recordings/lectures", "FilePattern": "*.mp3" }
  ]
}
```

## 자동 업데이트

모든 앱은 시작 시 GitHub Releases에서 새 버전을 확인하고 자동 업데이트합니다. 비활성화:

```json
{
  "Update": { "Enabled": false }
}
```

## 빌드

```bash
# 전체
dotnet build Pulsa.slnx -c Release

# 개별
dotnet build src/workers/Pulsa.AudioConvert.Worker/PulsaAudioConvert.Worker.csproj -c Release
dotnet build src/workers/Pulsa.STT.Worker/PulsaSTT.Worker.csproj -c Release
dotnet build src/workers/Pulsa.LLM.Worker/PulsaLLM.Worker.csproj -c Release
dotnet build src/workers/Pulsa.Vault.Worker/PulsaVault.Worker.csproj -c Release
dotnet build src/workers/Pulsa.PDFDiff.WebApp/PulsaPDFDiff.WebApp.csproj -c Release
```

## License

[MIT](LICENSE)
