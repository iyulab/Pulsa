# PulsaMedia

Pulsa's ffmpeg module: every Pulsa capability that needs [ffmpeg](https://ffmpeg.org/), in one package. Pass the
folder that contains the `ffmpeg` binary to the constructors.

- **Video composition** — images and captions into a captioned, Ken Burns–animated video (16:9 or 9:16) with a
  matching subtitle file, optional title and outro cards, and fonts loaded from a folder. The result reports a font
  that had to be substituted.
- **Redaction** — pixelates a region of an image, or of a video over a time range.

```csharp
using PulsaVideoCompose;

var composer = new FfmpegVideoComposer(ffmpegBinaryFolder: "./ffmpeg");
var result = await composer.ComposeAsync(new ComposeVideoRequest(
    ImagePaths: ["scene1.png", "scene2.png", "scene3.png"],
    Captions: ["First caption", "Second caption", "Third caption"],
    SceneDurationSeconds: 4,
    OutputPath: "out.mp4",
    AspectRatio: "16:9"));
// result.OutputPath — the video; result.SrtPath — the subtitle file
```

```csharp
using PulsaRedact;

var redactor = new MediaRedactor(ffmpegBinaryFolder: "./ffmpeg");
await redactor.RedactAsync(new RedactRequest(
    InputPath: "input.mp4", OutputPath: "redacted.mp4",
    X: 120, Y: 80, Width: 320, Height: 180,   // pixels, source resolution
    StartTime: 5, EndTime: 12));             // seconds; omit for the whole video or an image
```

Capabilities with no native dependency (transcript refinement, caption drafting) are in
[`Pulsa`](https://www.nuget.org/packages/Pulsa); a command line is in `PulsaMedia.Cli`. Contracts, design principles
and the changelog: [github.com/iyulab/Pulsa](https://github.com/iyulab/Pulsa). Pre-1.0: minor versions may change the
public surface.
