# Pulsa

[![Build](https://github.com/iyulab/Pulsa/actions/workflows/build.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/build.yml)
[![NuGet](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml)

## VideoCompose

Compose a folder's images into a captioned, Ken-Burns-animated 16:9 video (no narration) — built as
the core library for [Filer](https://github.com/iyulab/filer)'s `video-composer` plugin, but
independent of it: `PulsaVideoCompose.SDK` has no knowledge of Filer and can be used standalone via
`PulsaVideoCompose.Cli`.

### NuGet Packages

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

## Transcript

Refine a speech-to-text transcript (WebVTT) with a caller-supplied `IChatClient`:
`PulsaTranscript.SDK` has no knowledge of Filer and holds no credentials.

- The model sees only numbered cue texts and an optional glossary (names, titles, terms); it answers with
  the cues it would change. The transcript is reassembled by the library, so **cue count and timings never change**.
- Every correction passes a **sound-alike gate** (`CorrectionGate`): the cue is diffed word by word and each
  changed stretch must be close to what it replaces — Hangul is compared jamo by jamo. Inserted words, deleted
  words and replacements that do not sound like the original are refused and returned as suggestions instead.
- A cue the model marks unintelligible becomes `[unclear]`; every applied change is reported with its original.

```csharp
var cues = WebVtt.Parse(File.ReadAllText("meeting.vtt"));
var glossary = Glossary.FromMarkdown(File.ReadAllText("glossary.md"));
var result = await TranscriptRefiner.RefineAsync(chatClient, new RefineTranscriptRequest(cues, glossary));

File.WriteAllText("meeting.clean.vtt", WebVtt.Write(result.Cues));
// result.Applied  — changes made (index, original, refined, kind)
// result.Rejected — corrections the gate refused, for a person to decide
```

## 프로젝트 구조

```
src/
├── core/
│   └── Pulsa/                    # Pulsa — shared AI-abstraction base layer
│                                  # (carries Microsoft.Extensions.AI.Abstractions;
│                                  #  every Pulsa tool builds on it via ProjectReference)
├── sdk/
│   ├── Pulsa.Redact.SDK/         # PulsaRedact.SDK
│   ├── Pulsa.Transcript.SDK/     # PulsaTranscript.SDK
│   └── Pulsa.VideoCompose.SDK/   # PulsaVideoCompose.SDK
└── workers/
    └── Pulsa.VideoCompose.Cli/   # PulsaVideoCompose.Cli
```

## 빌드

```bash
dotnet build Pulsa.slnx -c Release
```

## License

[MIT](LICENSE)
