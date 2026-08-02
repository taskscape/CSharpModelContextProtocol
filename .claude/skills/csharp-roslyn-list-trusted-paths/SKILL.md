---
name: csharp-roslyn-list-trusted-paths
description: Use csharp_roslyn list_trusted_paths for auditing session and persistent workspace trust.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-list-trusted-paths/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `list_trusted_paths` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

