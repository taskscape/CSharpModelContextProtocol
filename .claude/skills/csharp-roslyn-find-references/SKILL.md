---
name: csharp-roslyn-find-references
description: Use csharp_roslyn find_references for impact analysis, mutation searches, and test-only references.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-find-references/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `find_references` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

