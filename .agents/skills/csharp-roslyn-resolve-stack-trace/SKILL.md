---
name: csharp-roslyn-resolve-stack-trace
description: Use csharp_roslyn resolve_stack_trace to map .NET stack frames, including async and compiler-generated names, to source symbols.
---

# Roslyn stack trace resolution

Turn a pasted .NET stack trace into loaded solution symbols and source locations.

## Workflow

1. Preserve the raw stack trace, including inner exceptions and log prefixes.
2. Call the `csharp_roslyn` MCP tool `resolve_stack_trace` with the trusted workspace and normally at most 50 frames.
3. Report each parsed frame, normalized type/member name, resolved symbol ID, project, source location, and unresolved reason.
4. Start diagnosis at the highest relevant application frame, then use `symbol_source`, `invocation_binding`, `call_hierarchy`, or the `debug-flow` `context_bundle`.
5. Keep external or metadata-only frames visible rather than pretending they resolved to source.

## Common scenarios

- Resolve async state-machine `MoveNext` frames.
- Map lambdas and local functions.
- Navigate generic or nested-type frames.
- Triage an inner-exception chain.

Resolution is against the loaded source snapshot. PDB line mappings, deployed binary drift, inlining, and optimized runtime behavior can make a production trace differ.

