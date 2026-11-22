# Changelog

Entries for changes that are on `main` and not yet in a published version. A package's version line is changed only by the
maintainer who publishes it.

## Unreleased

- **Breaking — redaction takes regions, and can find them.** `RedactRequest` takes `Regions` (a list of
  `RedactRegion(x, y, width, height)`) in place of `X`, `Y`, `Width` and `Height`: write
  `Regions: [new RedactRegion(x, y, width, height)]`. Several regions are hidden in one pass. Given an `IRegionDetector`
  (`RedactDetection`) — `ChatRegionDetector` asks any vision `IChatClient` — redaction finds what to hide in an image;
  every found region is clipped to the picture, hidden only above the confidence bar, and returned in `Detected`,
  `Review` (found, not hidden) or `Notes` (refused). Detection on video is not supported yet. `PulsaMedia.Cli redact`
  takes several `--region` values and `--detect <what> --openai-key <key>`.
- **Capability contracts.** `docs/` now holds a language-neutral contract for video composition, redaction, caption
  drafting and transcript refinement, next to the channel connector: what each takes and returns, the invariants it
  keeps, how it fails, its module and the ways to reach it. The README's design principles link each one. No package changes.
- **Breaking — packages are cut by the dependency they bring.** A consumer decides whether to take on a dependency
  (ffmpeg, a service, a library), so capabilities that share one now share a package. Namespaces and types are unchanged;
  only package references move:

  | Before | After |
  |---|---|
  | `PulsaVideoCompose.SDK` | `PulsaMedia` (ffmpeg) — caption drafting moved to `Pulsa` |
  | `PulsaRedact.SDK` | `PulsaMedia` (ffmpeg) |
  | `PulsaTranscript.SDK` | `Pulsa` (no dependency beyond the chat model you supply) |
  | `PulsaVideoCompose.Cli` | `PulsaMedia.Cli`, which also gains `redact` |

  Replace all of the retired packages in the same step. A retired package kept beside `Pulsa` or `PulsaMedia` defines the
  same types a second time, and using one of them fails to compile (CS0433). `PulsaMedia` no longer depends on `Pulsa`.
  The README's package table, installation, usage headings and repository layout follow the new packages, and its
  design principle reads "packages follow dependencies; capabilities stay separate".
- **README: what Pulsa is, stated without tying it to .NET.** Pulsa is described as parts for building AI agents and AI
  services: self-contained capabilities behind a contract of tools and events, with the model, configuration,
  credentials and host policies left to the product that uses them. A capability reaches its consumer as a library, a
  CLI, asynchronously through the file system, or as an MCP server — whichever its consumers need — and reliability is
  stated as what it must deliver. The design principles no longer assume a .NET host: a capability's language-neutral
  contract sets its shape, and each implementation is written in the language that fits the problem.
  The third admission condition now reads "it can be handed over as a contract" instead of "it reaches an agent as tools
  or events". CONTRIBUTING describes how a capability built before it has a consumer is verified (by another editing
  project, through its CLI) and published (as a prerelease until its first consumer).
  A fourth admission condition: every capability has a role for a model — required, or extending what it does when a
  model handler is given. The README no longer describes redaction as processing that takes no model. The package table and repository layout now list the channel connector packages (not yet
  published). No package changes.
- **Channel connector (design, new packages — not yet published).** A one-to-one messaging channel behind one contract:
  `PulsaChannel.SDK` (contract, in-memory fake provider, ordered event queue with a read cursor, credential redaction) and
  `PulsaChannel.Telegram` (Telegram Bot API over plain HTTPS, no client library). The contract is in
  [docs/channel-connector.md](docs/channel-connector.md). `PulsaChannel.Cli` is the executable over a provider: `serve` (a Model
  Context Protocol server with `channel_send`, `channel_status`, `channel_poll`, over stdin/stdout or Streamable HTTP with `--http`),
  `send`, `status`, `listen`.
  None of these packages is listed in the publishing workflow.
- **Channel connector: choices on a message and a `choice_selected` event.** `channel_send` takes an optional `choices` list
  (`{ id, label }`); the person's pick comes back through `channel_poll` as `choice_selected` (`messageId`, `choiceId`, `closed`),
  once per message — a second pick is told "already chosen" and is not an event. A provider declares what its service takes
  (`IChannelProvider.Choices`); a service that has none answers the new `not_supported` error and nothing is sent. The Telegram
  provider sends an inline keyboard, answers every press, and takes the keyboard off the message after the first pick. The fake
  provider has `Choose(...)` for tests. Still not published.
- **Channel connector: a file on a message.** `channel_send` takes an optional `file` — a path under the folder the host gave the
  connector (`PULSA_CHANNEL_FILE_ROOT`; `send --file` on the CLI) — and the text becomes its caption. Absolute paths, paths that climb
  out or go through a link, folders, missing and empty files are refused before anything is sent; a connector without a folder or a
  service without files answers `not_supported`. A provider declares its file and caption limits (`IChannelProvider.Files`). The
  Telegram provider uploads a document (`sendDocument`, multipart) with the caption, and with the choices' keyboard when there is one.
  Still not published.
