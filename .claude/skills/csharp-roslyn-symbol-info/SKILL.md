---
name: csharp-roslyn-symbol-info
description: Use csharp_roslyn symbol_info for resolving unfamiliar or ambiguous types and members.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-symbol-info/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `symbol_info` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

