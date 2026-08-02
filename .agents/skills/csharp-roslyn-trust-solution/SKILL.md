---
name: csharp-roslyn-trust-solution
description: Use csharp_roslyn trust_solution only after reviewing a repository, to authorize MSBuild, analyzer, and generator execution for Roslyn queries.
---

# Roslyn trust solution

Grant the CSharpMCP server permission to evaluate one repository root.

## Workflow

1. Explain the security boundary: loading a solution can execute repository-controlled MSBuild tasks, analyzers, and source generators.
2. Resolve the exact absolute solution, project, or repository-directory path.
3. Call the `csharp_roslyn` MCP tool `trust_solution` only when the user has authorized analysis of that repository.
4. Prefer `persist: false` for session-only trust. Use persistent trust only when explicitly requested or established by repository policy.
5. Confirm the normalized trusted root and persistence status.
6. Continue with `workspace_health` before substantive analysis.

## Common scenarios

- First semantic query in a repository.
- Re-establish session trust after a server restart.
- Persist trust for a known local development repository.

This tool changes only CSharpMCP's trust store, not repository files. Do not trust unreviewed or attacker-controlled workspaces merely to make another tool call succeed.

