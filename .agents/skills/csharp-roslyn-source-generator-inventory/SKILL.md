---
name: csharp-roslyn-source-generator-inventory
description: Use csharp_roslyn source_generator_inventory to inspect configured generators, diagnostics, generated document IDs, and bounded source excerpts.
---

# Roslyn source generator inventory

Inspect generator inputs and outputs without returning all generated source into model context.

## Workflow

1. Call the `csharp_roslyn` MCP tool `source_generator_inventory` with an exact `projectName` when possible.
2. Filter by generator name, generated document ID, or hint name when investigating one output.
3. Keep declarations and excerpts bounded. Use the returned opaque cursor for another page.
4. Report generator candidates, generator diagnostics, generated document IDs and hint names, declarations, and workspace warnings.
5. Retrieve an excerpt by generated document ID rather than asking for every generated file.

## Common scenarios

- Diagnose missing generated members.
- Find the generated document declaring a symbol.
- Review generator diagnostics after a package update.
- Inspect a bounded generated-source excerpt.

The public workspace API cannot always associate every generated document authoritatively with one generator. State that limitation and verify with the generator's own diagnostics or build output when necessary.

