---
name: csharp-roslyn-rename-preview
description: Use csharp_roslyn rename_preview for previewing read-only rename and signature refactors.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-rename-preview/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `rename_preview` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

