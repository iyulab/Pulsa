# Transcription — contract

Turns recorded speech into a transcript with speakers and times. Recognition is the model's work; Pulsa owns what
happens around it — preparing the audio, cutting it into speech segments, putting speakers on the segments, refining
the words — and guarantees the result's timing and structure whichever models fill the stages. Status: **draft** —
nothing is implemented yet, and this contract may change before the first release.

- **Module**: the facade (options, stage composition, invariant checks) is in `Pulsa`, which brings no dependency
  beyond the models the caller supplies. Every stage is a port, so `Pulsa` references no stage module: audio
  preparation is implemented in `PulsaMedia`, which brings ffmpeg, and a stage that needs another dependency gets its
  own module. Turning an option on means taking on that stage's module.
- **Ways in**: library (`Transcriber.TranscribeAsync`), CLI (`PulsaMedia.Cli transcribe`). The CLI is a host: it chooses
  the recognition model from its own flags.
- **Model**: required — recognition is a speech-to-text model the caller supplies (`ISpeechToTextClient` in .NET).
  Extending: a voice-activity model gives tighter segments than the built-in silence detection, a diarization model
  puts speakers on the segments, and a chat model refines the words ([transcript refinement](transcript-refine.md)).
  The capability never creates a client or holds a credential.

## Stages

Each stage is a port: the caller hands in an implementation, or the stage does not run.

| Stage | Without it | Port (in .NET) | Module |
|---|---|---|---|
| prepare — decode, mono, resample, loudness, optional noise reduction | the audio must already be 16 kHz mono PCM WAV | audio preparer: any audio → 16 kHz mono PCM, with the map back to the input's timeline | `PulsaMedia` (ffmpeg) implements it |
| segment — find where speech is | silence detection on the prepared audio | voice-activity detector: audio → speech spans | `Pulsa`; a model adapter in its own module |
| recognize — speech to text, per segment | required | `ISpeechToTextClient` | caller's model |
| diarize — who spoke when | no speakers | diarizer: audio → speaker turns `{ start, end, speaker }` | caller's model, in its own module |
| refine — fix misheard words | not run | `IChatClient` | `Pulsa` (transcript refinement) |

## Input

| Field | Rule |
|---|---|
| audio | a file path or a stream in any format ffmpeg reads; at least one audio channel |
| language | optional language hint passed to recognition (BCP 47); none lets the model detect it |
| prepare | given a preparer: `noiseReduction` (off by default), loudness normalization (on by default) |
| segment | maximum segment length in seconds (default 30); minimum silence that splits two segments (default 0.5) |
| diarize | given a diarizer: optional expected number of speakers, or a minimum and maximum |
| refine | given a chat client: a glossary and the refinement gate, as in transcript refinement |

## Result

`{ cues[], speakers[], stages[], rejected[], warnings[] }`

- A cue is `{ start, end, text, speaker? }`, times in seconds on the original audio's timeline. WebVTT out.
- `speakers` lists the labels used (`S1`, `S2`, …) in order of first appearance.
- `stages` says what ran and with which model (`{ stage, model? }`, the model id as the port reports it).
- `rejected` is refinement's refused corrections, for a person to decide on.
- `warnings` names what was skipped and why (a segment the recognizer returned nothing for, a speaker turn that
  fell outside every segment).

## Invariants

- **Times are on the original audio's timeline.** Preparation never shifts time: segments found on the prepared audio
  are mapped back to the input, and every cue lies within the input's duration.
- **Cues are in time order and do not overlap.** A cue's start is before its end.
- **Timing is as fine as the segments.** The common speech-to-text port returns text for the audio it was given, not
  word times, so a cue is one segment. A recognizer that reports finer times inside a segment may split it; it never
  moves the segment's bounds.
- **Only recognition puts words in the transcript.** Diarization adds speakers and nothing else. Refinement can only
  change words under its gate (cue count and times never change).
- **A speaker is one label for the whole file.** Speakers are assigned over the whole audio, not per segment, so the
  same voice keeps its label. A cue takes the speaker whose turns cover most of it.
- **A stage that is not asked for does not run, and its module is not needed.**

## Failures

Checked before any audio is processed, and refused with an exception:

- an option that needs a stage nobody supplied (a speaker count without a diarizer, a glossary without a chat client);
- an audio file that does not exist, or that the preparer cannot read as audio — without a preparer, audio that is
  not 16 kHz mono PCM WAV.

During the run, errors from a port are passed through and end the whole call: no partial transcript is returned.
Cancellation also ends the call. A segment the recognizer returns no text for is dropped and named in `warnings`.

## Quality

The capability is measured, not only tested. Each change to a stage is measured again on the same recordings:

- word error rate (character error rate for languages written without spaces between words, such as Korean);
- diarization error rate, when speakers are on;
- segment boundary offset against reference times.

The evaluation recordings and their reference transcripts are kept with the tests. Which recordings, and under what
terms, is decided before the first implementation.

## Open questions

- The shape of the voice-activity and diarizer ports, and whether a model supply layer already offers them.
- A recognizer that also returns speakers: whether it fills the diarize stage itself, or diarization stays a separate
  call so speaker labels hold across segments.
- Whether recognition is called once per segment or once per file with segments mapped afterwards, when the
  recognizer returns its own times.
