# Pulsa

[![Build](https://github.com/iyulab/Pulsa/actions/workflows/build.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/build.yml)
[![NuGet Publish](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Reusable AI features for .NET — the parts an application needs but should not build into its core.**

Pulsa is a set of AI-backed features, each packaged so that more than one product can use it. A feature pairs
an AI capability, which Pulsa takes as an interface (a `Microsoft.Extensions.AI` `IChatClient` the caller
supplies), with the processing that makes it useful for one kind of work: parsing, validation, media handling,
and the guarantees a model alone cannot give. Every feature is exposed through the same interface pattern:

- **SDK**, always: a NuGet package with a request/result API.
- **CLI**, optional: a standalone executable for scripts, and for hosts that run the feature in its own process.
- **MCP server**, optional: the same feature as tools for an agent. None ships yet.

A Pulsa package is not an AI product on its own. It is a component: something a product assembles into its own
workflow.

### What belongs in Pulsa

An application that grows AI features runs into a middle case. Some features are too big or too specialised
for its core. Written as one of its own plugins, they would work only inside that application. Pulsa is the
layer for that case. A feature belongs here when:

- **It is a reusable unit.** Its processing is substantial enough to test and version on its own, and another
  product could need the same thing.
- **It is a dependency boundary.** It brings dependencies (a native tool such as ffmpeg, a model format, a
  heavy library) that a host should take on only when it uses the feature.
- **It needs its own process.** A licence or a native runtime means it should run beside the host, not inside
  it. That is what the CLI (and later MCP) surface is for.

Very light AI tasks (one prompt, one call) do not need a library. They stay in the application that uses them.
A feature whose value comes from one application's domain stays in that application's own plugin.

## Packages

| Package | Description | Surfaces | NuGet |
|---|---|---|---|
| `Pulsa` | Shared base layer; carries `Microsoft.Extensions.AI.Abstractions` for every SDK | — | [![NuGet](https://img.shields.io/nuget/v/Pulsa.svg)](https://www.nuget.org/packages/Pulsa) |
| `PulsaVideoCompose.SDK` | Compose images and captions into a captioned, Ken Burns–animated video (ffmpeg), with optional AI caption drafting | SDK · CLI | [![NuGet](https://img.shields.io/nuget/v/PulsaVideoCompose.SDK.svg)](https://www.nuget.org/packages/PulsaVideoCompose.SDK) |
| `PulsaRedact.SDK` | Pixelate a rectangular region of an image or a time range of a video (ffmpeg) | SDK | [![NuGet](https://img.shields.io/nuget/v/PulsaRedact.SDK.svg)](https://www.nuget.org/packages/PulsaRedact.SDK) |
| `PulsaTranscript.SDK` | Refine a speech-to-text transcript (WebVTT) with a chat model, behind a sound-alike acceptance gate | SDK | [![NuGet](https://img.shields.io/nuget/v/PulsaTranscript.SDK.svg)](https://www.nuget.org/packages/PulsaTranscript.SDK) |

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

- **One feature per package.** Each package does one job and can be adopted on its own, with only the
  dependencies that job needs.
- **The caller owns the model.** AI steps take an injected `IChatClient`; no package creates clients,
  reads configuration, or stores credentials.
- **Code holds the invariants.** Model output is parsed and validated deterministically — counts, timings,
  and structure are guaranteed by the library, not requested in a prompt.
- **Host-agnostic.** No package knows about the application using it. [Filer](https://filer-ai.com) uses
  them through its plugins, and any other .NET host can do the same.
- **The same shape everywhere.** Each feature has a request/result API in its SDK. The CLI and MCP surfaces
  wrap that API, so the three never drift apart.

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
