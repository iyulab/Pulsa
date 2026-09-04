# Pulsa

[![Build](https://github.com/iyulab/Pulsa/actions/workflows/build.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/build.yml)
[![NuGet](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml)

### NuGet Packages

| Package | Version |
|---------|---------|
| Pulsa | [![NuGet](https://img.shields.io/nuget/v/Pulsa.svg)](https://www.nuget.org/packages/Pulsa) |

## VideoCompose

Compose a folder's images into a captioned, Ken-Burns-animated 16:9 video (no narration) — built as
the core library for [Filer](https://github.com/iyulab/filer)'s `video-composer` plugin, but
independent of it: `PulsaVideoCompose.SDK` has no knowledge of Filer and can be used standalone via
`PulsaVideoCompose.Cli`.

| Package | Version |
|---------|---------|
| PulsaVideoCompose.SDK | [![NuGet](https://img.shields.io/nuget/v/PulsaVideoCompose.SDK.svg)](https://www.nuget.org/packages/PulsaVideoCompose.SDK) |

### CLI usage

```bash
PulsaVideoCompose.Cli compose \
  --images scene1.png scene2.png scene3.png \
  --captions "First caption" "Second caption" "Third caption" \
  --scene-duration 4 \
  --output out.mp4 \
  --ffmpeg-dir ./ffmpeg
```

Prior versions of this repo hosted 5 unrelated file-watching automation tools (audio-convert, STT,
LLM, vault indexing, PDF diff) — archived at the
[`pre-video-compose-archive-2026-09-04`](https://github.com/iyulab/Pulsa/tree/pre-video-compose-archive-2026-09-04)
tag.

## 프로젝트 구조

```
src/
└── core/
    ├── Pulsa/                # 중앙 SDK (FileWatcher, UpdateService 등)
    └── Pulsa.Pipeline/       # 공용 파이프라인 SDK
```

## 빌드

```bash
dotnet build Pulsa.slnx -c Release
```

## License

[MIT](LICENSE)
