---
name: csharp-roslyn-find-references
description: Use csharp_roslyn find_references for exact impact analysis, mutation searches, event use, DI candidates, and test-only references.
---

# Roslyn find references

Find compiler-bound occurrences of one exact symbol and classify how each occurrence uses it.

## Workflow

1. Resolve an unambiguous symbol ID with `symbol_info` or `symbol_at_position`.
2. Call the `csharp_roslyn` MCP tool `find_references` with the absolute workspace path and stable symbol.
3. Use server-side `referenceKinds` filtering whenever the question is narrow. Supported examples include `invocation`, `method_group`, `object_creation`, `read`, `write`, `readwrite`, `attribute`, `nameof`, `typeof`, `cast`, `type_check`, `base_type`, `type_constraint`, `type_argument`, event subscription kinds, `dependency_injection_registration`, and `test_reference`.
4. Set `includeDeclarations` only when declarations belong in the answer. Start with `maxResults` of 50 or less and narrow before paging.
5. Report counts, exact locations, classification, truncation, and workspace warnings. Do not equate zero source references with safe deletion.

## Common scenarios

- Find mutation sites with `write,readwrite`.
- Separate production uses from test-only uses.
- Review a public signature change.
- Locate event or DI-related references.

Reflection, generated runtime wiring, configuration, and dynamic dispatch can remain invisible; pair deletion decisions with `unused_symbol_audit` risk flags and runtime-specific checks.

