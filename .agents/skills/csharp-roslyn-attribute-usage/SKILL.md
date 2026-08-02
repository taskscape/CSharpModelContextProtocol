---
name: csharp-roslyn-attribute-usage
description: Use csharp_roslyn attribute_usage to find exact attribute applications, constructor arguments, inherited use, and Obsolete migration groups.
---

# Roslyn attribute usage

Find symbols decorated with one exact attribute type and inspect semantic argument values.

## Workflow

1. Resolve the attribute by stable ID, metadata name, or qualified name.
2. Call the `csharp_roslyn` MCP tool `attribute_usage` with an exact project when needed.
3. Use `targetKinds` to limit types, methods, properties, or other symbol kinds.
4. Enable `includeInherited` only when AttributeUsage inheritance semantics matter.
5. For `System.ObsoleteAttribute`, enable `includeMigrationGroups` to group targets by message and error severity.
6. Report target identity, exact attribute constructor identity, positional and named arguments, inherited status, and location.

## Common scenarios

- Find authorization, serialization, or framework annotations.
- Plan an Obsolete migration.
- Audit attribute arguments across a project.
- Distinguish direct from inherited applications.

Attributes can participate in runtime reflection or framework conventions; this tool reports compile-time applications, not their runtime effect.

