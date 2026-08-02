---
name: csharp-roslyn-type-usage
description: Use csharp_roslyn type_usage for finding construction, DI, API, and other type uses.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-type-usage/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `type_usage` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

