---
name: csharp-roslyn-symbol-at-position
description: Use csharp_roslyn symbol_at_position for resolving exact semantics at a C# source coordinate.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-symbol-at-position/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `symbol_at_position` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

