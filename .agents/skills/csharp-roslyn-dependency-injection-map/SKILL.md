---
name: csharp-roslyn-dependency-injection-map
description: Use csharp_roslyn dependency_injection_map to find DI registrations by service, lifetime, registration shape, and confidence.
---

# Roslyn dependency injection map

Map compiler-bound Microsoft DI calls and convention-shaped registration extensions.

## Workflow

1. Call the `csharp_roslyn` MCP tool `dependency_injection_map` for the trusted workspace.
2. Scope by `projectName`, stable `serviceSymbol`, and `lifetimes` whenever possible.
3. Keep `maxResults` at 50 or less.
4. Report service and implementation types, lifetime, generic or `typeof` or factory shape, exact location, and confidence.
5. Use `implementation_map` for contract implementations and `construction_options` to answer how a consumer project can instantiate or resolve a type.

## Common scenarios

- Find every registration for an interface.
- Detect lifetime mismatches.
- Locate factory registrations.
- Identify composition roots before replacing a service.

Framework-recognized registrations are stronger evidence than convention-shaped extension methods, but neither proves the final runtime container after conditionals, reflection, assembly scanning, or configuration.

