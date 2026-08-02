---
name: csharp-roslyn-implementation-map
description: Use csharp_roslyn implementation_map before editing interfaces, abstract members, virtual methods, handlers, or polymorphic contracts.
---

# Roslyn implementation map

Map a compiler contract to concrete source implementations and overrides before changing it.

## Workflow

1. Resolve the interface, abstract type or member, or virtual member to a stable ID.
2. Call the `csharp_roslyn` MCP tool `implementation_map` with an exact `projectName` if needed and normally `maxResults: 50` or less.
3. Separate direct implementations, derived types, and overrides in the explanation.
4. Treat truncation or workspace-load diagnostics as blockers to a complete impact claim.
5. Before editing, combine the map with `find_references`, `affected_symbols`, and `dependency_injection_map` when dependency injection is involved.

## Common scenarios

- Change an interface method.
- Add an abstract member.
- Review handler or strategy implementations.
- Find overrides affected by a virtual member change.

Compiler implementation relationships do not prove which implementation is selected at runtime. Verify DI, reflection, configuration, and plugin loading separately.

