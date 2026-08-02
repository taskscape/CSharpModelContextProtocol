---
name: csharp-roslyn-inheritance-graph
description: Use csharp_roslyn inheritance_graph for tracing base, derived, interface, and implementation edges.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-inheritance-graph/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `inheritance_graph` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

