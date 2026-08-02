---
name: csharp-roslyn-region-flow
description: Use csharp_roslyn region_flow for analyzing data and control flow in a statement region.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-region-flow/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `region_flow` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

