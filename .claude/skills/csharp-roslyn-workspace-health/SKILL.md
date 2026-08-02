---
name: csharp-roslyn-workspace-health
description: Use csharp_roslyn workspace_health for checking workspace completeness, configuration, and freshness.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-workspace-health/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `workspace_health` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

