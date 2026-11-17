# Contributing to Pulsa

## Whether a capability belongs here

No single maintainer decides. At least one editing project other than the proposer checks the proposal against the
charter in the [README](README.md#what-belongs-in-pulsa) and the three conditions below; with no objection, it is admitted.
The proposer does not wait for that: the capability is built on a topic branch (see below), and the check is one item of
the gate that merges it.

1. **A reusable unit.** Name a second product that could use the same processing, or say why none could.
2. **A dependency boundary or its own process.** What would a host not want to take on unless it used the capability
   (a native tool, a model format, a heavy library, a licence, a native runtime)?
3. **Reaches an agent as tools or events.** Which tools or events, with their inputs and outputs.

Not admitted: a single prompt or a single call; a capability whose value comes from one application's domain (that
stays in the application's own plugin).

A proposal is a description of the capability and of what it needs, never of the application that asked for it.

## Where work happens

- Work in progress lives on a topic branch named `<kind>/<slug>` (for example `feature/messaging`); `main` receives only
  finished units. Every branch builds on push.
- A branch is merged (`--no-ff`) when `main` is contained in it, it changes no package version line, the change is in the
  changelog, a consumer other than its author has recorded a verification, and its build is green.
- Each package keeps its own version line and its own release manager — the editing project that built it.

## Writing for this repository

- Describe the situation, not the product: «an app whose backend outlives its window», not the app's name.
- The text here is public. No internal paths, machine names, ticket ids, or «found while doing X».
- A capability's surface is a process boundary (CLI, MCP server) and, optionally, a language binding. The language of
  an implementation is not part of the contract; builds, versions and publishing follow the language's own
  convention.
