# Contributing to Pulsa

## Whether a capability belongs here

The version manager of this repository decides, against the charter in the [README](README.md#what-belongs-in-pulsa),
from what the proposal shows:

1. **A reusable unit.** Name a second product that could use the same processing, or say why none could.
2. **A dependency boundary or its own process.** What would a host not want to take on unless it used the capability
   (a native tool, a model format, a heavy library, a licence, a native runtime)?
3. **Reaches an agent as tools or events.** Which tools or events, with their inputs and outputs.

Not admitted: a single prompt or a single call; a capability whose value comes from one application's domain (that
stays in the application's own plugin).

A proposal is a description of the capability and of what it needs, never of the application that asked for it.

## Writing for this repository

- Describe the situation, not the product: «an app whose backend outlives its window», not the app's name.
- The text here is public. No internal paths, machine names, ticket ids, or «found while doing X».
- A capability's surface is a process boundary (CLI, MCP server) and, optionally, a language binding. The language of
  an implementation is not part of the contract; builds, versions and publishing follow the language's own
  convention.
