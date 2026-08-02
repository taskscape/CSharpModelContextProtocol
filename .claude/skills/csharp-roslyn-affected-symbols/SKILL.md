---
name: csharp-roslyn-affected-symbols
description: Use csharp_roslyn affected_symbols for planning contract, signature, namespace, or type changes.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-affected-symbols/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `affected_symbols` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

