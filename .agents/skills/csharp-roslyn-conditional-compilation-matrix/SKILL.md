---
name: csharp-roslyn-conditional-compilation-matrix
description: Use csharp_roslyn conditional_compilation_matrix to compare C# declarations and diagnostics across configurations, target frameworks, and symbol sets.
---

# Roslyn conditional compilation matrix

Compare separately evaluated MSBuild variants and bounded preprocessor-symbol combinations.

## Workflow

1. Identify the project and the real `configurations`, `targetFrameworks`, and preprocessor symbol sets to compare.
2. Call the `csharp_roslyn` MCP tool `conditional_compilation_matrix`. Add a document filter for file-specific questions.
3. Keep the Cartesian product small; the server permits at most 32 variants.
4. Compare evaluated configuration/framework, declared symbols, inactive regions, and diagnostics for each variant.
5. Treat variant-specific workspace-load diagnostics as an incomplete-model warning and confirm affected variants with real builds.

## Common scenarios

- Find code compiled only in Debug or Release.
- Compare multi-target API availability.
- Diagnose a conditional declaration or diagnostic.
- Review feature-flagged compile-time code.

Only requested variants are analyzed. Runtime feature flags and deployment configuration are outside compile-time conditional analysis.

