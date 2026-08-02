---
name: csharp-roslyn-list-trusted-paths
description: Use csharp_roslyn list_trusted_paths to audit normalized session and persistent repository roots authorized for Roslyn evaluation.
---

# Roslyn trusted paths

Inspect the CSharpMCP trust boundary without loading a solution.

## Workflow

1. Call the `csharp_roslyn` MCP tool `list_trusted_paths`; it takes no workspace argument.
2. Report normalized roots and distinguish session-only from persisted trust.
3. Compare the list with the exact repository intended for analysis.
4. If a required root is absent, explain the execution risk before using `trust_solution`.
5. If a root is no longer appropriate, use `revoke_trust` only when removal is requested.

## Common scenarios

- Diagnose a workspace authorization failure.
- Audit persistent trust decisions.
- Confirm whether a solution is trusted before analysis.
- Identify stale trusted roots for later review.

Listing is read-only. Do not infer that a trusted repository is safe forever; trust records authorization, not a security audit.

