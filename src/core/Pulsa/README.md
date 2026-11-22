# Pulsa

Pulsa's module of capabilities that bring no dependency beyond the chat model you supply (any
`Microsoft.Extensions.AI` `IChatClient`). The model, its configuration and credentials stay with your application.

- **Transcript refinement** — corrects a speech-to-text transcript (WebVTT) against a glossary of names and terms.
  Every correction must pass a sound-alike gate; refused corrections come back for human review. Cue count and
  timings never change.
- **Caption drafting** — drafts one on-screen caption per scene from a short description.

```csharp
using PulsaTranscript;

var cues = WebVtt.Parse(File.ReadAllText("meeting.vtt"));
var glossary = Glossary.FromMarkdown(File.ReadAllText("glossary.md"));

var result = await TranscriptRefiner.RefineAsync(chatClient, new RefineTranscriptRequest(cues, glossary));

File.WriteAllText("meeting.clean.vtt", WebVtt.Write(result.Cues));
// result.Applied  — changes made
// result.Rejected — corrections the gate refused, for human review
```

```csharp
var captions = await CaptionDrafter.DraftAsync(chatClient,
    new DraftCaptionsRequest(["scene1.png", "scene2.png", "scene3.png"], "A product demo video."));
```

Capabilities that need ffmpeg (video composition, redaction) are in
[`PulsaMedia`](https://www.nuget.org/packages/PulsaMedia). Contracts, design principles and the changelog:
[github.com/iyulab/Pulsa](https://github.com/iyulab/Pulsa). Pre-1.0: minor versions may change the public surface.
