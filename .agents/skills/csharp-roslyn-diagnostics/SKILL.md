---
name: csharp-roslyn-diagnostics
description: Use csharp_roslyn diagnostics as a scoped compiler and analyzer gate before and after meaningful C# edits.
---

# Roslyn diagnostics

Run compiler and optionally repository-configured analyzer diagnostics for a trusted workspace.

## Workflow

1. Confirm the workspace is trusted before analyzer execution. Trusting a repository authorizes MSBuild, analyzers, and generators supplied by it.
2. Call the `csharp_roslyn` MCP tool `diagnostics`. Scope with `projectName`, `documentPath`, `minimumSeverity`, and `diagnosticIds` rather than requesting the whole solution by default.
3. Set `includeAnalyzers: true` for the normal verification gate; include suppressed diagnostics only when auditing configuration or debt.
4. Keep `maxResults` at 50 or less unless paging a specific filtered set.
5. Report diagnostic ID, severity, analyzer identity, location, message, suppression state, truncation, and workspace-load warnings.
6. Run the repository's authoritative build and relevant tests afterward.

## Common scenarios

- Check a changed file or project.
- Investigate one compiler or analyzer ID.
- Audit suppressed warnings.
- Validate an edit before a build.

Roslyn diagnostics are static analysis, not build-system, runtime, or test proof. Use `diagnostics_delta` when the requirement is to distinguish newly introduced diagnostics.

