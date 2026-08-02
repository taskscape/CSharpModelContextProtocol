---
name: csharp-roslyn-unused-symbol-audit
description: Use csharp_roslyn unused_symbol_audit to find bounded dead-code candidates while preserving runtime-discovery and framework risks.
---

# Roslyn unused symbol audit

Produce a review list of source symbols with no references or only test-project references.

## Workflow

1. Scope the `csharp_roslyn` MCP tool `unused_symbol_audit` to a production `projectName` whenever practical.
2. Select `symbolKinds` from `NamedType`, `Method`, `Property`, `Field`, and `Event`.
3. Keep `includeTestProjectsAsCandidates` false unless test-code cleanup is explicitly requested. Bound `maxSymbols`, `maxResults`, and retained examples.
4. Review the server's filtered-out counts and every runtime-discovery risk flag.
5. Before deletion, run exact `find_references`, inspect attributes and framework conventions, search runtime configuration/reflection sources, then build and test after normal patches.

## Common scenarios

- Find methods used only by tests.
- Prioritize dead private members.
- Audit legacy types after a migration.
- Review apparently unused fields, properties, or events.

The output is a candidate list, never proof that deletion is safe. Public APIs, reflection, serializers, native interop, source generation, dependency injection, and external consumers need explicit review.

