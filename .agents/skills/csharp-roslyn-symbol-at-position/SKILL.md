---
name: csharp-roslyn-symbol-at-position
description: Use csharp_roslyn symbol_at_position to resolve the exact bound symbol at a C# file coordinate when names or syntax are ambiguous.
---

# Roslyn symbol at position

Turn a source coordinate into exact bound, declared, enclosing, and candidate symbols.

## Workflow

1. Provide an absolute C# `documentPath` in the loaded workspace and one-based `line` and `column`.
2. Call the `csharp_roslyn` MCP tool `symbol_at_position`.
3. Keep `includeCandidates: true` when code is incomplete or overload resolution failed; normally cap `maxCandidates` at 20.
4. Distinguish the bound symbol from a declared symbol, enclosing symbol, and recovery candidates.
5. Use the returned stable ID with `symbol_info`, `invocation_binding`, `find_references`, or refactoring previews.

## Common scenarios

- Identify what a local identifier or member access means.
- Disambiguate overloads or extension methods.
- Resolve a symbol in incomplete code.
- Navigate from an editor coordinate to semantic analysis.

Line and column are one-based and must refer to the current file snapshot. Refresh coordinates after edits.

