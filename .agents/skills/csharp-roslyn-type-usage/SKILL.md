---
name: csharp-roslyn-type-usage
description: Use csharp_roslyn type_usage to locate construction sites, DI candidates, API exposure, and other compiler-bound uses of a type.
---

# Roslyn type usage

Classify where and how one type participates in the solution.

## Workflow

1. Resolve the type to a documentation ID or metadata name.
2. Call the `csharp_roslyn` MCP tool `type_usage` with the absolute workspace path and normally `maxResults: 50` or less.
3. Separate object construction, DI-registration candidates, public API signatures, and other type references.
4. Use exact locations and projects to identify composition roots and exposed contracts.
5. Follow with `construction_options` for instantiation questions or `dependency_injection_map` for registration detail.

## Common scenarios

- Locate all ways a service enters the object graph.
- Determine whether a type leaks through a public API.
- Plan a type move or replacement.
- Find likely composition roots.

Convention-shaped registrations are candidates, not proof of runtime container behavior. Reflection, configuration, serializers, and external consumers require separate evidence.

