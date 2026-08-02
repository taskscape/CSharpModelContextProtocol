---
name: csharp-roslyn-test-impact
description: Use csharp_roslyn test_impact for selecting statically related tests for a changed symbol.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-test-impact/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `test_impact` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

