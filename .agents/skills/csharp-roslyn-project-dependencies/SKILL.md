---
name: csharp-roslyn-project-dependencies
description: Use csharp_roslyn project_dependencies to inspect project, package, reverse, transitive, cycle, and namespace dependencies.
---

# Roslyn project dependencies

Use compiler and project evaluation facts to review dependency direction and layering.

## Workflow

1. Call the `csharp_roslyn` MCP tool `project_dependencies` with the absolute workspace path. Scope to one `projectName` when possible.
2. Leave `includeNamespaceEdges` false for a quick project/package graph. Enable it only for architecture questions.
3. Start with `maxResults: 50` or less and narrow before expanding.
4. Report direct and transitive project dependencies, reverse dependants, direct NuGet packages, cycles, and any requested namespace edges.
5. Use `architecture_rule_check` when the task provides enforceable namespace rules.

## Common scenarios

- Decide where a reference can be added without reversing a layer.
- Find projects transitively affected by a library change.
- Detect project-reference cycles.
- Inspect high-volume cross-namespace coupling.

Namespace edges are compile-time source references, not runtime service, message, database, or deployment dependencies.

