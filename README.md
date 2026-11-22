# Pulsa

[![Build](https://github.com/iyulab/Pulsa/actions/workflows/build.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/build.yml)
[![NuGet Publish](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/iyulab/Pulsa/actions/workflows/nuget-publish.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Parts for building AI agents and AI services — each one a self-contained capability that a product needs but should
not build into its own core.**

Pulsa is a set of components *for* AI: what uses them is an agent or an AI service. Each capability is split off on its
own and put behind a contract. It carries its own dependencies, is tested and versioned on its own,
and can run in its own process. The model, the configuration, the credentials and the host's policies stay with the
product that uses it. Most capabilities pair a model the caller supplies with the processing that makes it useful for
one kind of work: parsing, validation, media handling, and the guarantees a model alone cannot give. Every capability
has a role for a model. Either the model is required, or the capability runs deterministically without one and does
more when it is handed one. Redaction, for example, hides the regions it is told; given a vision model it can also
find what to hide.

A capability is defined by its contract, not by a programming language or by one way of calling it. What it has to do
is perform the part a consuming application needs, reliably. It reaches that application in whichever way suits it;
a capability offers the ways its consumers need, not necessarily all of them:

- **Library**: a per-language binding with a request/result API, called inside the consuming process.
- **CLI**: a standalone executable, called from another process or another language, and the way to keep a heavy or
  differently licensed dependency outside the host.
- **File system, asynchronously**: work handed over through folders and picked up later, for long jobs and for a
  capability that lives and fails apart from the application. The first capability that uses this way defines its
  contract.
- **MCP server**: the capability as tools and events for an agent. The channel connector has one (not yet published).

The implementation is written in the language that fits the problem. Every implementation and binding today is .NET,
published to NuGet; that is a current fact, not a rule. A capability in another language builds, versions and
publishes by that language's own conventions.

Reliability is what makes a capability worth taking: invariants are kept by the implementation, a result that cannot
keep them is refused with the reason, failures and retries are part of the contract, and each invariant has a test.

A Pulsa package is not an AI product on its own. It is a component: something a product assembles into its own
workflow.

### What belongs in Pulsa

Pulsa is the layer for reusable capabilities that an AI agent or an application needs but should not build into its
core. A capability belongs here when:

- **It is a reusable unit.** Its processing is substantial enough to test and version on its own, and another
  product could need the same thing.
- **It is a dependency boundary or needs its own process.** It brings dependencies (a native tool such as ffmpeg, a
  model format, a heavy library) that a host should take on only when it uses the capability, or a licence or a
  native runtime means it should run beside the host, not inside it. That is what the CLI, file-system and MCP ways
  are for.
- **It can be handed over as a contract.** Its inputs, results and invariants can be written down without reference
  to a language, and an agent could use that contract as a tool. It does not have to ship an MCP server: a capability
  offers the ways its consumers need.
- **It has a role for a model.** Handed an LLM, a vision model or another model handler, the capability can do more
  than it does without one. A capability with nothing for a model to do, however useful, is not admitted.

A single prompt or a single call is not a capability and stays in the application. A capability whose value comes
from one application's domain stays in that application's own plugin. Who accepts a new capability, and on what
evidence, is in [CONTRIBUTING.md](CONTRIBUTING.md).

## Packages

| Package | Description | Surfaces | NuGet |
|---|---|---|---|
| `Pulsa` | Capabilities that bring no dependency beyond the chat model you supply: refine a speech-to-text transcript (WebVTT) behind a sound-alike acceptance gate, and draft on-screen captions | SDK | [![NuGet](https://img.shields.io/nuget/v/Pulsa.svg)](https://www.nuget.org/packages/Pulsa) |
| `PulsaMedia` | Capabilities that need ffmpeg: compose images and captions into a captioned, Ken Burns–animated video, and pixelate a region of an image or a time range of a video | SDK | not yet published |
| `PulsaMedia.Cli` | `PulsaMedia` as an executable: `compose`, `draft-captions`, `redact` | CLI | not yet published |
| `PulsaChannel.SDK` | One-to-one messaging channel contract, in-memory fake provider, ordered event queue ([contract](docs/channel-connector.md)) | SDK | not yet published |
| `PulsaChannel.Telegram` | Telegram provider for the channel contract (Bot API over HTTPS) | SDK | not yet published |
| `PulsaChannel.Cli` | The channel connector as an executable: an MCP server (`serve`) and `send` · `status` · `listen` | MCP · CLI | not yet published |

A package holds the capabilities that share its dependencies (see the design principles). `PulsaVideoCompose.SDK`,
`PulsaRedact.SDK` and `PulsaTranscript.SDK` are retired: their capabilities are now in `PulsaMedia` and `Pulsa` — see
the [changelog](CHANGELOG.md).

The .NET packages target .NET 10. Pulsa is pre-1.0: minor versions may change the public surface.

## Installation

```bash
dotnet add package Pulsa        # transcript refinement, caption drafting
dotnet add package PulsaMedia   # video composition, redaction (ffmpeg)
```

`PulsaMedia` drives [ffmpeg](https://ffmpeg.org/); pass the folder that contains the `ffmpeg` binary to its
constructors. `Pulsa` has no native dependency.

## Usage

### Video composition — `PulsaMedia`

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

An optional title and outro card (`TitleImagePath`/`TitleText`, `OutroImagePath`/`OutroText`) open and close the
video with large centred text. How the burned-in text looks is set per composer — sizes are fractions of the frame
height, colours `#RRGGBB` or `#RRGGBBAA`:

```csharp
var composer = new FfmpegVideoComposer("./ffmpeg", new VideoComposeOptions(
    Captions: CaptionStyle.DefaultCaptions with { FontName = "Noto Sans KR", BoxColor = "#1A1A2ECC" },
    Cards: CaptionStyle.DefaultCards with { FontName = "Noto Sans KR", TextColor = "#FFD700" }));
```

A font does not have to be installed: `FontsDirectory` names a folder of font files (`.ttf`, `.otf`, `.ttc`) that
`FontName` can name by family, so a project can carry its own font to every machine that renders it. A family that is
found neither there nor among the installed fonts is drawn in a system font, and the result says so as far as the
renderer's choices show it (a bold that a variable font does not render is not caught — give bold its own static font
file):

```csharp
var composer = new FfmpegVideoComposer("./ffmpeg", new VideoComposeOptions(
    Captions: CaptionStyle.DefaultCaptions with { FontName = "IBM Plex Sans KR" },
    FontsDirectory: "./fonts"));
var result = await composer.ComposeAsync(request);

foreach (var s in result.FontSubstitutions ?? [])   // null: the check could not run
    Console.WriteLine($"{s.RequestedFamily} was not found; drawn in {s.UsedFont}");
```

Captions can be drafted from a short description with any `IChatClient` (`CaptionDrafter`, in `Pulsa`):

```csharp
var captions = await CaptionDrafter.DraftAsync(chatClient,
    new DraftCaptionsRequest(["scene1.png", "scene2.png", "scene3.png"], "A product demo video."));
```

A command-line front end is included (`PulsaMedia.Cli`, which also has `draft-captions` and `redact`):

```bash
PulsaMedia.Cli compose \
  --images scene1.png scene2.png scene3.png \
  --captions "First caption" "Second caption" "Third caption" \
  --scene-duration 4 \
  --output out.mp4 \
  --ffmpeg-dir ./ffmpeg \
  --aspect-ratio 9:16 \
  --title-image card.png --title-text "Our product" \
  --outro-image card.png --outro-text "Thanks for watching" \
  --font "Noto Sans KR" --fonts-dir ./fonts
```

### Redaction — `PulsaMedia`

```csharp
using PulsaRedact;

var redactor = new MediaRedactor(ffmpegBinaryFolder: "./ffmpeg");
var result = await redactor.RedactAsync(new RedactRequest(
    InputPath: "input.mp4",
    OutputPath: "redacted.mp4",
    Regions: [new RedactRegion(120, 80, 320, 180)],   // pixels, in the source's native resolution
    StartTime: 5, EndTime: 12));                     // seconds; video only — omit for the whole video or an image
```

Given a vision model (any `IChatClient` that takes images), redaction finds what to hide in an image. The code checks
every region the model returns: it is clipped to the picture, hidden only above the confidence bar, and a doubtful one
comes back in `result.Review` instead of being hidden:

```csharp
var detector = new ChatRegionDetector(visionClient, "faces and licence plates");
var result = await redactor.RedactAsync(new RedactRequest("photo.jpg", "photo.redacted.jpg", Regions: []),
    new RedactDetection(detector, MinimumConfidence: 0.6));
// result.Detected — hidden; result.Review — found but not hidden; result.Notes — refused boxes
```

### Transcript refinement — `Pulsa`

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

- **Packages follow dependencies; capabilities stay separate.** A package holds the capabilities that share its
  dependencies, so a host takes on a dependency only when it uses one of them; each capability keeps its own contract
  and can be adopted on its own. Several bindings of one capability are still one capability.
- **The caller owns the model.** A capability that uses a model takes it from the caller — in the .NET binding, an
  injected `IChatClient`. No package creates clients, reads configuration, or stores credentials.
- **Code holds the invariants.** Model output is parsed and validated deterministically — counts, timings,
  and structure are guaranteed by the implementation, not requested in a prompt, and the contract states them.
- **Host-agnostic.** No package knows about the application using it. A host written in any language takes a
  capability the same way, through whichever of its library, CLI, file-system or MCP ways it uses.
- **The contract sets the shape.** Each capability's contract — tools, events, inputs, results, invariants, errors —
  is written down without reference to a language, in `docs/`. Every way of reaching the capability follows it, so
  they never drift apart: [video composition](docs/video-compose.md), [caption drafting](docs/caption-draft.md),
  [redaction](docs/redact.md), [transcript refinement](docs/transcript-refine.md) and the
  [channel connector](docs/channel-connector.md).
- **The language fits the problem.** An implementation is written in the language that best fits what it has to
  do. The repository as a whole is not tied to one language.

## Repository layout

```
docs/                                # Language-neutral capability contracts
src/
├── core/Pulsa/                      # Pulsa — no dependency beyond the chat model
├── sdk/
│   ├── Pulsa.Channel.SDK/           # PulsaChannel.SDK
│   ├── Pulsa.Channel.Telegram/      # PulsaChannel.Telegram
│   └── Pulsa.Media/                 # PulsaMedia — ffmpeg
└── workers/
    ├── Pulsa.Channel.Cli/           # PulsaChannel.Cli
    └── Pulsa.Media.Cli/             # PulsaMedia.Cli
tests/                               # xUnit test projects
```

Everything under `src/` and `tests/` is .NET today; the layout for a second language is decided when the first
capability in one arrives.

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
