---
name: csharp-roslyn-revoke-trust
description: Use csharp_roslyn revoke_trust for removing CSharpMCP trust for a repository root.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-revoke-trust/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `revoke_trust` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

