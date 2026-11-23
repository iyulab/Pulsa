# Redaction — contract

Pixelates rectangular regions of an image, or of a video over a time range, and writes the result to a new file. Given
a vision or detection model, it finds the regions to hide in an image, or in the frames it looks at of a video.
Status: pre-1.0 — names and shapes may change in a minor version.

- **Module**: `PulsaMedia`, which brings ffmpeg. The caller names its folder, or it is found on `PATH`.
- **Ways in**: library (`MediaRedactor.RedactAsync`), CLI (`PulsaMedia.Cli redact`).
- **Model**: an extending role. Without one the caller says where to redact, and the capability makes those regions
  unreadable and changes nothing else. Given a detector — a vision model behind `IChatClient` (`ChatRegionDetector`) or
  another detection model behind the port (`IRegionDetector`: image, width, height → regions with a label and a
  confidence) — it finds what to redact in an image (a face, a licence plate, a name on screen). Each found region is
  checked by the code, and a doubtful one is returned for review instead of hidden. In a video the detector
  looks either at one frame (the caller picks the time) and what it finds is hidden over the whole time range as fixed
  rectangles, or at a frame every so many seconds: a region found in a frame is hidden until the next frame, over the area
  it and its match in the next frame (same label, overlapping) cover together, so a thing that moved a little in between
  stays covered. What is not in the next frame is hidden only until that frame.
- **Hosts**: runs in a trimmed or native-AOT application. The picture size is read from ffprobe's JSON without
  reflection, and the package sets `IsAotCompatible`, which keeps reflection-based code of its own out. (Checked by
  running a trimmed, self-contained publish; FFMpegCore's own probe call fails there, which is why it is not used.)

## Input

| Field | Rule |
|---|---|
| input | an existing image or video. Files ending in `.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp` or `.gif` are read as images; anything else is read as video |
| output | a new path; **must not exist** |
| regions | one or more `x, y, width, height`, in pixels of the source's own resolution; may be empty when a detector is given |
| detection | optional: a detector, the confidence (0–1, default 0.5) a found region needs to be hidden, and for a video either the time in seconds of the one frame the detector looks at (default: the start of the time range, else the first frame) or an interval in seconds between frames (not both) with the most frames it may be asked about (default 60) |
| start · end | for a video only, in seconds. Either may be left out: no start means from the beginning, no end means to the end. Both are ignored for an image |

On the CLI a region is written `--region x,y,width,height`, as four whole numbers, and several may follow one
`--region`. Anything else is refused before any file is touched. `--detect <what to hide> --openai-key <key>` (with
`--openai-model`, `--openai-endpoint`, `--min-confidence`, and for a video `--frame-at <seconds>` or `--sample-every <seconds>` with `--max-samples <n>`) asks a vision
model;
the key may be left out when `--openai-endpoint` names a server that takes none.

## Result

`{ success, outputPath?, error?, detected[], review[], notes[] }`

- `detected`: regions the detector found that were hidden, after clipping (`{ region, label, confidence, at? }`). `at` is
  the time in seconds of the frame it was found in, given when frames were sampled.
- `review`: regions it found below the confidence bar — not hidden, for a person to decide on (with `at` the same way).
- `notes`: found regions the code refused, with the reason (and the time of the frame when frames were sampled).

## Invariants

- Inside the region the pixels are replaced by coarse blocks. For a video this applies only within the time range, and
  for a sampled video only within the stretch the region was held for.
- Outside the region the picture is kept. For a lossless image, every pixel outside the region is byte-for-byte the same
  as in the input (tested).
- The input is never modified, and an existing output is never replaced.
- A detected region is never trusted as given: it is clipped to the picture, and one with less than 2 pixels inside on
  either side is refused. Only a region at or above the confidence bar is hidden; a box without a confidence counts as 0.

## Open questions

- Between two sampled frames nothing is known: a thing that enters and leaves within one interval is missed, and one that
  moves further than the union of its two positions covers (a long way, or a curved path) leaves part of itself
  uncovered. A shorter interval narrows both, at one detector call per frame — hence the frame limit. Following a
  thing between frames (motion estimation) rather than assuming it stays inside the union is not built.
- A video that is rotated by metadata: the frame is taken after the rotation is applied, and so are the rectangles, but
  this is checked with an unrotated video only.

## Failures

Nothing is thrown. The result carries `success: false` and an `error` sentence when:

- the input is missing;
- the output already exists;
- the detector would be asked about more frames than the limit allows (checked before it is asked about any), or the
  interval is not positive, or both a single frame and an interval are given;
- there is nothing to redact (no regions and no detector), or the detector found nothing confident enough;
- a video has no picture at the frame time asked for (after its end);
- the frame time is negative;
- ffmpeg fails, for example on an input it cannot decode.

Cancellation also ends in `success: false`, with the reason in `error`. A failed or cancelled run does not promise to
remove a partly written output.

**CLI**: on success the exit code is 0 and the output is `Wrote <output>`. Otherwise the exit code is 1 and the reason
goes to standard error; this includes a missing or malformed `--region`.
