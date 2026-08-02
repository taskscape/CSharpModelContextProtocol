---
name: csharp-roslyn-resolve-stack-trace
description: Use csharp_roslyn resolve_stack_trace for mapping .NET stack frames to loaded source symbols.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-resolve-stack-trace/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `resolve_stack_trace` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

