---
name: csharp-roslyn-rename-preview
description: Use csharp_roslyn rename_preview to preview read-only Roslyn rename or signature changes with conflicts, diagnostics, edits, and freshness checks.
---

# Roslyn rename preview

Preview a solution-wide refactor in Roslyn's immutable in-memory solution without writing files.

## Workflow

1. Resolve the target to a stable symbol ID and capture the current workspace snapshot.
2. Call the `csharp_roslyn` MCP tool `rename_preview` with `refactorKind: rename` and a valid `newName`, or `refactorKind: signature` and the requested parameter specification.
3. Supply `expectedFingerprint` on a repeated or approval-stage call so stale source is rejected.
4. Review Roslyn conflicts, introduced diagnostics, snapshot fingerprint, document checksums, and structured per-document edits. Page with the opaque cursor instead of increasing output without bound.
5. Apply approved changes through normal source patches, never by assuming the preview wrote files.
6. Run `diagnostics`, the authoritative build, and relevant tests after editing.

## Common scenarios

- Rename a type or member safely.
- Preview parameter add, remove, or reorder impact.
- Review multi-file changes before editing.
- Detect a stale refactor proposal after intervening changes.

Required new signature arguments may be represented with review-needed defaults. Runtime string references, reflection, configuration, and external consumers remain outside the rename engine.

