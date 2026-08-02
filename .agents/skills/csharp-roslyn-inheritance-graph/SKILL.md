---
name: csharp-roslyn-inheritance-graph
description: Use csharp_roslyn inheritance_graph to inspect bounded base, derived, interface, and implementation relationships for a C# type.
---

# Roslyn inheritance graph

Walk type inheritance and interface relationships with explicit depth and result limits.

## Workflow

1. Resolve the type to a stable symbol ID.
2. Call the `csharp_roslyn` MCP tool `inheritance_graph` with direction `base`, `derived`, or `both`.
3. Include interfaces when contract relationships matter. Start at depth 2 and at most 50 edges.
4. Report edge kinds, cycles, exact symbols, projects, source locations, and truncation.
5. Use `implementation_map` for member-level implementations and `affected_symbols` before contract edits.

## Common scenarios

- Understand a class hierarchy before refactoring.
- Find all derived types of a base class.
- Trace interface inheritance and implementation.
- Review polymorphic blast radius.

The graph covers symbols available to the loaded compilation; runtime-loaded plugins and reflection-created types may be absent.

