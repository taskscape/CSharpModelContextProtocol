---
name: csharp-roslyn-architecture-rule-check
description: Use csharp_roslyn architecture_rule_check for enforcing semantic namespace layering policies.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-architecture-rule-check/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `architecture_rule_check` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

