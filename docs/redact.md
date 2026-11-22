# Redaction — contract

Pixelates one rectangular region of an image, or of a video over a time range, and writes the result to a new file.
Status: pre-1.0 — names and shapes may change in a minor version.

- **Module**: `PulsaMedia`, which brings ffmpeg. The caller names its folder, or it is found on `PATH`.
- **Ways in**: library (`MediaRedactor.RedactAsync`), CLI (`PulsaMedia.Cli redact`).
- **Model**: an extending role, not built yet. Today the caller says where to redact, and the capability makes that
  region unreadable and changes nothing else. Given a vision or detection model, it is to find what to redact (a face, a
  licence plate, a name on screen); a detected region will be checked by the code, and a doubtful one returned for
  review instead of hidden.

## Input

| Field | Rule |
|---|---|
| input | an existing image or video. Files ending in `.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp` or `.gif` are read as images; anything else is read as video |
| output | a new path; **must not exist** |
| region | `x, y, width, height`, in pixels of the source's own resolution |
| start · end | for a video only, in seconds. Either may be left out: no start means from the beginning, no end means to the end. Both are ignored for an image |

On the CLI the region is written `--region x,y,width,height`, as four whole numbers. Anything else is refused before any
file is touched.

## Result

`{ success, outputPath?, error? }`

## Invariants

- Inside the region the pixels are replaced by coarse blocks. For a video this applies only within the time range.
- Outside the region the picture is kept. For a lossless image, every pixel outside the region is byte-for-byte the same
  as in the input (tested).
- The input is never modified, and an existing output is never replaced.

## Failures

Nothing is thrown. The result carries `success: false` and an `error` sentence when:

- the input is missing;
- the output already exists;
- ffmpeg fails, for example on an input it cannot decode.

Cancellation also ends in `success: false`, with the reason in `error`. A failed or cancelled run does not promise to
remove a partly written output.

**CLI**: on success the exit code is 0 and the output is `Wrote <output>`. Otherwise the exit code is 1 and the reason
goes to standard error; this includes a missing or malformed `--region`.
