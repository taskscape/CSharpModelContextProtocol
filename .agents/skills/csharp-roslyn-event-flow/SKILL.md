---
name: csharp-roslyn-event-flow
description: Use csharp_roslyn event_flow to resolve event subscribe, unsubscribe, raise, and handler sites before changing event contracts or lifetimes.
---

# Roslyn event flow

Trace compiler-bound event operations and resolve handlers where possible.

## Workflow

1. Resolve the event to a stable event documentation ID.
2. Call the `csharp_roslyn` MCP tool `event_flow`.
3. Filter `actions` to `subscribe`, `unsubscribe`, `raise`, or `reference` when the question is narrow; keep `maxResults` at 50 or less.
4. Report the action, event identity, resolved method-group or lambda handler, project, and exact location.
5. For lifecycle or memory-leak questions, pair subscriptions with unsubscriptions and inspect the owning object lifetime.

## Common scenarios

- Find all event handlers before renaming an event.
- Diagnose missing unsubscription.
- Locate event raise sites.
- Review event-driven execution paths.

Roslyn can classify source operations but cannot prove runtime subscription order, object lifetime, weak-event behavior, or reflection-based wiring.

