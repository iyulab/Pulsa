# Transcript refinement — contract

Corrects the speech-recognition mistakes in a transcript with a chat model. The model can only fix what was misheard: it
cannot add, drop, rephrase or move anything. Status: pre-1.0.

- **Module**: `Pulsa`, which brings no dependency beyond the chat model the caller supplies.
- **Ways in**: library only — `TranscriptRefiner.RefineAsync`, with `WebVtt.Parse`, `WebVtt.Write` and
  `Glossary.FromMarkdown`. There is no CLI yet.
- **Model**: the caller's (`IChatClient` in .NET). The capability never creates a client or holds a credential.

## Input

| Field | Rule |
|---|---|
| cues | the transcript as timed cues (start, end, text, optional speaker); WebVTT in and out |
| glossary | names and terms spelled the way they should be. It is a spelling list, never content |
| batch size | how many cues go into one model call; at least 1 (default 60) |
| gate | the rule every correction must pass (default similarity threshold 0.6) |

## Result

`{ cues, applied[], rejected[] }`

Each change is `{ index, original, refined, kind }`, where `kind` is `corrected` or `unclear`.

## Invariants

- **The number of cues and their timings never change.** The model receives only the numbered cue texts and the
  glossary, and answers with the cues it would change. The code reassembles the transcript.
- **Every correction must sound like what it replaces.** The cue is compared word by word. Each changed stretch must be
  close, letter by letter, to the stretch it replaces; Hangul is compared jamo by jamo. A change of spacing alone always
  passes. Insertions, deletions, and replacements that do not resemble the original are refused.
- Refused corrections are not lost. They are returned in `rejected`, for a person to decide on.
- A cue the model marks as unintelligible becomes `[unclear]` and is reported as an `unclear` change.

## Failures

A batch size below 1 is refused with an exception. Errors from the chat client are passed through and end the whole
call: no partial result is returned.
