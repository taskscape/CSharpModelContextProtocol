---
name: csharp-roslyn-context-bundle
description: Use csharp_roslyn context_bundle for gathering bounded understand, contract-change, or debug context.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-context-bundle/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `context_bundle` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

