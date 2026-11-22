# Caption drafting — contract

Drafts one short on-screen caption per scene from a few lines that describe what a video is for. Status: pre-1.0.

- **Module**: `Pulsa`, which brings no dependency beyond the chat model the caller supplies.
- **Ways in**: library (`CaptionDrafter.DraftAsync`), CLI (`PulsaMedia.Cli draft-captions`). The CLI is a host, so it
  picks a concrete chat client itself.
- **Model**: the caller's (`IChatClient` in .NET). The capability never creates a client, reads configuration or holds
  a credential.

## Input

`{ imagePaths (at least 1), introText }`

The number of images sets how many captions come back. The images themselves are not sent to the model.

## Result

A list with exactly one caption per image, in scene order.

## Invariants

- The count is guaranteed by the code, not requested from the model. List markers and blank lines are stripped and extra
  lines are dropped. A reply with too few lines is a failure.

## Failures

An empty image list is refused with an exception that says so, and so is a reply with fewer captions than images. Errors
from the chat client are passed through.
