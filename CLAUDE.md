# Pulsa Project Instructions

## Version Bump & Release

버전을 올릴 때 반드시 해당 앱의 git 태그를 생성하고 푸시한다.

### 태그 네이밍 규칙
- PulsaSTT: `stt-v{version}` (예: `stt-v0.1.7`)
- PulsaLLM: `llm-v{version}` (예: `llm-v0.1.7`)
- PulsaAudioConvert: `audio-convert-v{version}` (예: `audio-convert-v0.1.7`)

### 절차
1. csproj의 `<Version>` 업데이트
2. 커밋
3. 태그 생성: `git tag {prefix}-v{version}`
4. 푸시: `git push && git push origin {prefix}-v{version}`

태그 푸시가 GitHub Actions release workflow를 트리거한다.
