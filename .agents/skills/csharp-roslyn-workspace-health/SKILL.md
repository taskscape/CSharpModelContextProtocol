---
name: csharp-roslyn-workspace-health
description: Use csharp_roslyn workspace_health to verify load completeness, skipped projects, evaluated configuration, target framework, and cache freshness.
---

# Roslyn workspace health

Assess whether the compiler workspace is complete enough to support reliable semantic conclusions.

## Workflow

1. Call the `csharp_roslyn` MCP tool `workspace_health` with the absolute workspace path, real `configuration`, and an explicit `targetFramework` when relevant.
2. Enable project checks when expected-versus-loaded coverage matters.
3. Review expected, loaded, and skipped projects; evaluated configuration/framework; cache age and invalidation state; and workspace diagnostics.
4. Stop strong semantic claims when required projects are skipped or load diagnostics affect the area under analysis.
5. Restore missing SDKs, workloads, feeds, generated prerequisites, or proprietary references where possible, then rerun with force reload if the schema exposes that option.
6. Confirm with the repository's authoritative build.

## Common scenarios

- Diagnose an MCP workspace that differs from the build.
- Verify a multi-target evaluation.
- Check whether cached results reflect recent edits.
- Establish trustworthiness before a large refactor.

A loaded workspace can still omit runtime-only behavior. Health is about compile-time model completeness, not production readiness.

