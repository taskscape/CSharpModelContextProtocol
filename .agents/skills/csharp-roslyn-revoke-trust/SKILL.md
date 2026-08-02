---
name: csharp-roslyn-revoke-trust
description: Use csharp_roslyn revoke_trust to remove session and persistent CSharpMCP trust for one exact repository root.
---

# Roslyn revoke trust

Remove authorization for future CSharpMCP workspace evaluation without changing repository files.

## Workflow

1. Resolve the exact absolute solution, project, or repository-directory path whose normalized root should be revoked.
2. Confirm that trust removal is intended; subsequent semantic calls for that root will fail until trust is granted again.
3. Call the `csharp_roslyn` MCP tool `revoke_trust`.
4. Report whether session trust and persistent trust were each removed.
5. Optionally verify the result with `list_trusted_paths`.

## Common scenarios

- Remove trust for an archived or unreviewed repository.
- Clear a persistent authorization.
- Reset trust before analyzing a newly replaced checkout.

This operation changes only the CSharpMCP trust store. It does not delete, edit, unload, or otherwise modify repository files.

