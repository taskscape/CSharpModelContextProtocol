---
name: csharp-roslyn-solution-overview
description: Use csharp_roslyn solution_overview to orient in an unfamiliar C# solution, inspect projects and target frameworks, or establish architecture before editing.
---

# Roslyn solution overview

Use this skill to establish the compiler-evaluated shape of a C# workspace before broad analysis or changes.

## Workflow

1. Resolve the absolute `.sln`, `.slnx`, `.slnf`, or `.csproj` path.
2. Ensure the repository is trusted. If it is not, explain that MSBuild evaluation can execute repository-supplied build logic and use `trust_solution` only with authorization.
3. Call the `csharp_roslyn` MCP tool `solution_overview`. Pass the real build `configuration`, an explicit `targetFramework` when relevant, and normally keep `maxProjects` at 50 or less.
4. Report projects, frameworks, references, entry points, nullable and compiler settings, plus workspace-load diagnostics.
5. If any project is skipped or load diagnostics exist, treat later semantic results as potentially incomplete and confirm with `workspace_health` and the repository build.

## Common scenarios

- Orient before a multi-project refactor.
- Identify entry points and project boundaries.
- Compare Debug, Release, or one target-framework evaluation.
- Decide which project should receive a new dependency.

Do not infer runtime composition, reflection, configuration, or deployment behavior from this tool alone.

