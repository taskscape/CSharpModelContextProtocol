# Proposed CSharpMCP Extensions

## Purpose

This document proposes additional read-only tools for `csharp_roslyn`. The proposals follow the existing server's pattern: load the real solution through `MSBuildWorkspace`, answer one recognizable engineering question, return bounded compiler-derived evidence, and leave source edits, builds, and tests to the calling agent.

The current tools already cover solution orientation, symbol lookup, references, call hierarchy, implementations, type usage, diagnostics, dependencies, name-based semantic search, method-reference auditing, and conservative affected-symbol analysis. The extensions below are intended to close different gaps rather than create aliases for those tools.

OpenAI's MCP guidance recommends focused tools built around user goals, explicit input and output schemas, stable identifiers, truthful read-only annotations, and concise structured results. It also recommends evaluating tool metadata against direct, indirect, and negative prompts. Those principles should govern which of these ideas are promoted into the public catalog:

- [Define tools](https://developers.openai.com/plugins/plan/tools)
- [Build an MCP server](https://developers.openai.com/plugins/build/mcp-server)
- [Optimize tool metadata](https://developers.openai.com/plugins/guides/optimize-metadata)

## Recommended implementation order

The best first tranche is:

1. `symbol_at_position`
2. `invocation_binding`
3. `member_surface`
4. `inheritance_graph`
5. `rename_preview`
6. `diagnostics_delta`
7. `test_impact`
8. `source_generator_inventory`
9. `conditional_compilation_matrix`
10. `workspace_health`

Implementation status: the first tranche is implemented in the MCP 2.0 server. The later portfolio review was also applied without creating aliases: `attribute_usage`, `event_flow`, `symbol_source`, `dependency_injection_map`, `construction_options`, `api_compatibility`, `region_flow`, `architecture_rule_check`, `resolve_stack_trace`, and the merged `context_bundle` are advertised tools. Explicit workspace trust is provided by `trust_solution`, `list_trusted_paths`, and `revoke_trust`. Existing tools absorb reference-kind filtering, dependency cycles/packages, diagnostic filters, richer change impact, rename freshness, generated-source selection, and applicable extension/overload inspection. Write-capable refactors, mutable solution lifecycle, background tasks, and overlapping aliases remain intentionally unimplemented.

These tools answer frequent questions that currently require several calls or cannot be answered precisely by the existing catalog. Add later tools only after prompt-replay evaluation shows a real usage path and no material selection overlap.

## Shared contract for all extensions

Every proposed tool should remain `ReadOnly = true` and `Idempotent = true`. A preview tool may create an in-memory Roslyn `Solution`, but it must never call `TryApplyChanges`, write a file, execute a build, restore packages, run tests, or invoke source generators outside Roslyn's normal compilation pipeline.

All results should preserve the existing `BoundedResult<T>` envelope and add these common fields where applicable:

- `workspaceIdentity`: normalized workspace path plus a deterministic snapshot fingerprint.
- `projectId` and `project`: both the snapshot-scoped Roslyn project ID and readable project name.
- `symbolId`: documentation comment ID when available, with a serialized `SymbolKey` as an optional secondary identity.
- `location`: document path, one-based line and column, and a short excerpt.
- `evidenceKind`: for example `compiler-bound`, `syntax-derived`, `configuration-derived`, or `heuristic`.
- `confidence`: `exact`, `conservative`, or `candidate`; never imply runtime proof for convention or string-based findings.
- `returned`, `totalKnown`, and `truncated`: distinguish a bounded response from a complete result.
- `workspaceDiagnostics` and `limitations`: make incomplete workspace loads and runtime blind spots visible on every relevant call.

Prefer explicit enums and bounded integer parameters over free-form modes. All collection-producing tools should accept `projectName`, `maxResults`, and any domain-specific depth or item bounds. Expensive queries should accept cancellation, report scanned project/document counts, and reuse indexes cached against the workspace snapshot fingerprint.

## Priority A: everyday navigation and edit planning

### 1. `symbol_at_position`

**User goal:** Answer “what does this token mean here?” when the caller has a file and cursor position rather than a symbol ID.

**Why it is valuable:** This is the missing bridge from a compiler diagnostic, diff hunk, or text-search hit into the stable identifiers required by most existing tools. It removes a fragile name-resolution round trip.

**Suggested contract:** Inputs: `workspacePath`, absolute `documentPath`, one-based `line`, one-based `column`, and optional `includeCandidates`. Output: bound symbol, declared symbol when the position is a declaration, type information, converted type, enclosing symbol, candidate symbols/reason, and exact source span.

**Implementation:** Resolve the `Document` by normalized path, convert line/column through `SourceText.Lines`, find the smallest relevant syntax node/token, then query `SemanticModel.GetDeclaredSymbol`, `GetSymbolInfo`, `GetTypeInfo`, and `GetEnclosingSymbol`. Normalize aliases, reduced extension methods, and constructed generics before producing stable IDs. Reject positions outside the document and make ambiguous candidate reasons explicit.

### 2. `invocation_binding`

**User goal:** Explain exactly which overload, extension method, constructor, indexer, operator, or delegate target an expression invokes.

**Why it is valuable:** Overload resolution is one of the clearest advantages Roslyn has over text search. It helps agents safely alter arguments, generic constraints, optional parameters, or extension methods.

**Suggested contract:** Inputs: workspace/document position or a bounded source span. Output: selected callable symbol, receiver type, argument-to-parameter mapping, inferred generic arguments, conversions, defaulted/params arguments, extension reduction details, and rejected candidates when binding failed.

**Implementation:** Obtain `IInvocationOperation`, `IObjectCreationOperation`, `IPropertyReferenceOperation`, or operator operations from `SemanticModel.GetOperation`. Combine operation data with `GetSymbolInfo` candidate symbols and `Compilation.ClassifyConversion`. Keep diagnostics local to the invocation span.

### 3. `member_surface`

**User goal:** Inspect the effective API of a type, including inherited members and where each member originates.

**Why it is valuable:** `symbol_info` describes one symbol, but agents often need the complete contract before choosing an extension point or deciding whether a new member duplicates inherited behavior.

**Suggested contract:** Inputs: type symbol, optional `memberKinds`, `accessibility`, `includeInherited`, and `includeExplicitInterfaceMembers`. Output: declared/effective members grouped by kind, stable IDs, signatures, accessibility, declaring type, overridden member, implemented interface members, and hide/override status.

**Implementation:** Walk `INamedTypeSymbol.GetMembers`, base types, and `AllInterfaces`; use `FindImplementationForInterfaceMember`, `OverriddenMethod`/property/event, and `HidesBaseMethodsByName`. Deduplicate by `SymbolEqualityComparer.Default` and cap each member group.

### 4. `inheritance_graph`

**User goal:** See base classes, derived classes, interfaces, derived interfaces, and implementing types around a selected type.

**Why it is valuable:** `implementation_map` is contract-focused, but architectural work often needs a bidirectional type hierarchy with distance and project boundaries.

**Suggested contract:** Inputs: type symbol, `direction` (`ancestors`, `descendants`, or `both`), `maxDepth`, `includeInterfaces`, and `maxResults`. Output: nodes and typed edges such as `inherits`, `implements`, and `interface-inherits`, including abstract/sealed flags.

**Implementation:** Use the symbol's `BaseType` and interfaces for ancestors and `SymbolFinder.FindDerivedClassesAsync`, `FindDerivedInterfacesAsync`, and `FindImplementationsAsync` for descendants. Traverse breadth-first with stable-ID cycle detection and bounded depth.

### 5. `attribute_usage`

**User goal:** Find where an attribute is applied and inspect the semantically bound constructor/named arguments.

**Why it is valuable:** Attributes drive routing, serialization, tests, analyzers, source generation, authorization, DI, and runtime discovery. Plain text cannot reliably distinguish same-named attributes or inherited behavior.

**Suggested contract:** Inputs: attribute type symbol, optional target kinds, project, and `includeInherited`. Output: attributed symbol, attribute constructor ID, positional/named constant values, application location, and whether the result is direct or inherited.

**Implementation:** Resolve the attribute `INamedTypeSymbol`, enumerate source symbols efficiently, and compare each `AttributeData.AttributeClass` using `SymbolEqualityComparer.Default`. Serialize `TypedConstant` values safely and bound array output. Inherited attributes should be marked conservative because `AttributeUsageAttribute` rules matter.

### 6. `event_flow`

**User goal:** Trace event declarations, subscriptions, unsubscriptions, and raises.

**Why it is valuable:** Event-driven control flow is poorly represented by a normal call hierarchy, and missed unsubscription sites can cause leaks or duplicate behavior.

**Suggested contract:** Inputs: event symbol and optional project/depth bounds. Output: `subscribe`, `unsubscribe`, and `raise` edges; handler symbols; locations; static/instance status; and unresolved dynamic handlers.

**Implementation:** Find exact event references, inspect `IEventAssignmentOperation` and event-reference operations, resolve delegate operands to method symbols where possible, and detect raises inside the declaring type. Treat lambdas and method groups explicitly; report handlers stored in variables as unresolved candidates rather than guessing.

### 7. `delegate_flow`

**User goal:** Identify method-group/lambda assignments and likely invocation paths for a delegate type or delegate-valued symbol.

**Why it is valuable:** Callback-heavy code, pipelines, middleware, and strategy maps hide execution edges from ordinary call hierarchy results.

**Suggested contract:** Inputs: delegate type or field/property/local/parameter symbol, optional depth and result bounds. Output: assignment targets, conversions, invocations, passed-as-argument sites, and confidence per edge.

**Implementation:** Use `IDelegateCreationOperation`, `IAnonymousFunctionOperation`, `IMethodReferenceOperation`, conversions, assignments, and invocations. Exact method-group bindings are compiler-bound; flows through collections, fields, returns, or external assemblies must be labeled conservative.

### 8. `rename_preview`

**User goal:** Determine whether a symbol rename is valid and see the exact edits and conflicts before modifying files.

**Why it is valuable:** A reference list does not reveal namespace collisions, overload ambiguities, string/comment changes, or linked/generated document behavior caused by a proposed name.

**Suggested contract:** Inputs: target symbol, `newName`, optional project, and flags for strings/comments. Output: validity, conflicts, affected documents/projects, bounded text changes, changed symbol identities, and diagnostics introduced in the in-memory result.

**Implementation:** Use Roslyn's rename APIs against an immutable in-memory `Solution`, then diff old/new `Document` text and compare diagnostics for changed projects. Never call `TryApplyChanges`. Verify the exact Roslyn 5.6 rename option API during implementation because overloads have evolved across versions.

### 9. `signature_change_preview`

**User goal:** Preview the blast radius of adding, removing, reordering, or changing method parameters and return types.

**Why it is valuable:** `affected_symbols` finds related symbols but cannot show which calls would fail or how overload selection would change.

**Suggested contract:** Inputs: method symbol plus a structured proposed signature model; optionally allow only one change category per call. Output: impacted declarations, calls, method groups, overrides/interfaces, newly ambiguous calls, compile diagnostics delta, and locations requiring manual decisions.

**Implementation:** Rewrite only the declaration in an in-memory solution using `SyntaxGenerator` or carefully constructed syntax, recompile affected/transitively dependent projects, and compare invocation bindings and diagnostics. Do not synthesize call-site fixes in the first version. Start with parameter rename/type/default changes; defer generic-arity and ref-kind transformations until separately tested.

### 10. `diagnostics_delta`

**User goal:** Report only diagnostics introduced, removed, or changed by the current edits or an in-memory preview.

**Why it is valuable:** Large legacy solutions may have extensive baseline warnings. Agents need a high-signal verification gate that distinguishes regressions from pre-existing debt.

**Suggested contract:** Inputs: workspace path, project selection, minimum severity, analyzer flag, and either a baseline fingerprint/snapshot token or explicit baseline diagnostic set. Output: `introduced`, `resolved`, and `changed` diagnostics with stable comparison keys and counts.

**Implementation:** Normalize diagnostics by project, ID, file, span, severity, and message arguments. Cache a bounded baseline against a snapshot fingerprint or accept a client-supplied baseline token. Invalidate on project/analyzer/editorconfig/package changes. Do not hide the full current count or workspace-load diagnostics.

### 11. `test_impact`

**User goal:** Select the most relevant tests for a changed symbol or set of changed files.

**Why it is valuable:** Running an entire large test estate after every change is expensive, while filename guesses miss compiler-bound dependencies.

**Suggested contract:** Inputs: changed symbol IDs and/or document paths, project scope, `maxDepth`, and `maxResults`. Output: candidate test methods, evidence paths from change to test, test framework/attribute, project, confidence, and unresolved runtime-dispatch risks.

**Implementation:** Seed from declared/enclosing changed symbols, traverse bounded callers, implementations, overrides, type usage, and project dependents, then identify test methods through semantically resolved test attributes and test project metadata. Cache reverse call/reference indexes per snapshot. Results are prioritization candidates, never proof that unlisted tests are unnecessary.

### 12. `semantic_diff`

**User goal:** Summarize what a set of file changes means at the symbol level rather than returning a text diff.

**Why it is valuable:** Review and handoff prompts benefit from knowing that an API signature, accessibility, base type, attribute, or body changed, even when the textual diff is noisy.

**Suggested contract:** Inputs: current workspace plus either baseline workspace path or a bounded patch/baseline document set. Output: added/removed/changed symbols, signature and attribute deltas, body-only changes, affected projects, and stable old/new IDs.

**Implementation:** Load two immutable solutions or construct a baseline solution with `WithDocumentText`; match projects/documents by normalized path and symbols by documentation ID plus declaration identity. Compare declared symbol metadata and syntax equivalence. Avoid automatically invoking Git; keep revision material explicit so the tool remains deterministic and transport-independent.

## Priority B: audits and architecture safety

### 13. `unused_symbol_audit`

**User goal:** Extend method auditing to unused private types, fields, properties, events, constructors, and local functions.

**Why it is valuable:** Dead-code cleanup is broader than ordinary methods, but each symbol kind has different compiler and runtime caveats.

**Suggested contract:** Inputs: project, allowed symbol kinds, accessibility ceiling, result/reference limits, and test-project policy. Output: candidates classified by symbol kind, production/test/self references, exclusions, risk flags, and evidence.

**Implementation:** Reuse the single-pass semantic reference index now exposed by `unused_symbol_audit`, while maintaining kind-specific exclusion policies. Exclude or flag serialization members, XAML/Razor bindings, reflection-shaped members, designer/generated code, P/Invoke, fields referenced by name, and public/protected APIs. Expand each symbol kind with dedicated fixtures rather than treating every unreferenced declaration as safely removable.

### 14. `api_surface`

**User goal:** Produce the externally consumable API for a project, assembly, namespace, or type.

**Why it is valuable:** Agents need a precise boundary before refactoring libraries, DTOs, controller contracts, or compatibility bridges.

**Suggested contract:** Inputs: scope, accessibility threshold, `includeInternalsVisibleTo`, attribute/doc flags, and result limits. Output: normalized public/protected signatures, base/interface contracts, nullability, generic constraints, constants/defaults, and declaring locations.

**Implementation:** Walk assembly namespaces and symbols from `Compilation.Assembly.GlobalNamespace`; apply effective accessibility, including containing types and `InternalsVisibleTo`. Serialize signatures with a stable `SymbolDisplayFormat` that includes nullable annotations, ref kinds, tuple names, and constraints.

### 15. `api_compatibility_diff`

**User goal:** Detect source- and binary-breaking API changes between two solution/project versions.

**Why it is valuable:** This turns review of public contract changes into an objective gate and complements `affected_symbols`, which only sees current source consumers.

**Suggested contract:** Inputs: baseline/current workspace or assembly paths, project/assembly mapping, and compatibility mode (`source`, `binary`, or `both`). Output: removed/changed members, accessibility reductions, signature/nullability/constraint/base-type changes, severity, and rationale.

**Implementation:** Build normalized API surfaces from both compilations and compare stable identities. Model binary names separately from C# source signatures. Start with removals and signature/accessibility changes; label nuanced overload-resolution and nullability findings according to compatibility type. Consider integrating `Microsoft.DotNet.ApiCompat` only after confirming its licensing, versioning, and bounded-output behavior.

### 16. `dependency_cycles`

**User goal:** Find cycles and unexpected transitive paths in project or namespace dependencies.

**Why it is valuable:** `project_dependencies` returns edges but makes the agent reconstruct cycles and paths in model context.

**Suggested contract:** Inputs: scope (`project` or `namespace`), optional source/target, maximum path depth, and result bounds. Output: strongly connected components, shortest dependency paths, edge evidence, and cycle summaries.

**Implementation:** Use Roslyn's project dependency graph for projects. For namespaces, derive compiler-bound edges from referenced symbols in syntax/operations, cache the adjacency index, then run Tarjan/Kosaraju for components and breadth-first search for paths. Namespace results should be marked conservative when generated code or unresolved symbols are present.

### 17. `architecture_rule_check`

**User goal:** Verify repository-specific layering rules such as “Web may reference Core, but Core must not reference Web.”

**Why it is valuable:** Architecture decisions become mechanically checkable and can prevent new drift while allowing explicitly documented legacy exceptions.

**Suggested contract:** Inputs: workspace path and structured rules containing source/target project or namespace patterns, allow/deny mode, and exception IDs. Output: violations with referencing and referenced symbols, location, matched rule, exception status, and counts.

**Implementation:** Reuse the project/namespace dependency indexes, validate rule syntax, and match only compiler-bound symbol edges. Keep rules client-supplied or in an explicit versioned repository file; do not infer architecture intent. This remains read-only even when used as a CI gate.

### 18. `package_usage`

**User goal:** Determine which source symbols actually use an assembly or package and whether a package reference appears removable.

**Why it is valuable:** Project files show declared dependencies, not whether code or analyzers rely on them. Dependency cleanup otherwise relies on weak string searches.

**Suggested contract:** Inputs: project and package/assembly name, optional usage kinds, and limits. Output: referenced assembly symbols with source locations, project/package metadata, analyzer/build-only assets, and a removal-candidate classification.

**Implementation:** Map `PackageReference` assets from evaluated MSBuild/NuGet restore metadata to compilation references, then scan bound symbols' `ContainingAssembly`. Separately inspect analyzers, source generators, build props/targets, content files, and native/runtime assets. “No compile-time symbol usage” must remain a candidate because MSBuild targets and runtime loading can still require the package.

### 19. `friend_assembly_map`

**User goal:** Understand `InternalsVisibleTo` exposure and which internal APIs are consumed by friend assemblies.

**Why it is valuable:** Internal contract changes can have a larger blast radius than project-local reference counts suggest, especially in test and proxy-generation assemblies.

**Suggested contract:** Inputs: project/assembly, optional target friend assembly, and limits. Output: declared friend relationships, key/signing information where public, internal symbols referenced by friends, and source locations.

**Implementation:** Inspect assembly attributes semantically and compare cross-project references to symbols with internal effective accessibility. Account for strong-name public keys and generated proxy conventions without exposing secrets. External friend assemblies not loaded in the solution should be reported as an unresolved boundary.

### 20. `code_metrics`

**User goal:** Find large, deeply nested, highly coupled, or complex methods/types that deserve focused review.

**Why it is valuable:** It helps agents prioritize comprehension and refactoring work without dumping entire files into context.

**Suggested contract:** Inputs: project/symbol scope, metric thresholds, generated-code policy, and result limits. Output: lines, statement count, cyclomatic complexity, nesting depth, parameter count, member count, fan-in/fan-out approximations, and locations.

**Implementation:** Calculate syntax metrics from declaration spans and branching constructs; derive operation-level control-flow complexity from `ControlFlowGraph` where available. Use cached reference/call indexes for coupling metrics. Define every metric precisely in output because tool-to-tool complexity formulas differ.

## Priority C: framework and runtime-discovery bridges

### 21. `dependency_injection_map`

**User goal:** Map service contracts to registration sites, lifetimes, implementations/factories, and constructor consumers.

**Why it is valuable:** `type_usage` only labels DI-shaped calls. A dedicated graph directly supports composition-root changes and lifetime reviews.

**Suggested contract:** Inputs: optional service type, project, supported container profile, and limits. Output: registrations, lifetime, implementation/factory type, registration location, constructor consumers, open-generic status, and confidence.

**Implementation:** Resolve known container extension methods by symbol identity, beginning with `Microsoft.Extensions.DependencyInjection` APIs such as `AddSingleton`, `AddScoped`, `AddTransient`, `TryAdd`, enumerable registrations, and common scanning extensions. Evaluate `typeof`, generic arguments, and simple factory returns through `IOperation`. Mark assembly scanning, conditional branches, decorators, and custom container extensions as candidates rather than runtime proof.

### 22. `endpoint_map`

**User goal:** Enumerate ASP.NET MVC, Razor Pages, and Minimal API endpoints with their bound handlers and authorization metadata.

**Why it is valuable:** Routes are a key externally invoked surface that normal references and call hierarchy cannot fully represent.

**Suggested contract:** Inputs: web project, framework modes, optional route prefix/filter, and limits. Output: HTTP methods, route templates, controller/action or handler symbol, parameters, return type, authorization/anonymous metadata, source location, and confidence.

**Implementation:** Resolve MVC/controller base types and routing attributes semantically; combine controller- and action-level templates. Inspect Minimal API `MapGet`/`MapPost`/`MapMethods` invocations through operations and resolve delegate targets. Razor conventions, dynamically assembled routes, endpoint filters, and external startup code must be flagged as incomplete.

### 23. `configuration_binding_map`

**User goal:** Connect configuration keys and option sections to their consuming C# symbols.

**Why it is valuable:** Configuration-driven dependencies are a major Roslyn blind spot and are often the reason apparently unused members cannot be removed.

**Suggested contract:** Inputs: workspace, optional key/section or options type, recognized configuration file patterns, and limits. Output: binding/lookup sites, option type members, string-key locations, configuration-file occurrences, default values, and confidence.

**Implementation:** Semantically identify `IConfiguration` indexers, `GetSection`, `GetValue`, `Bind`, and Options registration APIs; constant-fold string expressions with `SemanticModel.GetConstantValue`. Parse only explicitly supported JSON/XML/config files and connect keys conservatively. Never claim environment-variable, secret-store, or remote-provider completeness.

### 24. `serialization_contract_map`

**User goal:** Identify serialized names, constructors, converters, ignored members, and polymorphic relationships for a DTO/type.

**Why it is valuable:** Serialization members may have no C# references yet remain externally required. This tool would materially improve removal and rename safety.

**Suggested contract:** Inputs: type symbol and serializer profile (`System.Text.Json`, `Newtonsoft.Json`, `DataContract`, or `all`). Output: effective wire names, included/ignored members, constructors, converters, polymorphic discriminators, required/nullability state, and diagnostic risks.

**Implementation:** Resolve framework attribute types and known serializer APIs by metadata identity, not attribute-name fragments. Apply documented inclusion precedence for each supported serializer independently. Configuration supplied through runtime options should be reported as unresolved unless its registration calls are statically analyzable.

### 25. `reflection_risk_scan`

**User goal:** Find string- and reflection-based references that may target a symbol even though Roslyn reports no normal references.

**Why it is valuable:** This is the most useful companion to dead-code and rename previews, provided its output is explicitly heuristic.

**Suggested contract:** Inputs: target symbol, project scope, risk categories, and limits. Output: `typeof`/`nameof` uses, reflection API calls, constant strings matching simple/qualified/member names, assembly/type loading sites, and confidence/reason.

**Implementation:** Bind calls to known reflection APIs (`Type.GetType`, `GetMethod`, `Activator.CreateInstance`, assembly loading, expression/member APIs) and constant-fold arguments. Search constant string literals only after generating bounded target-name variants. Separate exact compiler-bound `typeof`/`nameof` evidence from heuristic text matches and never call the result exhaustive.

### 26. `source_generator_inventory`

**User goal:** Show which source generators ran, which documents they produced, and which diagnostics they emitted.

**Why it is valuable:** Generated symbols affect compilation but are absent from ordinary source-file navigation and can explain surprising declarations or diagnostics.

**Suggested contract:** Inputs: project, optional generator name, `includeGeneratedSourceExcerpt`, and bounds. Output: generator identity, generated document hint names/paths, declared top-level symbols, diagnostics, elapsed time when available, and truncation.

**Implementation:** Use `Project.GetSourceGeneratedDocumentsAsync` and compilation/analyzer references to associate generated documents with generators where public APIs allow. If necessary, construct a `GeneratorDriver` from resolved `ISourceGenerator`/`IIncrementalGenerator` instances, but avoid running generators twice when workspace results are already available. Never return full generated files by default.

### 27. `analyzer_configuration`

**User goal:** Explain why a diagnostic has its current severity and which analyzer/configuration source produced it.

**Why it is valuable:** Agents otherwise waste time treating suppressed, elevated, or generated-code diagnostics as unexplained compiler behavior.

**Suggested contract:** Inputs: project and diagnostic ID, with optional document path. Output: descriptor, analyzer assembly/type, effective severity, relevant `.editorconfig`/globalconfig options, `NoWarn`/`WarningsAsErrors` effects, suppression attributes/pragmas, and source locations.

**Implementation:** Inspect analyzer references and descriptors, `AnalyzerConfigOptionsProvider`, compilation options, project-file warning properties, `SuppressMessageAttribute`, and syntax trivia for pragmas. Report precedence evidence without attempting to reproduce every MSBuild condition not represented in the loaded workspace.

### 28. `conditional_compilation_matrix`

**User goal:** Determine which declarations and code paths exist under target frameworks and preprocessor symbol sets.

**Why it is valuable:** A single loaded compilation can hide branches that will fail in another configuration or target framework.

**Suggested contract:** Inputs: project, bounded configurations/target frameworks or symbol sets, optional document/symbol, and limits. Output: active/inactive spans, declaration availability, diagnostics per variant, and differences.

**Implementation:** For symbol-only variants, clone parse options with explicit preprocessor symbols and reparse documents. For real target frameworks/configurations, load separate `MSBuildWorkspace` instances with global properties such as `TargetFramework` and `Configuration`; key caches by those properties. Cap matrix size aggressively and report failed variant loads separately.

### 29. `multi_target_diagnostics`

**User goal:** Compare compiler/analyzer diagnostics across all target frameworks of a multi-targeted project.

**Why it is valuable:** The existing diagnostics tool observes the workspace's active evaluation, not necessarily every target listed in `TargetFrameworks`.

**Suggested contract:** Inputs: project, target framework allow-list, configuration, severity, analyzer flag, and limits. Output: diagnostics grouped by TFM, common versus TFM-specific findings, load failures, and counts.

**Implementation:** Reuse the variant workspace loader proposed for `conditional_compilation_matrix`, passing evaluated global properties per TFM. Normalize diagnostics so cross-target comparisons are stable. Avoid parallel loads by default because MSBuildWorkspace and large compilations can consume substantial memory.

## Priority D: method-body reasoning and correctness

### 30. `data_flow`

**User goal:** Explain which variables are read, written, captured, escape a region, or are definitely assigned around a selected statement/expression range.

**Why it is valuable:** This supports safe extraction, movement, and simplification of code without relying on lexical variable matches.

**Suggested contract:** Inputs: document plus bounded start/end positions. Output: declared/read/written variables, data flows in/out, captured variables, always-assigned variables, unsafe-address flows, enclosing symbol, and analysis success/failure reason.

**Implementation:** Map the requested range to compatible syntax nodes and call `SemanticModel.AnalyzeDataFlow`. Return stable symbol descriptors rather than names alone. Roslyn only accepts certain node/range shapes, so normalize to statements/expressions and return a clear unsupported-region error rather than widening silently.

### 31. `control_flow`

**User goal:** Summarize branches, exits, loops, and reachability for a method or selected body.

**Why it is valuable:** Call hierarchy explains interactions between methods; control flow explains behavior within a method and helps diagnose missing cases or unreachable code.

**Suggested contract:** Inputs: method symbol or document position, optional block/edge limits. Output: basic blocks, branch conditions, successors, entry/exit reachability, return/throw/yield points, and source spans.

**Implementation:** Obtain the method/body `IOperation` and construct `Microsoft.CodeAnalysis.FlowAnalysis.ControlFlowGraph`. Compress linear block chains so output remains model-friendly; retain operation kinds and short excerpts rather than full operation trees.

### 32. `nullability_trace`

**User goal:** Explain why an expression is maybe-null/not-null and where a nullable warning originates.

**Why it is valuable:** A diagnostic alone often does not show the assignment, annotation, conversion, or branch state that caused the warning.

**Suggested contract:** Inputs: document position or nullable diagnostic location, optional backward-step bound. Output: annotated and flow-state types, conversions, relevant assignments/guards, containing symbol nullable context, and explanation evidence.

**Implementation:** Combine `SemanticModel.GetTypeInfo` nullability annotations/flow states, `IOperation`, control-flow blocks, assignments, pattern tests, and null checks. Keep explanations evidence-based and bounded; Roslyn does not expose every internal nullable-analysis decision as a public trace, so mark reconstructed paths conservative.

### 33. `exception_flow`

**User goal:** Identify explicit and documented exception sources, handlers, filters, and unhandled propagation paths for a method.

**Why it is valuable:** It improves error-boundary reviews and helps agents avoid swallowing failures or adding redundant broad catches.

**Suggested contract:** Inputs: method symbol, direction/depth, and limits. Output: explicit throw sites, rethrows, catch/finally/filter blocks, called methods with documented/explicit exceptions, and paths escaping the method.

**Implementation:** Analyze `IThrowOperation`, try/catch/finally operations, and bounded callees. C# metadata has no enforced checked-exception contract, so exceptions inferred from callees or XML `<exception>` documentation must be labeled candidates. Do not claim a complete runtime exception set.

### 34. `async_cancellation_flow`

**User goal:** Audit async call chains for missing cancellation propagation, dropped tasks, `async void`, blocking waits, and context-capture choices.

**Why it is valuable:** These are frequent correctness and responsiveness problems in service and UI code, and they span symbol binding plus call flow.

**Suggested contract:** Inputs: method/type/project scope and rule selection. Output: findings with caller/callee symbols, cancellation-token parameter mapping, operation kind, location, severity/confidence, and exemptions.

**Implementation:** Inspect method return types/async modifiers and bound invocations; identify `CancellationToken` parameters by symbol identity, omitted/default token arguments, `.Wait()`, `.Result`, `GetAwaiter().GetResult()`, unobserved task expressions, and `async void` outside event-handler shapes. Configuration and framework conventions should be explicit exemptions rather than hidden assumptions.

### 35. `resource_lifetime`

**User goal:** Find likely `IDisposable`/`IAsyncDisposable` ownership and lifetime mistakes.

**Why it is valuable:** Leaks and premature disposal often require semantic knowledge of construction, assignment, return, using scopes, and DI ownership.

**Suggested contract:** Inputs: symbol/project scope, resource type filter, and limits. Output: creation sites, using/await-using scopes, explicit disposal, escapes to fields/returns, DI-created instances, and candidate leaks/double-disposal risks.

**Implementation:** Use operations for object creation, invocation factories, using declarations/statements, dispose calls, assignments, and returns. Verify interface implementation through symbols. This should be a conservative ownership analysis; factory semantics, interprocedural aliases, and container disposal are not always provable.

## Priority E: service operability and context efficiency

### 36. `workspace_health`

**User goal:** Diagnose whether the cached Roslyn model is current and complete before trusting other answers.

**Why it is valuable:** Workspace load problems are currently repeated in each result but are difficult to diagnose operationally, especially after project, package, SDK, or generated-file changes.

**Suggested contract:** Inputs: workspace path and optional `includeProjectChecks`. Output: normalized path, snapshot fingerprint/time, invalidation state/reason, load duration, project/document counts, failed projects, SDK/MSBuild instance, global properties, load diagnostics, cache size, and memory-safe counters.

**Implementation:** Record load timing and invalidation events in `SolutionWorkspaceCache`; expose metadata without forcing compilation unless requested. Never return environment variables, credentials, private-feed tokens, or full command lines. Add an optional `forceReload` only if it is defined as cache invalidation, remains read-only with respect to repository files, and has a separate description explaining its cost.

### 37. `context_bundle`

**User goal:** Retrieve the smallest compiler-grounded context needed to understand one symbol before editing it.

**Why it is valuable:** Agents often call `symbol_info`, `find_references`, `implementation_map`, and a shallow call hierarchy in sequence. A bounded goal-specific bundle can reduce latency and repeated serialization.

**Suggested contract:** Inputs: symbol, project, `profile` (`understand`, `contract-change`, or `debug-flow`), and strict per-section limits. Output: the resolved symbol plus named bounded sections, individual truncation flags, and a recommended next exact tool call when more detail is needed.

**Implementation:** Compose existing analysis-service methods internally against one workspace snapshot rather than making nested MCP calls. Keep profiles few and explicit; do not expose a free-form “run any analyses” array. Preserve the focused primitive tools for follow-up and for cases requiring different limits.

### 38. `index_status`

**User goal:** Understand the cost and readiness of reusable semantic indexes used by audits and graph tools.

**Why it is valuable:** Whole-solution reference, call, attribute, and dependency indexes can be expensive. Agents need to know whether a query will be warm, partial, stale, or likely to exceed timeout limits.

**Suggested contract:** Inputs: workspace path and optional index kind. Output: snapshot fingerprint, available indexes, build state, indexed project/document/symbol counts, creation time/duration, truncation policy, and last failure.

**Implementation:** Introduce a snapshot-scoped index registry with `Lazy<Task<T>>` per index kind, cancellation-safe creation, bounded memory accounting, and invalidation with the workspace snapshot. The tool reports state only; it should not start every index unless explicitly requested through a narrowly named warm-up option.

## Cross-cutting implementation recommendations

1. **Introduce structured MCP output before expanding heavily.** The current tool layer serializes anonymous objects to JSON text. Add explicit result records and MCP output schemas so Codex can reuse fields reliably while retaining a short human-readable `content` summary.
2. **Add a snapshot fingerprint.** `WorkspaceLoadedAt` is useful but not a stable identity. Hash normalized workspace path, project/version stamps, parse/compilation options, and relevant file-change generation so baseline and delta tools can reject mismatched snapshots.
3. **Build reusable semantic indexes.** Reference, caller, attribute, namespace-dependency, and test indexes should be snapshot-scoped and shared. Avoid one whole-solution `SymbolFinder` query per candidate.
4. **Separate exact evidence from heuristics.** Use a field, not prose alone, to distinguish compiler-bound results from configuration parsing, name matching, or convention inference.
5. **Keep framework profiles modular.** MVC, Minimal API, Microsoft DI, System.Text.Json, Newtonsoft.Json, test frameworks, and other conventions should be separate analyzers registered behind narrow interfaces. Do not grow one global list of attribute-name fragments.
6. **Return local evidence, not source dumps.** Keep excerpts short, cap locations per symbol, and report aggregate counts even when details are truncated.
7. **Preserve backward compatibility.** Add new optional fields and new tools; do not rename existing tools or reinterpret existing classifications without a versioned migration.
8. **Test metadata as well as code.** Maintain a golden prompt set containing direct calls, indirect user goals, negative prompts that should use existing tools or shell/build commands, invalid paths, ambiguous symbols, large-result truncation, cancellation, and incomplete-workspace cases.
9. **Test with real framework fixtures.** Add compact solutions for MVC routing, Minimal APIs, DI, serializers, source generators, multi-targeting, events/delegates, and nullable flow. Assertions should verify stable IDs, evidence classifications, limits, and exclusions.
10. **Measure before enabling all tools.** Record latency, allocations, output size, selection precision, and recall. Use Codex `enabled_tools` during trials so experimental tools do not make the production catalog harder to select from.

## Tools intentionally not recommended yet

- **Automatic source rewriting or code-fix application:** keep the server read-only until previews have strong conflict detection, tests, and a separate confirmation model.
- **A general “run arbitrary Roslyn query” tool:** it would require accepting code or a query language, enlarge the security surface, and provide weak metadata for tool selection.
- **Unbounded syntax-tree, operation-tree, or generated-source dumps:** they recreate the context-flooding problem the server is meant to solve.
- **A tool that runs builds or tests:** repository-specific commands, credentials, timeouts, and side effects belong to Codex's normal execution workflow; Roslyn diagnostics should remain a complementary static-analysis gate.
- **Runtime certainty from DI, reflection, routing, serialization, or configuration heuristics:** these domains need explicit confidence and limitation fields, plus authoritative runtime tests where correctness matters.

## Practical milestone plan

- **Milestone 1 — navigation:** implement `symbol_at_position`, `invocation_binding`, `member_surface`, and `inheritance_graph` with explicit result records and structured output.
- **Milestone 2 — safe editing:** add `rename_preview`, `diagnostics_delta`, `test_impact`, and `semantic_diff`, backed by a snapshot fingerprint and reusable indexes.
- **Milestone 3 — hidden code:** add `source_generator_inventory`, `conditional_compilation_matrix`, `multi_target_diagnostics`, and `analyzer_configuration`.
- **Milestone 4 — framework adapters:** add DI, endpoint, configuration, serialization, and reflection tools only for frameworks represented by real fixtures.
- **Milestone 5 — deeper reasoning:** add operation/flow tools and architecture audits after latency and context-size budgets are established.

After each milestone, rebuild the Release server, run its unit/integration tests, inspect every tool through MCP Inspector or a direct protocol harness, restart Codex to refresh the tool catalog, and replay the golden prompts before enabling the new tools broadly.
