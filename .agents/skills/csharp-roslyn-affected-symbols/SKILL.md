---
name: csharp-roslyn-affected-symbols
description: Use csharp_roslyn affected_symbols before contract, signature, namespace, or type edits to obtain a sectioned impact plan.
---

# Roslyn affected symbols

Build a conservative compile-time impact package before changing a contract.

## Workflow

1. Resolve the target to one stable symbol ID.
2. Call the `csharp_roslyn` MCP tool `affected_symbols`.
3. Set independent limits for contracts, implementations, production references, callers, tests, and dependent projects. Start near 20 per section.
4. Report each section separately, including total and returned counts, truncation, exact symbols, and projects.
5. Use gaps to choose focused follow-ups such as `implementation_map`, `find_references`, `call_hierarchy`, or `test_impact`.
6. Do not edit until ambiguous identities or incomplete workspace loads are resolved.

## Common scenarios

- Change a public or internal method signature.
- Move a type or namespace.
- Modify an interface or abstract member.
- Estimate affected tests and downstream projects.

This is a conservative static impact set. It does not model reflection, external binaries, configuration, databases, or distributed routing.

