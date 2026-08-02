---
name: csharp-roslyn-test-impact
description: Use csharp_roslyn test_impact to find tests statically reachable from a changed C# symbol and select focused verification.
---

# Roslyn test impact

Find test methods connected to a production symbol through bounded compiler-resolved reference paths.

## Workflow

1. Resolve the production symbol to a stable ID.
2. Call the `csharp_roslyn` MCP tool `test_impact`, normally beginning with depth 2 or 3 and at most 50 tests.
3. Review each test's framework marker, exact location, and evidence path through production or helper symbols.
4. Use the result to choose a focused test set, then include broader repository-required tests for the change risk.
5. If results are truncated, narrow by project or symbol before increasing depth.

## Common scenarios

- Select tests after a method or contract change.
- Find test-only consumers.
- Explain why a test is related to a production symbol.
- Identify obvious test gaps for review.

Static reachability is not code coverage and does not prove that a test executes the behavior at runtime. Dynamic tests, reflection, generated cases, and external harnesses may not be visible.

