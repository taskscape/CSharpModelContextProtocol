---
name: csharp-roslyn-unused-symbol-audit
description: Use csharp_roslyn unused_symbol_audit for reviewing dead-code and test-only-use candidates.
---

# Claude Code adapter

Read and follow [the canonical portable skill](../../../.agents/skills/csharp-roslyn-unused-symbol-audit/SKILL.md) completely before acting.

Call the configured MCP server `csharp_roslyn` and its exact `unused_symbol_audit` tool as directed there. If the MCP server or tool is unavailable, say so explicitly and use a local Roslyn/MSBuild/compiler fallback when possible; never present lexical search alone as compiler-resolved evidence.

