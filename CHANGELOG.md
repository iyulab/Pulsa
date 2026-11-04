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
