---
name: csharp-roslyn-trust-solution
description: Use csharp_roslyn trust_solution for authorizing MSBuild, analyzer, and generator evaluation.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-trust-solution/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `trust_solution` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

