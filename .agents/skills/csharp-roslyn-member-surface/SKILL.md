---
name: csharp-roslyn-member-surface
description: Use csharp_roslyn member_surface to inspect constructors, overloads, operators, extension methods, and inherited members of a type.
---

# Roslyn member surface

Inspect a type's callable and visible member surface without retrieving entire source files.

## Workflow

1. Resolve the target type to a stable ID.
2. Call the `csharp_roslyn` MCP tool `member_surface`.
3. Choose the narrowest mode for the task: members, constructors, overloads, operators, or applicable extension methods. Add `memberName` for an overload-only query.
4. State whether inherited members, explicit interface implementations, and metadata extension methods are required; keep `maxResults` at 50 or less.
5. Report signatures, accessibility, modifiers, declaring types, override/interface relationships, and source versus metadata origin.

## Common scenarios

- Find every constructor or overload before changing a call.
- Review operators or conversions.
- Discover applicable LINQ or project extension methods.
- Understand a type's externally callable surface.

Applicable extension methods are compiler candidates, not proof a particular call binds to them; use `invocation_binding` at a real call site for that answer.

