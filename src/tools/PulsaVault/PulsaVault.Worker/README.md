# PulsaVault

문서 자동 인덱싱 및 시맨틱 검색 앱. 지정된 폴더의 문서 파일을 감시하여 자동으로 벡터 인덱싱하고, MCP 서버로 시맨틱 검색을 제공합니다.

[FluxIndex.Extensions.FileVault](https://github.com/iyulab/FluxIndex)를 인프로세스로 호스팅합니다.

## 사용법

1. `appsettings.json`에서 감시 폴더, 벡터 스토어, 임베딩을 설정
2. 앱 실행

```bash
PulsaVault                     # 백그라운드 sync만 (MCP 없음)
PulsaVault --mcp-stdio         # MCP stdio 서버 (Claude Desktop/Code용)
PulsaVault --mcp-sse           # MCP HTTP/SSE 서버
PulsaVault --mcp-sse --port 8090  # 포트 지정
```

## 동작 방식

1. 시작 시 설정된 폴더를 FileVault에 등록하고 초기 전체 sync
2. 파일 생성 → 자동 memorize (텍스트 추출 → 청킹 → 임베딩 → 벡터 스토어 저장)
3. 파일 수정 → 자동 re-memorize
4. 파일 삭제 → 자동 unmemorize
5. MCP `search_knowledge_base` tool로 시맨틱 검색 제공

## 지원 문서 형식

PDF, DOCX, XLSX, PPTX, MD, TXT, HTML, HWP (FileFlux 제공)

## 설정

### 감시 폴더

```json
{
  "Vault": {
    "Folders": [
      {
        "Path": "D:/documents/notes",
        "IncludePatterns": ["*.md", "*.txt"],
        "ExcludePatterns": ["*.tmp", "~$*"],
        "Recursive": true
      },
      {
        "Path": "D:/documents/papers",
        "IncludePatterns": ["*.pdf", "*.docx"],
        "Recursive": true
      }
    ]
  }
}
```

| 설정 | 설명 | 기본값 |
|------|------|--------|
| `Path` | 감시할 폴더 경로 | (필수) |
| `IncludePatterns` | 포함할 파일 패턴 | `["*.pdf", "*.docx", "*.md", "*.txt"]` |
| `ExcludePatterns` | 제외할 파일 패턴 | `["*.tmp", "~$*"]` |
| `Recursive` | 하위 폴더 포함 여부 | `true` |

### FileVault

```json
{
  "FileVault": {
    "VaultBasePath": "./data/vault",
    "MaxFileSizeMB": 100,
    "EnableRealTimeWatch": true,
    "DebounceDelayMs": 500,
    "MaxConcurrentProcessing": 4
  }
}
```

| 설정 | 설명 | 기본값 |
|------|------|--------|
| `VaultBasePath` | vault 데이터 저장 경로 | `./data/vault` |
| `MaxFileSizeMB` | 최대 파일 크기 (초과 시 스킵) | `100` |
| `EnableRealTimeWatch` | 실시간 파일 감시 활성화 | `true` |
| `DebounceDelayMs` | 파일 변경 이벤트 디바운스 (ms) | `500` |
| `MaxConcurrentProcessing` | 최대 동시 처리 수 | `4` |

### 벡터 스토어

```json
{
  "VectorStore": {
    "Provider": "SQLite",
    "SQLite": {
      "DatabasePath": "./data/vault-vectors.db"
    },
    "PostgreSQL": {
      "ConnectionString": "Host=localhost;Database=pulsavault;Username=user;Password=pass"
    }
  }
}
```

| Provider | 설명 |
|----------|------|
| `SQLite` | 로컬 SQLite + sqlite-vec (기본값) |
| `PostgreSQL` | PostgreSQL + pgvector |

### 임베딩

```json
{
  "Embedding": {
    "Provider": "OpenAI",
    "OpenAI": {
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "sk-...",
      "Model": "text-embedding-3-small",
      "Dimension": 1536
    },
    "LMSupply": {
      "ModelId": "default"
    }
  }
}
```

| Provider | 설명 |
|----------|------|
| `OpenAI` | OpenAI 호환 API (OpenAI, Azure, GPUStack 등) |
| `LMSupply` | 로컬 ONNX 임베딩 (API 키 불필요, 모델 자동 다운로드) |

`Provider`를 빈 문자열로 두면 LMSupply로 fallback됩니다.

## MCP 서버

### stdio 모드 (Claude Desktop/Code)

```bash
PulsaVault --mcp-stdio
```

Claude Desktop `claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "pulsavault": {
      "command": "D:/path/to/PulsaVault.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

### SSE 모드 (HTTP 클라이언트)

```bash
PulsaVault --mcp-sse --port 3200
```

MCP 엔드포인트: `http://localhost:3200/mcp`

### MCP Tool

| Tool | 설명 |
|------|------|
| `search_knowledge_base` | 시맨틱 유사도 기반 문서 검색 |

파라미터:
| 이름 | 타입 | 설명 | 기본값 |
|------|------|------|--------|
| `query` | string | 검색 쿼리 | (필수) |
| `maxResults` | int | 최대 결과 수 | `10` |
| `minScore` | float | 최소 유사도 (0.0-1.0) | `0.0` |
| `pathScope` | string | 파일 경로 필터 | `null` |

## MCP 설정

```json
{
  "Mcp": {
    "Name": "PulsaVault",
    "Version": "0.1.0",
    "Instructions": "Knowledge base search server for local documents"
  }
}
```

## CLI 오버라이드

```bash
PulsaVault --Vault:Folders:0:Path=D:/docs --Embedding:Provider=OpenAI --Embedding:OpenAI:ApiKey=sk-...
```
