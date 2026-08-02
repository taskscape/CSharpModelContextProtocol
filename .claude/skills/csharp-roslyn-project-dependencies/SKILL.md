---
name: csharp-roslyn-project-dependencies
description: Use csharp_roslyn project_dependencies for reviewing project, package, cycle, and namespace dependencies.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-project-dependencies/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `project_dependencies` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

