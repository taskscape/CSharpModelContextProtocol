---
name: csharp-roslyn-symbol-info
description: Use csharp_roslyn symbol_info before changing an unfamiliar C# type or member, resolving ambiguity, or reviewing a contract.
---

# Roslyn symbol information

Resolve an unfamiliar symbol to compiler facts before reasoning about or editing it.

## Workflow

1. Prefer a Roslyn documentation ID from an earlier result, such as `T:Namespace.Type` or `M:Namespace.Type.Method(System.String)`. Otherwise use a metadata or qualified name.
2. Call the `csharp_roslyn` MCP tool `symbol_info` with the absolute workspace path. Add the exact `projectName` when the query is ambiguous.
3. Keep `maxResults` small, normally 20. If multiple matches remain, do not guess; refine the identity or use `symbol_at_position`.
4. Use the returned signature, accessibility, modifiers, declaration locations, documentation, base type, and interfaces as the authoritative compile-time context.
5. Before changing a contract, follow with `find_references`, `implementation_map`, and `affected_symbols`.

## Common scenarios

- Distinguish same-named types in different namespaces.
- Confirm overload signatures and accessibility.
- Inspect inheritance and interface contracts.
- Obtain stable IDs for subsequent MCP calls.

Metadata-only results may not have source locations or bodies; use `symbol_source` only for source declarations.

