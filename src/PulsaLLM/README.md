# PulsaLLM

파일 감시 기반 LLM 텍스트 처리 앱. 지정된 폴더의 텍스트 파일을 감시하고, prompt 파일에 정의된 지시에 따라 LLM으로 처리하여 결과를 저장합니다.

## 사용법

1. `appsettings.json`에서 감시 폴더와 provider를 설정
2. prompt 파일 작성 (예: `SUMMARIZE-PROMPT.md`)
3. 앱 실행

```
PulsaLLM.exe
```

감시 폴더에 입력 파일이 생성되면 자동으로 LLM 처리 후 결과를 저장합니다.

## 설정

Provider는 전역으로 설정하고, `Tasks` 배열에 작업별 감시 폴더와 prompt를 정의합니다.

```json
{
  "Provider": {
    "Type": "openai-compatible",
    "Host": "http://localhost:1234",
    "Model": "local-model",
    "MaxTokens": 4096
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

### Provider 설정

| 설정 | 설명 | 기본값 |
|------|------|--------|
| `Provider.Type` | `openai`, `openai-compatible` | `local` |
| `Provider.Model` | 모델명 | `default` |
| `Provider.ApiKey` | API 키 (openai 계열) | |
| `Provider.Host` | API 엔드포인트 (openai-compatible) | |
| `Provider.PathPrefix` | API 경로 접두사 | `/v1` |
| `Provider.MaxTokens` | 최대 생성 토큰 수 | `4096` |
| `Provider.ContextWindow` | 모델 컨텍스트 윈도우 크기 (입력 자동 truncation용) | `0` (비활성화) |
| `Provider.Temperature` | Temperature (0.0-2.0) | provider 기본값 |

### Task 설정

| 설정 | 설명 | 기본값 |
|------|------|--------|
| `Name` | 작업 이름 (로그 식별용, 선택) | |
| `WatchPath` | 감시할 디렉터리 경로 | `.` |
| `FilePattern` | 입력 파일 패턴 | `*.stt.txt` |
| `PromptFile` | prompt 파일 경로 (앱 디렉터리 기준 상대 경로) | `SUMMARIZE-PROMPT.md` |
| `RescanIntervalSeconds` | 미처리 파일 재스캔 간격 (초) | `60` |

### 멀티 태스크 예시

하나의 프로세스에서 서로 다른 폴더와 prompt로 여러 작업을 동시에 처리합니다:

```json
{
  "Provider": {
    "Type": "openai",
    "Model": "gpt-4o",
    "ApiKey": "sk-..."
  },
  "Tasks": [
    {
      "Name": "meetings",
      "WatchPath": "D:/recordings/meetings",
      "FilePattern": "*.stt.txt",
      "PromptFile": "SUMMARIZE-PROMPT.md"
    },
    {
      "Name": "sermons",
      "WatchPath": "D:/recordings/sermons",
      "FilePattern": "*.stt.txt",
      "PromptFile": "SERMON-PROMPT.md"
    }
  ]
}
```

## Provider

### openai

```json
{
  "Provider": {
    "Type": "openai",
    "Model": "gpt-4o",
    "ApiKey": "sk-..."
  }
}
```

### openai-compatible

LM Studio, Ollama, vLLM, GPUStack 등 OpenAI 호환 API를 사용합니다.

```json
{
  "Provider": {
    "Type": "openai-compatible",
    "Host": "http://localhost:1234",
    "Model": "local-model",
    "ApiKey": ""
  }
}
```

## Prompt 파일

prompt 파일은 YAML frontmatter와 본문으로 구성됩니다. frontmatter는 선택 사항이며, 전역 `Provider` 설정을 작업별로 오버라이드합니다.

```markdown
---
model: gpt-4o
max_tokens: 4096
---
다음 설교 내용을 요약해주세요.

## 요약 형식
- 제목
- 핵심 메시지 (1-2문장)
- 주요 성경 구절
- 핵심 포인트 (번호 목록)
```

### 지원하는 frontmatter 키

| 키 | 설명 |
|----|------|
| `model` | 모델명 오버라이드 |
| `max_tokens` | 최대 토큰 수 오버라이드 |
| `context_window` | 컨텍스트 윈도우 크기 오버라이드 |
| `provider` | provider type 오버라이드 |
| `api_key` | API 키 오버라이드 |
| `host` | API 호스트 오버라이드 |
| `path_prefix` | API 경로 접두사 오버라이드 |
| `temperature` | Temperature 오버라이드 |

## Output 파일명 규칙

prompt 파일명에서 자동으로 output 접미사가 결정됩니다:

| Prompt 파일 | Output 패턴 |
|-------------|------------|
| `SUMMARIZE-PROMPT.md` | `{name}.summarize.md` |
| `ANALYZE-PROMPT.md` | `{name}.analyze.md` |
| `TRANSLATE-PROMPT.md` | `{name}.translate.md` |

입력 파일명의 첫 번째 `.` 앞까지가 `{name}`이 됩니다:
- `설교.stt.txt` → `설교.summarize.md`

## CLI 오버라이드

```bash
PulsaLLM --Provider:Type=openai --Provider:ApiKey=sk-... --Tasks:0:WatchPath=/path/to/files
```
