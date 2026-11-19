# Channel connector — contract

A channel connector lets an agent hold a one-to-one conversation with a person through an external messaging service. It does
no modelling: the processing is the service-specific connection, signature checking, payload normalisation and delivery
confirmation, behind one contract that every service shares. Status: design, pre-1.0 — names and shapes may change.

## What it is, and is not

- **Is**: a process (MCP server, or the same functions as a CLI) that holds the connection to a messaging service and offers
  an agent two tools and a stream of events. One provider package per service (the service's own client library is a
  dependency only that package takes on).
- **Is not**: the mapping from a person to a conversation, any notice shown before a person turns a channel on, or what an
  approval means. Those belong to the host that uses the connector. A conversation and a sender are opaque strings to the
  connector.
- **First cut**: one-to-one conversations. Group rooms (mentions, threads) are a separate design; the contract does not forbid
  them.

## Credentials

The host gives the connector its credentials (a bot token and the like) through the process's own configuration — environment
variables or a configuration file the host names. A credential never appears in a tool argument, an event, a log line or an
error message. Providers must redact it from anything they write.

## Tools

| Tool | Input | Result |
|---|---|---|
| `channel_send` | `conversation` (opaque string), `text` (UTF-8, at most the provider's limit), optional `link` (an absolute URL appended as a link, not a body) | `{ delivered: bool, messageId?: string, error?: Error }` |
| `channel_status` | none | `{ provider: string, state: "connecting" \| "connected" \| "reconnecting" \| "stopped", since: ISO-8601 time, detail?: string }` |
| `channel_poll` | `after` (the `seq` last seen, default 0), `epoch` (the epoch of the read that gave `after`, when the caller has one), `timeoutMs` (0–30000, default 0), `max` (1–100, default 20) | `{ epoch, reset, gap, events: Event[] }` — the events with `seq` greater than `after`, waiting up to the timeout for the first one |

`channel_poll` exists because the Model Context Protocol has no standard server-to-client event for tools; a client that can
subscribe to notifications may also be offered the same events as notifications, but the poll tool is the baseline every client
can use.

## Events

Every event has `type`, `at` (ISO-8601), and an ordering `seq` that increases by one per event of a running connector.

- `message_received` — `conversation`, `sender` (opaque label), `text`, `messageId` (the service's id), `receivedAt`.
- `connection_changed` — `state` (as in `channel_status`), `detail?`.

A read does not consume: the connector keeps the newest 1000 events, and a consumer that asks again with the last `seq` it saw
gets what came after, so a lost reply loses nothing **while the same connector keeps running**. Beyond that the contract says so
instead of failing silently:

- `epoch` names the running connector. After a restart `seq` starts again from 1 and the `epoch` differs; a caller that hands its
  old `epoch` back (or a cursor ahead of anything this run has produced) gets `reset: true` and the read starts from the beginning.
- `gap: true` means events between the cursor and the first one returned were dropped because more than 1000 arrived unread.

Delivery toward the consumer is at least once, and a consumer de-duplicates by `messageId`. A provider whose service confirms a
message when it is read (Telegram does) holds that confirmation back until the consumer's cursor has passed the event: the
cursor a consumer hands to `channel_poll` is what tells the provider the message was taken over. A message the connector holds
but the consumer has not yet fetched is therefore delivered again after a restart — at the cost of the service repeating
unconfirmed updates, which the provider reads and drops without publishing, polling about once a second while any are
unconfirmed. A consumer that stops polling stops confirming, and the backlog stays on the service's side.

## Errors

`{ code, message, retryable }`. `code` is one of a closed set so a caller can branch without reading text:

- `invalid_request` — the tool input is wrong (empty text, text longer than the limit, malformed link). Not retryable.
- `unknown_conversation` — the service does not know that conversation (or the bot may not write there). Not retryable.
- `not_connected` — the connector has no live connection to the service. Retryable.
- `rate_limited` — the service asked the connector to slow down. Retryable; `message` may say for how long.
- `service_error` — the service refused or failed for another reason. `retryable` is the provider's judgement.

`message` is for a person to read, in the connector's language, and holds no credential and no message text.

## Providers

A provider implements the contract and nothing else is public about it. It declares its text limit and what it needs in
configuration. The contract package carries a fake provider that holds conversations in memory, so a consumer and the
connector's own tests run without an account or a network.

## Packaging

| Project | Role |
|---|---|
| `PulsaChannel.SDK` | the contract, the fake provider, shared helpers (event queue, redaction) |
| `PulsaChannel.Telegram` | the Telegram provider (Bot API over HTTPS, no client library) |
| `PulsaChannel.Cli` | one executable: `serve` (the MCP server over stdin/stdout, or over HTTP with `--http <url>`), `send`, `status`, `listen` |

`status` of a real service asks whether the credential works without reading any message (a provider can offer this probe); only the
fake provider is started for a status. Standard input is read as UTF-8 whatever the console's code page is.

The provider is chosen by `PULSA_CHANNEL_PROVIDER` (`fake` or `telegram`) or `--provider`; credentials are read from the
environment only (`PULSA_CHANNEL_TELEGRAM_TOKEN`; `PULSA_CHANNEL_TELEGRAM_API` points at another Bot API root). `send` does not
start the provider's connection for a real service: reading messages is what confirms them to the service, so a send that read
would take messages nobody has seen. Exit codes: 0 done, 1 not delivered or not connected, 2 wrong usage.

`serve --http <url>` is the Streamable HTTP form (`POST <url>/mcp`, one JSON-RPC message in, the JSON reply out, 202 for a
notification) that a host registers as a connector by address. It listens only on the address given. A page in a browser must not
be able to drive it, so a request that carries an `Origin` header (403) or is not `application/json` (415) is refused, as is any
method but POST (405) and any other path (404); with `PULSA_CHANNEL_MCP_KEY` set, a request without that bearer key is refused
(401) — the key is read from the environment like every credential.

The MCP server implements only what a tools-only server needs (`initialize`, `ping`, `tools/list`, `tools/call`) over
newline-delimited JSON-RPC, so it adds no dependency. A send the contract refuses is a tool result with `isError`, not a protocol
error; arguments that do not fit the schema are a JSON-RPC `-32602` error.

A new package starts at version `0.0.1` and is not listed in the publishing workflow until its maintainers add it.
