---
name: csharp-roslyn-symbol-source
description: Use csharp_roslyn symbol_source to retrieve bounded original declarations, XML docs, attributes, signatures, and optional bodies for one or more symbols.
---

# Roslyn symbol source

Retrieve exact formatted source declarations without opening whole files or failing an entire batch on one bad query.

## Workflow

1. Supply 1 to 50 stable symbol queries and an exact project when ambiguity is possible.
2. Call the `csharp_roslyn` MCP tool `symbol_source`.
3. Keep `includeBody` false for contract review; enable it only when implementation is required.
4. Bound each result with `maxLines` and `maxCharacters`.
5. Handle each item status independently: `ok`, `notFound`, `ambiguous`, `metadata`, or `unsupportedKind`.
6. Use exact source only as needed for the current decision.

## Common scenarios

- Inspect an unfamiliar method body and its XML docs.
- Batch-retrieve overload declarations.
- Review attributes and signatures before an edit.
- Avoid embedding entire source files.

Metadata symbols have no original source declaration. Refine ambiguous queries rather than selecting the first result.

