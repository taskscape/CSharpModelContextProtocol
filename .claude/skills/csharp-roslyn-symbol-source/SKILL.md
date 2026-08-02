---
name: csharp-roslyn-symbol-source
description: Use csharp_roslyn symbol_source for retrieving bounded original declarations and method bodies.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-symbol-source/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `symbol_source` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

