---
name: csharp-roslyn-api-compatibility
description: Use csharp_roslyn api_compatibility for reviewing public API surfaces and compatibility baselines.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-api-compatibility/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `api_compatibility` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

