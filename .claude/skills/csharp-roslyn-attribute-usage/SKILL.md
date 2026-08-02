---
name: csharp-roslyn-attribute-usage
description: Use csharp_roslyn attribute_usage for auditing attributes, arguments, inheritance, and obsolescence.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-attribute-usage/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `attribute_usage` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

