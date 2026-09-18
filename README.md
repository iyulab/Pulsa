# Pulsa

[![Build](https://github.com/iyulab/Pulsa/actions/workflows/build.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/build.yml)
[![NuGet Publish](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**AI-powered media and document actions for .NET.** Pulsa is a family of small, independent SDKs, each
performing one concrete action on files — composing a video, redacting a region, refining a transcript.
Where an action needs a language model, the caller supplies it as a `Microsoft.Extensions.AI`
`IChatClient`; Pulsa never constructs a client or holds credentials.

## Packages

| Package | Description | NuGet |
|---|---|---|
| `Pulsa` | Shared base layer; carries `Microsoft.Extensions.AI.Abstractions` for every SDK | [![NuGet](https://img.shields.io/nuget/v/Pulsa.svg)](https://www.nuget.org/packages/Pulsa) |
| `PulsaVideoCompose.SDK` | Compose images and captions into a captioned, Ken Burns–animated video (ffmpeg), with optional AI caption drafting | [![NuGet](https://img.shields.io/nuget/v/PulsaVideoCompose.SDK.svg)](https://www.nuget.org/packages/PulsaVideoCompose.SDK) |
| `PulsaRedact.SDK` | Pixelate a rectangular region of an image or a time range of a video (ffmpeg) | [![NuGet](https://img.shields.io/nuget/v/PulsaRedact.SDK.svg)](https://www.nuget.org/packages/PulsaRedact.SDK) |
| `PulsaTranscript.SDK` | Refine a speech-to-text transcript (WebVTT) with a chat model, behind a sound-alike acceptance gate | [![NuGet](https://img.shields.io/nuget/v/PulsaTranscript.SDK.svg)](https://www.nuget.org/packages/PulsaTranscript.SDK) |

All packages target .NET 10. Pulsa is pre-1.0: minor versions may change the public surface.

## Installation

```bash
dotnet add package PulsaVideoCompose.SDK
dotnet add package PulsaRedact.SDK
dotnet add package PulsaTranscript.SDK
```

The video and redaction SDKs drive [ffmpeg](https://ffmpeg.org/); pass the folder that contains the
`ffmpeg` binary to their constructors. `PulsaTranscript.SDK` has no native dependency.

## Usage

### Video composition — `PulsaVideoCompose.SDK`

```csharp
using PulsaVideoCompose;

var composer = new FfmpegVideoComposer(ffmpegBinaryFolder: "./ffmpeg");
var result = await composer.ComposeAsync(new ComposeVideoRequest(
    ImagePaths: ["scene1.png", "scene2.png", "scene3.png"],
    Captions: ["First caption", "Second caption", "Third caption"],
    SceneDurationSeconds: 4,
    OutputPath: "out.mp4",
    AspectRatio: "16:9"));   // or "9:16"

// result.OutputPath — the video; result.SrtPath — the matching subtitle file
```

Captions can be drafted from a short description with any `IChatClient`:

```csharp
var captions = await CaptionDrafter.DraftAsync(chatClient,
    new DraftCaptionsRequest(["scene1.png", "scene2.png", "scene3.png"], "A product demo video."));
```

A command-line front end is included (`PulsaVideoCompose.Cli`):

```bash
PulsaVideoCompose.Cli compose \
  --images scene1.png scene2.png scene3.png \
  --captions "First caption" "Second caption" "Third caption" \
  --scene-duration 4 \
  --output out.mp4 \
  --ffmpeg-dir ./ffmpeg
```

### Redaction — `PulsaRedact.SDK`

```csharp
using PulsaRedact;

var redactor = new MediaRedactor(ffmpegBinaryFolder: "./ffmpeg");
var result = await redactor.RedactAsync(new RedactRequest(
    InputPath: "input.mp4",
    OutputPath: "redacted.mp4",
    X: 120, Y: 80, Width: 320, Height: 180,   // pixels, in the source's native resolution
    StartTime: 5, EndTime: 12));             // seconds; video only — omit for the whole video or an image
```

### Transcript refinement — `PulsaTranscript.SDK`

```csharp
using PulsaTranscript;

var cues = WebVtt.Parse(File.ReadAllText("meeting.vtt"));
var glossary = Glossary.FromMarkdown(File.ReadAllText("glossary.md"));   // names, titles, terms

var result = await TranscriptRefiner.RefineAsync(chatClient, new RefineTranscriptRequest(cues, glossary));

File.WriteAllText("meeting.clean.vtt", WebVtt.Write(result.Cues));
// result.Applied  — changes made (index, original, refined, kind)
// result.Rejected — corrections the gate refused, returned for human review
```

- The model receives only numbered cue texts and the glossary, and answers with the cues it would change.
  The library reassembles the transcript, so **cue count and timings never change**.
- Every correction must pass `CorrectionGate`: the cue is diffed word by word, and each changed span must
  sound like the span it replaces (Hangul is compared jamo by jamo). Insertions, deletions, and
  replacements that do not resemble the original are refused and reported in `Rejected`.
- A cue the model marks as unintelligible becomes `[unclear]`. Long transcripts are sent in batches
  (`RefineTranscriptOptions.BatchSize`).

## Design principles

- **One action per SDK.** Each package does one job and can be adopted on its own.
- **The caller owns the model.** AI steps take an injected `IChatClient`; no package creates clients,
  reads configuration, or stores credentials.
- **Code holds the invariants.** Model output is parsed and validated deterministically — counts, timings,
  and structure are guaranteed by the library, not requested in a prompt.
- **Host-agnostic.** No SDK knows about any particular application; [Filer](https://github.com/iyulab/filer)
  consumes them as plugins, and any other .NET host can do the same.

## Repository layout

```
src/
├── core/Pulsa/                      # Shared base layer
├── sdk/
│   ├── Pulsa.Redact.SDK/            # PulsaRedact.SDK
│   ├── Pulsa.Transcript.SDK/        # PulsaTranscript.SDK
│   └── Pulsa.VideoCompose.SDK/      # PulsaVideoCompose.SDK
└── workers/
    └── Pulsa.VideoCompose.Cli/      # PulsaVideoCompose.Cli
tests/                               # xUnit test projects, one per SDK
```

## Building and testing

```bash
dotnet build Pulsa.slnx -c Release
dotnet test Pulsa.slnx -c Release
```

The redaction tests include live ffmpeg round-trips and need `ffmpeg` on `PATH`.

## Contributing

Issues and pull requests are welcome. Please open an issue to discuss substantial changes first, keep
each SDK free of host-specific concepts, and include tests with every behavior change.

## History

Versions of this repository before September 2026 hosted a different project — a set of file-watching
automation tools (audio conversion, speech-to-text, LLM processing, vault indexing, PDF diff). That code is
preserved at the [`pre-video-compose-archive-2026-09-04`](https://github.com/iyulab/Pulsa/tree/pre-video-compose-archive-2026-09-04) tag.

## License

[MIT](LICENSE) © iyulab
