# Redaction — contract

Pixelates rectangular regions of an image, or of a video over a time range, and writes the result to a new file. Given
a vision or detection model, it finds the regions to hide in an image.
Status: pre-1.0 — names and shapes may change in a minor version.

- **Module**: `PulsaMedia`, which brings ffmpeg. The caller names its folder, or it is found on `PATH`.
- **Ways in**: library (`MediaRedactor.RedactAsync`), CLI (`PulsaMedia.Cli redact`).
- **Model**: an extending role. Without one the caller says where to redact, and the capability makes those regions
  unreadable and changes nothing else. Given a detector — a vision model behind `IChatClient` (`ChatRegionDetector`) or
  another detection model behind the port (`IRegionDetector`: image, width, height → regions with a label and a
  confidence) — it finds what to redact in an image (a face, a licence plate, a name on screen). Each found region is
  checked by the code, and a doubtful one is returned for review instead of hidden. Video detection is not built yet.

## Input

| Field | Rule |
|---|---|
| input | an existing image or video. Files ending in `.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp` or `.gif` are read as images; anything else is read as video |
| output | a new path; **must not exist** |
| regions | one or more `x, y, width, height`, in pixels of the source's own resolution; may be empty when a detector is given |
| detection | optional: a detector and the confidence (0–1, default 0.5) a found region needs to be hidden. Images only |
| start · end | for a video only, in seconds. Either may be left out: no start means from the beginning, no end means to the end. Both are ignored for an image |

On the CLI a region is written `--region x,y,width,height`, as four whole numbers, and several may follow one
`--region`. Anything else is refused before any file is touched. `--detect <what to hide> --openai-key <key>` (with
`--openai-model`, `--openai-endpoint`, `--min-confidence`) asks a vision model.

## Result

`{ success, outputPath?, error?, detected[], review[], notes[] }`

- `detected`: regions the detector found that were hidden, after clipping (`{ region, label, confidence }`).
- `review`: regions it found below the confidence bar — not hidden, for a person to decide on.
- `notes`: found regions the code refused, with the reason.

## Invariants

- Inside the region the pixels are replaced by coarse blocks. For a video this applies only within the time range.
- Outside the region the picture is kept. For a lossless image, every pixel outside the region is byte-for-byte the same
  as in the input (tested).
- The input is never modified, and an existing output is never replaced.
- A detected region is never trusted as given: it is clipped to the picture, and one with less than 2 pixels inside on
  either side is refused. Only a region at or above the confidence bar is hidden; a box without a confidence counts as 0.

## Failures

Nothing is thrown. The result carries `success: false` and an `error` sentence when:

- the input is missing;
- the output already exists;
- there is nothing to redact (no regions and no detector), or the detector found nothing confident enough;
- a detector is given for a video;
- ffmpeg fails, for example on an input it cannot decode.

Cancellation also ends in `success: false`, with the reason in `error`. A failed or cancelled run does not promise to
remove a partly written output.

**CLI**: on success the exit code is 0 and the output is `Wrote <output>`. Otherwise the exit code is 1 and the reason
goes to standard error; this includes a missing or malformed `--region`.
