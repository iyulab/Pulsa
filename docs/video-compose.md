# Video composition — contract

Turns a sequence of images and one caption per image into a video. Each image is held as a scene with a slow Ken Burns
zoom and its caption burned in. Optional title and closing cards open and close the video, and a subtitle file comes with
it. Status: pre-1.0 — names and shapes may change in a minor version.

- **Module**: `PulsaMedia`, which brings ffmpeg. The caller names the folder that holds the `ffmpeg` binary; otherwise it
  is found on `PATH`.
- **Ways in**: library (`FfmpegVideoComposer.ComposeAsync`), CLI (`PulsaMedia.Cli compose`).
- **Model**: an extending role. Composition itself needs no model; given a chat model, the captions can be drafted
  first — [caption-draft.md](caption-draft.md), a capability of its own because it needs no ffmpeg.

## Input

| Field | Rule |
|---|---|
| images | at least 2 paths, in scene order |
| captions | exactly one per image, in the same order |
| scene duration | how many seconds each image is held |
| output | the video path; **must not exist**, because the composer never overwrites |
| aspect ratio | `16:9` (1920×1080, the default) or `9:16` (1080×1920); any other value is refused |
| title card · closing card | optional; an image and its text come together or not at all |

Each composer is configured with:

- frame size and frame rate;
- zoom speed and maximum zoom;
- the look of the captions and of the card text: font family, size as a fraction of the frame height, and colours as
  `#RRGGBB` or `#RRGGBBAA`;
- a folder of font files (`.ttf`, `.otf`, `.ttc`) whose families a style may name. The folder must exist.

## Result

`{ success, outputPath?, srtPath?, error?, fontSubstitutions? }`

- `srtPath` is the output path with the extension `.srt`. It holds the full transcript of what is on screen, including
  the title and closing cards.
- `fontSubstitutions` lists each family a style named that was not found, with the font the text was drawn in instead.
  The video is still written.
  - An empty list means no substitution was seen.
  - Null means the check could not run.
  - A family that was found but drawn at another weight (the bold of a variable font) is not reported.

## Invariants

- There is one scene per image, in the given order. Each caption is shown on its own scene and nowhere else.
- The cues of the subtitle file follow the scenes as they were rendered.
- An existing file at the output path is never replaced.
- A font that cannot be honoured is reported, not silently swapped.

## Failures

The composer throws nothing for a request it can judge. The result carries `success: false` and an `error` sentence when:

- there are fewer than 2 images;
- there is not exactly one caption per image;
- the output already exists;
- a card has its image without its text, or its text without its image;
- the aspect ratio is unknown;
- a style is invalid;
- the fonts folder is missing;
- ffmpeg fails.

Cancellation also ends in `success: false`, with the reason in `error`. A failed or cancelled run does not
promise to remove a partly written output.

**CLI**: on success the exit code is 0 and the output is `Wrote <video> (+ <srt>)`. Otherwise the exit code is 1 and the
error goes to standard error. Font substitutions are printed as warnings and do not change the exit code.
