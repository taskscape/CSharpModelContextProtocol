---
name: csharp-roslyn-diagnostics
description: Use csharp_roslyn diagnostics for running compiler and analyzer verification gates.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-diagnostics/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `diagnostics` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

