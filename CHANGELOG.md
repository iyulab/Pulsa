# Changelog

Entries for changes that are on `main` and not yet in a published version. A package's version line is changed only by the
maintainer who publishes it.

## Unreleased

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
