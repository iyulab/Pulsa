# Grounded extraction — contract

Pulls the fields a schema names out of a text, and proves each value against the text: every value it returns comes
with the place in the text it was read from, and a value the text does not support is refused with the reason instead
of returned. The model reads the text; Pulsa owns the proof. Status: **draft** — verification, extraction with a model and
the command line are implemented. This contract may change before the first release.

- **Module**: `Pulsa`, which brings no dependency beyond the model the caller supplies.
- **Ways in**: library (`Extractor.ExtractAsync` · `Extractor.Verify`; `ExtractJson` reads a schema or candidates from
  JSON and writes a result as JSON), CLI (`Pulsa.Cli extract` · `Pulsa.Cli verify`).
- **Model**: required for extraction — a chat model the caller supplies (`IChatClient` in .NET) reads the text and
  answers with values and the words it read each one from. Verification alone takes no model: given a text, a schema
  and any structured output already made (by a chat model, a structured-output call of an agent framework, or a
  person), it checks that output the same way. The capability never creates a client or holds a credential, and does
  not replace a structured-output call: it is the step after one.

## CLI

`Pulsa.Cli extract --text <file> --schema <schema.json> --openai-key <key>` asks a chat model (`--openai-model`, default
`gpt-4o-mini`; `--openai-endpoint` for any OpenAI-compatible server, which then needs no key) and verifies its answer.
`Pulsa.Cli verify --text <file> --schema <schema.json> --candidates <candidates.json>` takes no model and checks values
someone already proposed. Both take `--fuzzy` and `--threshold <0-1>`, and write the result as JSON to standard output or
to `--output <file>` (which must not exist). The schema file is `{"fields": [{ "name", "type", … }]}` as in Input; the
candidates file is `{"fields": {"<name>": [{"value": …, "quote": …}]}}`, the shape the model is asked to answer in. Refused
values are listed on standard error as `refused: <name> (<reason>): <detail>`. The exit code is 0 when the run
completed — refused values are a result, not a failure — and 1 with the reason on standard error otherwise (unreadable
schema or candidates, an unusable schema, a missing or malformed option, an error from the model).

Input documents of any kind — a meeting transcript, a medical note, an invoice, a contract — are text by the time they
reach this capability. Turning a file into text is a document parser's work and stays outside.

## Input

| Field | Rule |
|---|---|
| text | the text to read, as one string |
| schema | a list of fields, each `{ name, type, required?, many?, values?, pattern?, description? }` |
| output | for verification: a candidate value per field, each `{ value, quote }` (a list of them when `many`) |
| options | `fuzzy` — accept a quote that is close to the text but not exact (off by default); `threshold` (0–1) for it |

A field's `type` is one of `string`, `number`, `date` (ISO 8601 calendar date), `boolean` or `enum` (then `values`
lists the allowed values). `pattern` is a regular expression a `string` value must match in full. `description` is
passed to the model and nowhere else. A pattern that cannot decide within one second does not accept the value.

## Result

`{ fields[], rejected[] }`

- A field is `{ name, value, quote, span: { start, end }, match }`. `span` is where the quote is in `text`, as offsets
  that count Unicode scalar values from the start, end exclusive. `match` says how the quote was found: `exact`,
  `normalized` (equal once both are normalized — Unicode NFC, case, whitespace), or `fuzzy` (only when asked for).
- A rejected entry is `{ name, value?, quote?, reason }`, with `reason` one of `missing` (a required field has no
  value), `no-quote` (a value without the words it was read from), `not-in-text` (the quote is not in the text),
  `not-supported` (the quote is in the text but does not carry the value), `format` (the value is not of the field's
  type, does not match its pattern, or is not one of its values) and `not-in-schema` (a field the schema does not name).

## Invariants

- **Every returned value is grounded.** Its quote is found in the text at `span`, and the value is carried by the
  quote: a `string` value appears in its quote (once both are normalized); a `number` or `date` value is the one the
  quote writes, read by the code, not by the model; a `boolean` or `enum` value needs a quote but is not read from it
  (the quote is the evidence a person checks).
- **Nothing is returned that the schema does not name, and nothing a required field lacks is silently dropped** — it
  is in `rejected` as `missing`.
- **The text is never changed**, and the result never holds a value that is not in a field or in `rejected`.
- **Verification is deterministic.** The same text, schema and output always give the same result; the model only
  matters to extraction.
- **Several values of one field may share a quote, and spans may overlap** — two fields can rest on the same words.
  When a quote occurs more than once in the text, the span is its first occurrence after the previous field's span,
  else its first occurrence.

## Failures

Refused with an exception before the model is called: an empty schema, two fields with one name, an `enum` without
values, a `pattern` that is not a valid regular expression, a fuzzy threshold outside 0–1.

During extraction, an error from the model is passed through and ends the call. A model answer that cannot be read as
values is not an error: every field it fails to give is `missing` (when required) and the reason says the answer could
not be read.

## Quality

Measured on texts with reference values: per field, precision (returned values that equal the reference) and recall
(reference values returned), and the share of wrong values that were caught in `rejected` rather than returned. The
evaluation texts are kept with the tests; which texts, under what terms, is decided before the first release.

## Open questions

- Whether fuzzy matching is worth having at all (it ignores punctuation at the edges of words and ends its span at the
  first and last word it shares with the quote), or whether `normalized` is the only lenient match (a fuzzy quote is
  the case a person most needs to check).
- Whether a field may be a nested object or a list of objects (a line item with its own fields), or the schema stays
  flat with `many` fields for a first version.
- Numbers written in words or with local grouping ("1.234,5", "삼만 원"): which forms the code reads.
