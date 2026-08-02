---
name: csharp-roslyn-invocation-binding
description: Use csharp_roslyn invocation_binding for explaining overload, receiver, conversion, and argument binding.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-invocation-binding/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `invocation_binding` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

