---
name: csharp-roslyn-context-bundle
description: Use csharp_roslyn context_bundle for one bounded understand, contract-change, or debug-flow semantic package around a symbol.
---

# Roslyn context bundle

Gather a deliberately bounded multi-fact context for one symbol when several focused tools would otherwise be needed.

## Workflow

1. Resolve one stable symbol ID.
2. Call the `csharp_roslyn` MCP tool `context_bundle` with profile `understand`, `contract-change`, or `debug-flow`.
3. Keep `maxResultsPerSection` between 10 and 20.
4. Review the profile-specific sections, their counts and truncation, workspace warnings, and the recommended next focused tool.
5. Use a focused tool for deeper follow-up rather than repeatedly enlarging the bundle.

## Common scenarios

- Understand an unfamiliar type or method.
- Prepare a contract change.
- Triage execution flow around a stack frame.
- Quickly orient before choosing a precise query.

Do not use the bundle as a universal dump. Its strict bounds trade completeness for low-context orientation, and runtime-only relationships remain outside Roslyn.

