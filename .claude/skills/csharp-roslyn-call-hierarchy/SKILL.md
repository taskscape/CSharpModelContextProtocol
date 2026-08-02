---
name: csharp-roslyn-call-hierarchy
description: Use csharp_roslyn call_hierarchy for debugging execution flow and tracing callers or callees.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-call-hierarchy/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `call_hierarchy` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

