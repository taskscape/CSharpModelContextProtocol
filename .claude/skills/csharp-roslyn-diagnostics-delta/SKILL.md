---
name: csharp-roslyn-diagnostics-delta
description: Use csharp_roslyn diagnostics_delta for comparing diagnostics before and after an edit.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-diagnostics-delta/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `diagnostics_delta` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

