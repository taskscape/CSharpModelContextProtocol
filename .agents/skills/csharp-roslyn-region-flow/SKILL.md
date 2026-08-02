---
name: csharp-roslyn-region-flow
description: Use csharp_roslyn region_flow for authoritative Roslyn data-flow and control-flow analysis across a contiguous statement range.
---

# Roslyn region flow

Analyze variables and reachability within one valid contiguous statement region.

## Workflow

1. Provide an absolute C# `documentPath` and one-based start and end line/column coordinates.
2. Call the `csharp_roslyn` MCP tool `region_flow` with `kind: data`, `control`, or `both`.
3. For data flow, report declared, read, written, always-assigned, captured, flows-in, and flows-out symbols.
4. For control flow, report entry/exit reachability, returns, branches, and exit points.
5. If Roslyn rejects the region, adjust it to whole contiguous statements rather than approximating from syntax text.

## Common scenarios

- Safely extract a method.
- Understand captured locals in a lambda.
- Diagnose definite-assignment or reachability issues.
- Review mutation across a code block.

The analysis is scoped to the selected region and compile-time control flow. It is not runtime path coverage or exception-flow analysis.

