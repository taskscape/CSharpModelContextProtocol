---
name: csharp-roslyn-construction-options
description: Use csharp_roslyn construction_options to determine accessible constructors, factories, required members, DI registrations, and InternalsVisibleTo effects.
---

# Roslyn construction options

Answer how a specific project can legally construct or resolve one type.

## Workflow

1. Resolve the target type and, when relevant, the consuming `fromProject`.
2. Call the `csharp_roslyn` MCP tool `construction_options` with an exact declaring project if needed.
3. Review constructors with full parameter and accessibility details, required members, static factories found across the solution, and DI registrations.
4. Distinguish generally declared options from options accessible to `fromProject`, including `InternalsVisibleTo`.
5. Use the result to design production or test construction without guessing about internal access.

## Common scenarios

- Construct a service in a unit test.
- Find a preferred static factory.
- Understand required initialization.
- Determine whether a project can call an internal constructor.

Runtime factories, reflection, configuration-driven activation, and containers outside recognized source patterns may provide additional options.

