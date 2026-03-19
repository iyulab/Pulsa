# PulsaPDFDiff

웹 기반 PDF 비교 도구. 두 PDF 문서(기준 vs 작업)를 OpenAI Vision API로 비교하여 오타 중심 교정 리포트를 생성합니다.

## 사용법

```bash
PulsaPDFDiff.WebApp.exe
```

브라우저에서 `http://localhost:5000` 접속 후:

1. **설정** (⚙) — OpenAI API Key 입력, 모델 선택
2. **기준 문서** — 비교 기준이 되는 PDF 업로드 (드래그 또는 클릭)
3. **작업 문서** — 교정 대상 PDF 업로드
4. **프롬프트 선택** — 내장 프롬프트 또는 직접 편집
5. **비교 실행** — LLM이 두 문서를 분석하여 마크다운 리포트 생성

## 설정

### appsettings.json

```json
{
  "OpenAI": {
    "ApiKey": "",
    "Model": "gpt-4o"
  },
  "Update": {
    "Enabled": true,
    "Repository": "iyulab/Pulsa",
    "TagPrefix": "pdfdiff-v",
    "AssetPattern": "PulsaPDFDiff-win-x64.zip"
  },
  "LogsPath": "logs"
}
```

API Key와 모델은 웹 UI의 설정 모달에서도 변경할 수 있습니다. UI에서 변경한 설정은 `appsettings.user.json`에 저장됩니다.

### 모델 선택

설정 모달에서 OpenAI `/v1/models` API를 통해 사용 가능한 모델 목록을 자동으로 불러옵니다. Vision 지원 모델을 선택하세요:

- `gpt-4o` (권장)
- `gpt-4o-mini` (빠르고 저렴)
- `gpt-4-turbo`

## 프롬프트

`prompts/` 디렉토리에 `.txt` 파일로 관리됩니다. 웹 UI에서 선택하거나 편집할 수 있습니다.

### 내장 프롬프트

**calendar-diff** — 달력 인쇄물 교정용. 교회력, 음력, 행사력, 날짜 숫자 등을 페이지별로 비교합니다.

### 커스텀 프롬프트 작성

`prompts/` 디렉토리에 `.txt` 파일을 추가하면 UI 드롭다운에 자동 표시됩니다. 프롬프트는 LLM의 system prompt로 전달됩니다.

```
당신은 계약서 교정 전문가입니다.
기준 문서와 작업 문서를 비교하여 다음을 확인하세요:
1. 금액, 날짜, 이름 등 핵심 데이터 일치 여부
2. 조항 번호 순서 정확성
3. 오탈자
...
```

## API

| Method | Path | 설명 |
|--------|------|------|
| `POST` | `/api/compare` | PDF 2개 + 프롬프트 → 마크다운 리포트 |
| `GET` | `/api/prompts` | 프롬프트 목록 |
| `GET` | `/api/prompts/{name}` | 프롬프트 내용 |
| `PUT` | `/api/prompts/{name}` | 프롬프트 저장 |
| `GET` | `/api/models` | OpenAI 모델 목록 |
| `GET` | `/api/settings` | 현재 설정 (API key 마스킹) |
| `PUT` | `/api/settings` | 설정 변경 |

### POST /api/compare

```
Content-Type: multipart/form-data

Fields:
  reference: (PDF file)        # 기준 문서
  target: (PDF file)           # 작업 문서
  prompt: "calendar-diff"      # 프롬프트 이름
  customPrompt: "..."          # (선택) 직접 입력 프롬프트
```

응답: `text/markdown` (교정 리포트)

## 아키텍처

```
PulsaPDFDiff.SDK/
  PdfImageConverter.cs    — PDF → PNG 이미지 변환 (Docnet.Core)
  VisionComparer.cs       — OpenAI Vision API 호출
  PromptManager.cs        — 프롬프트 파일 CRUD
  SettingsManager.cs      — appsettings.user.json 관리
  OpenAIOptions.cs        — 설정 모델

PulsaPDFDiff.WebApp/
  Program.cs              — Minimal API 엔드포인트
  wwwroot/                — 프론트엔드 (HTML/CSS/JS)
  prompts/                — 내장 프롬프트 파일
```

## 제한 사항

- Windows 전용 (`System.Drawing.Common` 사용)
- PDF 이미지 변환에 PDFium 네이티브 라이브러리 필요 (Docnet.Core에 포함)
- 대용량 PDF(20+ 페이지)는 Vision API 토큰 소비가 큽니다
