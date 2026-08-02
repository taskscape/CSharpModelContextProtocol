---
name: csharp-roslyn-semantic-search
description: Use csharp_roslyn semantic_search for finding symbols when only a concept or partial name is known.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-semantic-search/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `semantic_search` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

