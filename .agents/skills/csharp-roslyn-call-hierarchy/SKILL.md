---
name: csharp-roslyn-call-hierarchy
description: Use csharp_roslyn call_hierarchy to trace bounded callers and callees while debugging execution flow or planning method changes.
---

# Roslyn call hierarchy

Trace compiler-resolved caller and callee relationships without flooding context.

## Workflow

1. Identify a method, constructor, accessor, or local function by stable symbol ID.
2. Call the `csharp_roslyn` MCP tool `call_hierarchy` with `direction` set to `callers`, `callees`, or `both`.
3. Start at `maxDepth: 1` or `2` and `maxResults: 50` or less. Increase depth only when the first graph leaves a concrete question unanswered.
4. Explain graph edges, cycles, truncation, and the projects or source locations involved.
5. For a contract edit, combine callers with `implementation_map`, `affected_symbols`, and `test_impact`.

## Common scenarios

- Find entry paths into a failing method.
- Understand direct dependencies called by a service.
- Estimate the blast radius of a method change.
- Identify recursive or cyclic call paths.

Do not claim completeness for reflection, delegates that cannot be resolved, dynamic invocation, message routing, or framework callbacks.

