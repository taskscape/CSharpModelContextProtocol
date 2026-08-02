---
name: csharp-roslyn-implementation-map
description: Use csharp_roslyn implementation_map for changing interfaces, abstract members, or virtual contracts.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-implementation-map/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `implementation_map` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

