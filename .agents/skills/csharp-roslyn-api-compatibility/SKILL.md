---
name: csharp-roslyn-api-compatibility
description: Use csharp_roslyn api_compatibility to enumerate or compare public APIs, including official ApiCompat checks against DLL baselines.
---

# Roslyn API compatibility

Review deterministic public and protected API contracts or compare them with a baseline.

## Workflow

1. Confirm the server was started with the optional `api` tool group enabled.
2. Call the `csharp_roslyn` MCP tool `api_compatibility`, scoped to a production project when possible.
3. Omit `baselinePath` to enumerate a surface. Supply an absolute JSON or DLL baseline to compare; DLL baselines use Microsoft's official ApiCompat rules.
4. Keep `includeCurrentSurface` false unless the actual list is needed. Page with the opaque cursor and keep `maxResults` near 50.
5. Report breaking and non-breaking changes separately, plus tool diagnostics and truncation.
6. Do not modify the baseline.

## Common scenarios

- Gate a package or contracts-library release.
- Review removed or reduced-accessibility members.
- Generate a bounded API baseline view.
- Compare a build against a shipped DLL.

Compatibility results cover the analyzed public contract. Runtime behavior, serialization compatibility, protocol semantics, and downstream source not loaded into the workspace require separate checks.

