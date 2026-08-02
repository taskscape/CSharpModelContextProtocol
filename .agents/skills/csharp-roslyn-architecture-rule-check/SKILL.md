---
name: csharp-roslyn-architecture-rule-check
description: Use csharp_roslyn architecture_rule_check to enforce caller-supplied namespace layering rules against semantic type references.
---

# Roslyn architecture rule check

Check explicit namespace boundaries using compiler-resolved source type relationships.

## Workflow

1. Confirm the server was started with the optional `architecture` tool group enabled.
2. Translate the repository's actual policy into 1 to 50 named rules. Each rule needs `FromNamespace` and may provide `Forbid` and/or `AllowOnly` prefixes.
3. Call the `csharp_roslyn` MCP tool `architecture_rule_check`, scoped to a project when practical and normally capped at 50 grouped violations.
4. Report each violated boundary with total reference count and a few exact source examples.
5. Distinguish existing approved exceptions from newly introduced violations when the repository has a baseline policy.

## Common scenarios

- Prevent Domain from depending on Web.
- Enforce a ports-and-adapters boundary.
- Audit namespace coupling before a project split.
- Add a semantic architecture gate.

Rules are caller-supplied policy, not architecture inferred by the tool. Runtime, deployment, message, and database dependencies need separate controls.

