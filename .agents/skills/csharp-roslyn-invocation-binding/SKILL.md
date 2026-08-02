---
name: csharp-roslyn-invocation-binding
description: Use csharp_roslyn invocation_binding to explain selected overloads, receiver types, conversions, and argument mapping at a call site.
---

# Roslyn invocation binding

Explain exactly how the compiler bound one invocation or object creation.

## Workflow

1. Provide the absolute C# `documentPath` and one-based source `line` and `column` inside the call.
2. Call the `csharp_roslyn` MCP tool `invocation_binding`.
3. Report the selected target, receiver type, extension reduction, generic type arguments, argument-to-parameter map, conversions, candidate symbols, and failure reason when binding is incomplete.
4. If the position identifies the wrong node, use `symbol_at_position` to refine the coordinate.
5. Use the exact target ID for `symbol_info`, `call_hierarchy`, or `find_references`.

## Common scenarios

- Diagnose why an overload was selected.
- Understand extension-method binding.
- Review named, optional, `params`, or generic arguments.
- Explain a binding compiler error.

The coordinate must match the current source snapshot. Do not infer runtime virtual dispatch from compile-time invocation binding.

