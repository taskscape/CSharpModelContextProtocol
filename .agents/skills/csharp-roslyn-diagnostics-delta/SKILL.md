---
name: csharp-roslyn-diagnostics-delta
description: Use csharp_roslyn diagnostics_delta to capture a process-local baseline and report only diagnostics introduced, resolved, or changed after edits.
---

# Roslyn diagnostics delta

Compare scoped compiler and analyzer diagnostics across an edit without mixing new issues with existing debt.

## Workflow

1. Before editing, call the `csharp_roslyn` MCP tool `diagnostics_delta` without a baseline token. Match the intended project, severity, analyzer, document, ID, and suppression scope.
2. Preserve the returned baseline token.
3. After editing, call the same tool with the token and the same scope.
4. Report introduced, resolved, and changed diagnostics separately, including analyzer identity and exact locations.
5. If the token is unknown or expired, capture a new baseline and disclose that a true before/after comparison is no longer available.
6. Run the authoritative build and relevant tests after the delta check.

## Common scenarios

- Enforce a no-new-warning gate.
- Prove a focused diagnostic was fixed.
- Separate pre-existing analyzer debt from a refactor.
- Compare a changed project or document.

Tokens are bounded and process-local; a server restart invalidates them. This tool does not replace builds or tests.

