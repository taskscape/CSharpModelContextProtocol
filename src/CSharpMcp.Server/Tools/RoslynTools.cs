using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpMcp.Analysis;
using CSharpMcp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CSharpMcp.Tools;

/// <summary>
/// Exposes bounded, read-only Roslyn code-intelligence operations through MCP.
/// </summary>
[McpServerToolType]
internal sealed class RoslynTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly RoslynAnalysisService analysisService;
    private readonly SolutionTrustStore trustStore;
    private readonly ToolCatalogProfile toolCatalogProfile;

    /// <summary>
    /// Initializes the MCP tool facade.
    /// </summary>
    public RoslynTools(
        RoslynAnalysisService analysisService,
        SolutionTrustStore trustStore,
        ToolCatalogProfile toolCatalogProfile)
    {
        this.analysisService = analysisService;
        this.trustStore = trustStore;
        this.toolCatalogProfile = toolCatalogProfile;
    }

    /// <summary>
    /// Lists projects, target frameworks, references, documents, analyzers, and entry points.
    /// </summary>
    [McpServerTool(Name = "solution_overview", Title = "Solution overview", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ValueArrayDataSchema>))]
    [Description("Load a .sln, .slnx, .slnf, or .csproj with MSBuildWorkspace and summarize its C# project graph, target frameworks, and entry points.")]
    public async Task<McpToolResponse> SolutionOverviewAsync(
        [Description("Absolute path to a .sln, .slnx, .slnf, or .csproj file.")] string workspacePath,
        [Description("MSBuild Configuration used to evaluate the workspace.")] string configuration = "Debug",
        [Description("Optional target framework used for an explicit multi-target project evaluation.")] string? targetFramework = null,
        [Description("Maximum projects to return, from 1 to 1000.")] int maxProjects = 50,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithProgressAsync(
            () => analysisService.GetSolutionOverviewAsync(
                workspacePath,
                configuration,
                targetFramework,
                maxProjects,
                cancellationToken),
            progress,
            "Loading the MSBuild workspace and summarizing its project graph.").ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a symbol and reports exact declarations and semantic metadata.
    /// </summary>
    [McpServerTool(Name = "symbol_info", Title = "Symbol information", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ValueArrayDataSchema>))]
    [Description("Resolve a C# symbol by documentation ID, metadata name, or qualified name and return declarations, type relationships, modifiers, and documentation.")]
    public async Task<McpToolResponse> SymbolInfoAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Documentation ID such as T:Namespace.Type, metadata name, or qualified symbol name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("Maximum matching declarations to return, from 1 to 1000.")] int maxResults = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetSymbolInfoAsync(workspacePath, symbol, projectName, maxResults, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Finds exact compiler-bound source references to one symbol.
    /// </summary>
    [McpServerTool(Name = "find_references", Title = "Find symbol references", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ReferenceDataSchema>))]
    [Description("Find exact Roslyn-bound source references to one unambiguous symbol and classify them as calls, reads, writes, constructions, or type uses.")]
    public async Task<McpToolResponse> FindReferencesAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable documentation ID, metadata name, or qualified symbol name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("Optional comma-separated server-side kinds filter such as invocation, write, readwrite, attribute, nameof, typeof, dependency_injection_registration, or test_reference.")] string? referenceKinds = null,
        [Description("Include source declarations as kind declaration.")] bool includeDeclarations = false,
        [Description("Maximum references to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.FindReferencesAsync(workspacePath, symbol, projectName, referenceKinds, includeDeclarations, maxResults, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Builds a bounded caller and/or callee graph for a method-like symbol.
    /// </summary>
    [McpServerTool(Name = "call_hierarchy", Title = "Call hierarchy", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<CallHierarchyDataSchema>))]
    [Description("Return a depth-limited call graph for a method, constructor, accessor, or local function. Dynamic and reflection calls are outside Roslyn's model.")]
    public async Task<McpToolResponse> CallHierarchyAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable documentation ID or qualified method name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("callers, callees, or both.")] string direction = "both",
        [Description("Recursive depth from 1 to 5.")] int maxDepth = 2,
        [Description("Maximum graph edges to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetCallHierarchyAsync(
            workspacePath,
            symbol,
            projectName,
            direction,
            maxDepth,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Maps an interface or abstract contract to source implementations and overrides.
    /// </summary>
    [McpServerTool(Name = "implementation_map", Title = "Implementation map", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ImplementationDataSchema>))]
    [Description("Map an interface, abstract type, virtual member, or contract member to concrete source implementations and overrides.")]
    public async Task<McpToolResponse> ImplementationMapAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable documentation ID, metadata name, or qualified symbol name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("Maximum implementations to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetImplementationMapAsync(workspacePath, symbol, projectName, maxResults, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Classifies construction, DI-registration candidates, public API signatures, and other type uses.
    /// </summary>
    [McpServerTool(Name = "type_usage", Title = "Type usage", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<TypeUsageDataSchema>))]
    [Description("Find uses of a named type and classify construction sites, DI-registration candidates, API signatures, and other type references.")]
    public async Task<McpToolResponse> TypeUsageAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable type documentation ID or metadata name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("Maximum usages to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetTypeUsageAsync(workspacePath, symbol, projectName, maxResults, cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Runs compiler and optional analyzer diagnostics for selected projects.
    /// </summary>
    [McpServerTool(Name = "diagnostics", Title = "Compiler diagnostics", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<DiagnosticsDataSchema>))]
    [Description("Run Roslyn compiler diagnostics and, by default, configured project analyzers. This is a static-analysis gate and does not replace builds or tests.")]
    public async Task<McpToolResponse> DiagnosticsAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Optional exact project name; omit to inspect all C# projects.")] string? projectName = null,
        [Description("hidden, info, warning, or error.")] string minimumSeverity = "warning",
        [Description("Run analyzers referenced by each project in addition to compiler diagnostics.")] bool includeAnalyzers = true,
        [Description("Optional absolute document path used to restrict diagnostics.")] string? documentPath = null,
        [Description("Optional comma-separated diagnostic IDs such as CS8602, CA2000, or IDE0055.")] string? diagnosticIds = null,
        [Description("Include diagnostics suppressed by configuration or attributes.")] bool includeSuppressed = false,
        [Description("Maximum diagnostics to return, from 1 to 1000.")] int maxResults = 50,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithProgressAsync(
            () => analysisService.GetDiagnosticsAsync(
                workspacePath,
                projectName,
                minimumSeverity,
                includeAnalyzers,
                documentPath,
                diagnosticIds,
                includeSuppressed,
                maxResults,
                cancellationToken),
            progress,
            includeAnalyzers
                ? "Compiling selected projects and running trusted analyzers."
                : "Compiling selected projects and collecting compiler diagnostics.").ConfigureAwait(false);
    }

    /// <summary>
    /// Reports project and assembly dependencies.
    /// </summary>
    [McpServerTool(Name = "project_dependencies", Title = "Project dependencies", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<DependenciesDataSchema>))]
    [Description("Return project-to-project edges and bounded assembly reference names for architecture and layering analysis.")]
    public async Task<McpToolResponse> ProjectDependenciesAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Optional exact project name; omit for all C# projects.")] string? projectName = null,
        [Description("Include bounded compiler-resolved namespace dependency edges with counts and examples.")] bool includeNamespaceEdges = false,
        [Description("Maximum projects and namespace edges to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetProjectDependenciesAsync(
                workspacePath,
                projectName,
                includeNamespaceEdges,
                maxResults,
                cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Searches source symbols by semantic identity rather than raw file text.
    /// </summary>
    [McpServerTool(Name = "semantic_search", Title = "Semantic symbol search", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<SearchDataSchema>))]
    [Description("Search source symbol names by concept tokens and rank matches using qualified identities and documentation. Returns stable IDs for later exact queries.")]
    public async Task<McpToolResponse> SemanticSearchAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Concept or symbol-name tokens to search.")] string query,
        [Description("Optional exact project name.")] string? projectName = null,
        [Description("Optional comma-separated Roslyn SymbolKind values such as NamedType, Method, Property.")] string? symbolKinds = null,
        [Description("Maximum symbols to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.SemanticSearchAsync(
            workspacePath,
            query,
            projectName,
            symbolKinds,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Audits source types and members for no source references or references only from test projects.
    /// </summary>
    [McpServerTool(Name = "unused_symbol_audit", Title = "Unused symbol audit", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<UnusedAuditDataSchema>))]
    [Description("Enumerate source types, methods, properties, fields, and events; exclude known generated, contract, framework, and runtime entry points; and report candidates with no source references or only test-project references.")]
    public async Task<McpToolResponse> UnusedSymbolAuditAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Optional exact production project name; omit to audit every non-test C# project.")] string? projectName = null,
        [Description("Include symbols declared in projects classified as test projects among removal candidates.")] bool includeTestProjectsAsCandidates = false,
        [Description("Optional comma-separated kinds: NamedType, Method, Property, Field, Event.")] string? symbolKinds = null,
        [Description("Maximum non-excluded symbols to analyze, from 1 to 20000.")] int maxSymbols = 5000,
        [Description("Maximum removal candidates to return, from 1 to 1000.")] int maxResults = 50,
        [Description("Maximum reference examples retained per symbol, from 0 to 50.")] int maxReferencesPerSymbol = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.AuditUnusedSymbolsAsync(
            workspacePath,
            projectName,
            includeTestProjectsAsCandidates,
            symbolKinds,
            maxSymbols,
            maxResults,
            maxReferencesPerSymbol,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Computes a conservative compile-time impact set before a contract edit.
    /// </summary>
    [McpServerTool(Name = "affected_symbols", Title = "Affected symbols", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ImpactDataSchema>))]
    [Description("Plan a signature, namespace, or type edit by collecting source symbols that reference, implement, override, or contain the target. Runtime-only coupling is not inferred.")]
    public async Task<McpToolResponse> AffectedSymbolsAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable documentation ID, metadata name, or qualified symbol name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("Maximum changed-contract descriptors, from 1 to 1000.")] int maxContracts = 10,
        [Description("Maximum implementation and override descriptors, from 1 to 1000.")] int maxImplementations = 20,
        [Description("Maximum production reference occurrences, from 1 to 1000.")] int maxReferences = 25,
        [Description("Maximum distinct caller symbols, from 1 to 1000.")] int maxCallers = 20,
        [Description("Maximum test reference occurrences, from 1 to 1000.")] int maxTests = 25,
        [Description("Maximum transitively dependent projects, from 1 to 1000.")] int maxDependentProjects = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetAffectedSymbolsAsync(
                workspacePath,
                symbol,
                projectName,
                maxContracts,
                maxImplementations,
                maxReferences,
                maxCallers,
                maxTests,
                maxDependentProjects,
                cancellationToken)
            .ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Resolves compiler-bound symbol and type information at a source coordinate.
    /// </summary>
    [McpServerTool(Name = "symbol_at_position", Title = "Symbol at source position", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<PositionDataSchema>))]
    [Description("Resolve the exact bound, declared, enclosing, and candidate symbols at a one-based document line and column.")]
    public async Task<McpToolResponse> SymbolAtPositionAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Absolute path to a C# document in the workspace.")] string documentPath,
        [Description("One-based source line.")] int line,
        [Description("One-based source column.")] int column,
        [Description("Include overload or error-recovery candidate symbols.")] bool includeCandidates = true,
        [Description("Maximum candidate symbols to return, from 1 to 1000.")] int maxCandidates = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetSymbolAtPositionAsync(
            workspacePath,
            documentPath,
            line,
            column,
            includeCandidates,
            maxCandidates,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Explains compiler overload resolution and argument-to-parameter binding.
    /// </summary>
    [McpServerTool(Name = "invocation_binding", Title = "Invocation binding", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<InvocationDataSchema>))]
    [Description("Explain the callable selected by Roslyn at a source position, including receiver, generic arguments, conversions, and argument-to-parameter mapping.")]
    public async Task<McpToolResponse> InvocationBindingAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Absolute path to a C# document in the workspace.")] string documentPath,
        [Description("One-based source line inside the invocation.")] int line,
        [Description("One-based source column inside the invocation.")] int column,
        [Description("Maximum failed-binding candidates to return, from 1 to 1000.")] int maxCandidates = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetInvocationBindingAsync(
            workspacePath,
            documentPath,
            line,
            column,
            maxCandidates,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Enumerates a type's declared and optional inherited API surface.
    /// </summary>
    [McpServerTool(Name = "member_surface", Title = "Member surface", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<MemberSurfaceDataSchema>))]
    [Description("Enumerate a named type's bounded member surface with accessibility, inheritance, override, and interface-contract relationships.")]
    public async Task<McpToolResponse> MemberSurfaceAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable type documentation ID, metadata name, or qualified name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("Optional comma-separated Roslyn SymbolKind values such as Method, Property, Event.")] string? memberKinds = null,
        [Description("all, public, public-or-protected, or non-private.")] string accessibility = "all",
        [Description("Include members declared by base types and interfaces.")] bool includeInherited = false,
        [Description("Include explicit interface implementations.")] bool includeExplicitInterfaceImplementations = true,
        [Description("Optional exact member name, useful for overload-only queries.")] string? memberName = null,
        [Description("Include source and referenced-assembly extension methods that Roslyn can reduce against this type.")] bool includeApplicableExtensionMethods = false,
        [Description("all, constructors, overloads, operators, or extensions. Overloads requires memberName.")] string mode = "all",
        [Description("Maximum members to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetMemberSurfaceAsync(
            workspacePath,
            symbol,
            projectName,
            memberKinds,
            accessibility,
            includeInherited,
            includeExplicitInterfaceImplementations,
            memberName,
            includeApplicableExtensionMethods,
            mode,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Traverses bounded base, derived, and interface relationships.
    /// </summary>
    [McpServerTool(Name = "inheritance_graph", Title = "Inheritance graph", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<InheritanceDataSchema>))]
    [Description("Build a depth-limited graph of base types, derived types, interfaces, and implementations for one named type.")]
    public async Task<McpToolResponse> InheritanceGraphAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable type documentation ID, metadata name, or qualified name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("ancestors, descendants, or both.")] string direction = "both",
        [Description("Recursive graph depth from 1 to 5.")] int maxDepth = 2,
        [Description("Include interface inheritance and implementation edges.")] bool includeInterfaces = true,
        [Description("Maximum graph edges to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetInheritanceGraphAsync(
            workspacePath,
            symbol,
            projectName,
            direction,
            maxDepth,
            includeInterfaces,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Previews a semantic rename or signature change without writing source files.
    /// </summary>
    [McpServerTool(Name = "rename_preview", Title = "Rename preview", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<RefactorPreviewDataSchema>))]
    [Description("Preview a bounded Roslyn rename or compiler-bound method signature change in an immutable solution, with freshness validation and structured edits; never writes files.")]
    public async Task<McpToolResponse> RenamePreviewAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Stable documentation ID, metadata name, or qualified symbol name.")] string symbol,
        [Description("rename or signature.")] string refactorKind = "rename",
        [Description("Valid replacement C# identifier when refactorKind is rename.")] string? newName = null,
        [Description("New C# parameter list such as '(int id, string name)' when refactorKind is signature.")] string? newSignature = null,
        [Description("Optional exact project name used to disambiguate the query.")] string? projectName = null,
        [Description("Also rename exact identifier words inside string literals.")] bool renameInStrings = false,
        [Description("Also rename exact identifier words inside comments and documentation trivia.")] bool renameInComments = false,
        [Description("Rename all overloads through Roslyn's Renamer when refactorKind is rename.")] bool renameOverloads = false,
        [Description("Rename a matching type document through Roslyn's Renamer when refactorKind is rename.")] bool renameFile = false,
        [Description("Optional fingerprint from a previous preview; a mismatch rejects stale analysis.")] string? expectedFingerprint = null,
        [Description("Optional opaque cursor returned by the previous page of this same preview.")] string? cursor = null,
        [Description("Maximum individual text changes to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetRenamePreviewAsync(
            workspacePath,
            symbol,
            refactorKind,
            newName,
            newSignature,
            projectName,
            renameInStrings,
            renameInComments,
            renameOverloads,
            renameFile,
            expectedFingerprint,
            cursor,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Captures and compares diagnostic baselines across separate MCP calls.
    /// </summary>
    [McpServerTool(Name = "diagnostics_delta", Title = "Diagnostics delta", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<DiagnosticsDeltaDataSchema>))]
    [Description("Capture a short-lived diagnostic baseline or compare the current compiler/analyzer results with a prior token to report introduced and resolved diagnostics.")]
    public async Task<McpToolResponse> DiagnosticsDeltaAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Baseline token returned by an earlier call; omit to capture a new baseline.")] string? baselineToken = null,
        [Description("Optional exact project name; omit to inspect all C# projects.")] string? projectName = null,
        [Description("hidden, info, warning, or error. Must match the baseline call.")] string minimumSeverity = "warning",
        [Description("Run configured analyzers in addition to compiler diagnostics. Must match the baseline call.")] bool includeAnalyzers = true,
        [Description("Maximum introduced and resolved diagnostics to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetDiagnosticsDeltaAsync(
            workspacePath,
            baselineToken,
            projectName,
            minimumSeverity,
            includeAnalyzers,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Finds statically connected tests for changed symbols or documents.
    /// </summary>
    [McpServerTool(Name = "test_impact", Title = "Test impact", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<TestImpactDataSchema>))]
    [Description("Trace compiler-bound reverse references from changed symbols or documents to test-project methods and return evidence paths for focused test selection.")]
    public async Task<McpToolResponse> TestImpactAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Optional stable symbol IDs, metadata names, or qualified names to treat as changed seeds.")] string[]? symbols = null,
        [Description("Optional absolute C# document paths whose declared symbols are changed seeds.")] string[]? documentPaths = null,
        [Description("Optional exact project name used to disambiguate symbol queries.")] string? projectName = null,
        [Description("Maximum reverse-reference depth from 1 to 5.")] int maxDepth = 3,
        [Description("Maximum impacted test methods to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await analysisService.GetTestImpactAsync(
            workspacePath,
            symbols,
            documentPaths,
            projectName,
            maxDepth,
            maxResults,
            cancellationToken).ConfigureAwait(false);
        return Serialize(result);
    }

    /// <summary>
    /// Inventories generated documents and their declared symbols.
    /// </summary>
    [McpServerTool(Name = "source_generator_inventory", Title = "Source generator inventory", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<GeneratorInventoryDataSchema>))]
    [Description("List source-generator candidates and bounded generated documents, declarations, diagnostics, and optional short excerpts for selected projects.")]
    public async Task<McpToolResponse> SourceGeneratorInventoryAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Optional exact project name; omit to inspect all C# projects.")] string? projectName = null,
        [Description("Optional case-insensitive generator type-name filter.")] string? generatorName = null,
        [Description("Optional generated document ID or exact hint name; use the inventory result to select one generated source without adding a separate tool.")] string? generatedDocumentId = null,
        [Description("Include at most eight lines and 800 characters from each generated document.")] bool includeGeneratedSourceExcerpt = false,
        [Description("Optional opaque cursor returned by the previous page of this same inventory query.")] string? cursor = null,
        [Description("Maximum generated documents to return, from 1 to 1000.")] int maxResults = 50,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithProgressAsync(
            () => analysisService.GetSourceGeneratorInventoryAsync(
                workspacePath,
                projectName,
                generatorName,
                generatedDocumentId,
                includeGeneratedSourceExcerpt,
                cursor,
                maxResults,
                cancellationToken),
            progress,
            "Running trusted source generators and inventorying bounded output.").ConfigureAwait(false);
    }

    /// <summary>
    /// Compares explicit C# preprocessor-symbol variants.
    /// </summary>
    [McpServerTool(Name = "conditional_compilation_matrix", Title = "Conditional compilation matrix", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ConditionalMatrixDataSchema>))]
    [Description("Reparse and recompile one project under 1-16 explicit preprocessor-symbol sets, reporting declaration availability, inactive spans, and diagnostics per variant.")]
    public async Task<McpToolResponse> ConditionalCompilationMatrixAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("Exact C# project name.")] string projectName,
        [Description("One to sixteen comma-, semicolon-, or space-delimited preprocessor-symbol sets; use an empty string for no symbols.")] string[] symbolSets,
        [Description("Optional MSBuild Configuration values; defaults to Debug. Combined matrix is limited to 32 variants.")] string[]? configurations = null,
        [Description("Optional target frameworks for separate MSBuild evaluations; omit for the project default.")] string[]? targetFrameworks = null,
        [Description("Optional absolute document path used to restrict declaration and inactive-region output.")] string? documentPath = null,
        [Description("Maximum diagnostics, declarations, and inactive regions per section, from 1 to 1000.")] int maxResults = 50,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithProgressAsync(
            () => analysisService.GetConditionalCompilationMatrixAsync(
                workspacePath,
                projectName,
                symbolSets,
                configurations,
                targetFrameworks,
                documentPath,
                maxResults,
                cancellationToken),
            progress,
            $"Compiling {symbolSets.Length} preprocessor-symbol variant(s).").ConfigureAwait(false);
    }

    /// <summary>
    /// Reports cache freshness, load diagnostics, MSBuild identity, and optional compilation checks.
    /// </summary>
    [McpServerTool(Name = "workspace_health", Title = "Workspace health", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<WorkspaceHealthDataSchema>))]
    [Description("Diagnose cached workspace freshness and completeness, with reload timing, invalidation history, MSBuild identity, load diagnostics, and optional bounded project compilations.")]
    public async Task<McpToolResponse> WorkspaceHealthAsync(
        [Description("Absolute workspace path.")] string workspacePath,
        [Description("MSBuild Configuration used to evaluate the workspace.")] string configuration = "Debug",
        [Description("Optional target framework used for an explicit multi-target project evaluation.")] string? targetFramework = null,
        [Description("Compile selected projects to verify that semantic models are available.")] bool includeProjectChecks = false,
        [Description("Maximum project checks to return, from 1 to 1000.")] int maxProjects = 50,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithProgressAsync(
            () => analysisService.GetWorkspaceHealthAsync(
                workspacePath,
                configuration,
                targetFramework,
                includeProjectChecks,
                maxProjects,
                cancellationToken),
            progress,
            includeProjectChecks
                ? "Checking workspace freshness and compiling selected projects."
                : "Checking workspace freshness and load diagnostics.").ConfigureAwait(false);
    }

    /// <summary>
    /// Returns bounded original source for one or more resolved declarations.
    /// </summary>
    [McpServerTool(Name = "symbol_source", Title = "Symbol source", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<SymbolSourceDataSchema>))]
    [Description("Return original formatted declaration source for 1-50 symbols with per-item ok, notFound, ambiguous, metadata, or unsupportedKind status and strict line/character limits.")]
    public async Task<McpToolResponse> SymbolSourceAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Stable IDs, metadata names, or qualified symbol names to retrieve.")] string[] symbolQueries,
        [Description("Optional exact project name used to disambiguate queries.")] string? projectName = null,
        [Description("Include member/type bodies; false returns signature-oriented source only.")] bool includeBody = false,
        [Description("Maximum lines per declaration, from 1 to 500.")] int maxLines = 80,
        [Description("Maximum characters per declaration, from 200 to 50000.")] int maxCharacters = 8000,
        CancellationToken cancellationToken = default)
    {
        return Serialize(await analysisService.GetSymbolSourceAsync(
            workspacePath,
            symbolQueries,
            projectName,
            includeBody,
            maxLines,
            maxCharacters,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Finds event subscriptions, unsubscriptions, and raises.
    /// </summary>
    [McpServerTool(Name = "event_flow", Title = "Event flow", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<EventFlowDataSchema>))]
    [Description("Find compiler-bound event subscribe, unsubscribe, raise, and other reference sites, resolving method-group and lambda handlers where Roslyn can do so.")]
    public async Task<McpToolResponse> EventFlowAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Stable event documentation ID or qualified name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the event.")] string? projectName = null,
        [Description("Optional comma-separated subscribe, unsubscribe, raise, or reference filters.")] string? actions = null,
        [Description("Maximum sites to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        return Serialize(await analysisService.GetEventFlowAsync(
            workspacePath,
            symbol,
            projectName,
            actions,
            maxResults,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Finds attribute-decorated symbols and argument values.
    /// </summary>
    [McpServerTool(Name = "attribute_usage", Title = "Attribute usage", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<AttributeUsageDataSchema>))]
    [Description("Find symbols decorated with an exact attribute type and return target identity, source location, constructor arguments, and named arguments.")]
    public async Task<McpToolResponse> AttributeUsageAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Stable attribute type ID, metadata name, or qualified name.")] string attribute,
        [Description("Optional exact project name used to disambiguate the attribute type.")] string? projectName = null,
        [Description("Optional comma-separated Roslyn SymbolKind target filters.")] string? targetKinds = null,
        [Description("Include derived types and overriding members that inherit the attribute under AttributeUsage rules.")] bool includeInherited = false,
        [Description("For ObsoleteAttribute, group targets by migration message and error severity.")] bool includeMigrationGroups = false,
        [Description("Maximum usages to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        return Serialize(await analysisService.GetAttributeUsageAsync(
            workspacePath,
            attribute,
            projectName,
            targetKinds,
            includeInherited,
            includeMigrationGroups,
            maxResults,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Maps compiler-bound Microsoft DI and convention-shaped service registrations.
    /// </summary>
    [McpServerTool(Name = "dependency_injection_map", Title = "Dependency injection map", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<DependencyInjectionDataSchema>))]
    [Description("Find generic, typeof-pair, and factory-lambda DI registrations with service, implementation, lifetime, shape, and explicit framework-versus-convention confidence.")]
    public async Task<McpToolResponse> DependencyInjectionMapAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Optional exact project name; omit to scan all C# projects.")] string? projectName = null,
        [Description("Optional stable service type ID or metadata name.")] string? serviceSymbol = null,
        [Description("Optional comma-separated singleton, scoped, transient, or unknown filters.")] string? lifetimes = null,
        [Description("Maximum registrations to return, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        return Serialize(await analysisService.GetDependencyInjectionMapAsync(
            workspacePath,
            projectName,
            serviceSymbol,
            lifetimes,
            maxResults,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Explains how a project can construct or resolve one named type.
    /// </summary>
    [McpServerTool(Name = "construction_options", Title = "Construction options", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ConstructionDataSchema>))]
    [Description("Return constructors, static factories, required members, DI registrations, and compiler accessibility from a selected project, including InternalsVisibleTo effects.")]
    public async Task<McpToolResponse> ConstructionOptionsAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Stable type ID, metadata name, or qualified name.")] string symbol,
        [Description("Optional exact declaring project name used to disambiguate the type.")] string? projectName = null,
        [Description("Optional project from which constructor/factory accessibility is evaluated.")] string? fromProject = null,
        [Description("Maximum factory and registration results, from 1 to 1000.")] int maxResults = 50,
        CancellationToken cancellationToken = default)
    {
        return Serialize(await analysisService.GetConstructionOptionsAsync(
            workspacePath,
            symbol,
            projectName,
            fromProject,
            maxResults,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Enumerates or compares production public API contracts.
    /// </summary>
    [McpServerTool(Name = "api_compatibility", Title = "API compatibility", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ApiCompatibilityDataSchema>))]
    [Description("Return a deterministic public/protected API surface and optionally compare it with a JSON or DLL baseline, reporting removed and reduced-accessibility contracts as breaking changes.")]
    public async Task<McpToolResponse> ApiCompatibilityAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Optional exact production project name; omit for all non-test C# projects.")] string? projectName = null,
        [Description("Optional absolute .json or .dll baseline path.")] string? baselinePath = null,
        [Description("Include the current bounded API list as well as comparison counts.")] bool includeCurrentSurface = false,
        [Description("Optional opaque cursor returned by the previous page of this same API query.")] string? cursor = null,
        [Description("Maximum API items and changes to return, from 1 to 1000.")] int maxResults = 50,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        toolCatalogProfile.EnsureEnabled("api_compatibility");
        return await ExecuteWithProgressAsync(
            () => analysisService.GetApiCompatibilityAsync(
                workspacePath,
                projectName,
                baselinePath,
                includeCurrentSurface,
                cursor,
                maxResults,
                cancellationToken),
            progress,
            "Enumerating and comparing the public API surface.").ConfigureAwait(false);
    }

    /// <summary>
    /// Runs Roslyn data-flow and control-flow analysis over a statement region.
    /// </summary>
    [McpServerTool(Name = "region_flow", Title = "Region flow analysis", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<RegionFlowDataSchema>))]
    [Description("Analyze variable flow and/or branch reachability for a contiguous statement range using Roslyn's authoritative DataFlowAnalysis and ControlFlowAnalysis APIs.")]
    public async Task<McpToolResponse> RegionFlowAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Absolute C# document path.")] string documentPath,
        [Description("One-based start line.")] int startLine,
        [Description("One-based start column.")] int startColumn,
        [Description("One-based end line.")] int endLine,
        [Description("One-based end column.")] int endColumn,
        [Description("data, control, or both.")] string kind = "both",
        CancellationToken cancellationToken = default)
    {
        return Serialize(await analysisService.GetRegionFlowAsync(
            workspacePath,
            documentPath,
            startLine,
            startColumn,
            endLine,
            endColumn,
            kind,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Checks semantic namespace dependencies against caller-supplied layering rules.
    /// </summary>
    [McpServerTool(Name = "architecture_rule_check", Title = "Architecture rule check", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ArchitectureDataSchema>))]
    [Description("Enforce 1-50 namespace layering rules against compiler-resolved source type references. Each rule supplies a source namespace and forbid and/or allowOnly namespace prefixes.")]
    public async Task<McpToolResponse> ArchitectureRuleCheckAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Rules with Name, FromNamespace, optional Forbid prefixes, and optional AllowOnly prefixes.")] ArchitectureRuleInput[] rules,
        [Description("Optional exact project name; omit to check all C# projects.")] string? projectName = null,
        [Description("Maximum grouped boundary violations to return, from 1 to 1000.")] int maxResults = 50,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        toolCatalogProfile.EnsureEnabled("architecture_rule_check");
        return await ExecuteWithProgressAsync(
            () => analysisService.CheckArchitectureRulesAsync(
                workspacePath,
                rules,
                projectName,
                maxResults,
                cancellationToken),
            progress,
            $"Checking {rules.Length} semantic architecture rule(s).").ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves pasted .NET stack frames to source symbols.
    /// </summary>
    [McpServerTool(Name = "resolve_stack_trace", Title = "Resolve stack trace", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<StackTraceDataSchema>))]
    [Description("Map a pasted .NET stack trace to loaded source symbols, normalizing async state machines, lambdas, local functions, nested types, and generic arity where possible.")]
    public async Task<McpToolResponse> ResolveStackTraceAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Raw .NET stack trace, including optional inner-exception or log-prefixed lines.")] string stackTrace,
        [Description("Maximum parsed frames to return, from 1 to 200.")] int maxFrames = 50,
        CancellationToken cancellationToken = default)
    {
        return Serialize(await analysisService.ResolveStackTraceAsync(
            workspacePath,
            stackTrace,
            maxFrames,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Returns one bounded, goal-specific semantic context package.
    /// </summary>
    [McpServerTool(Name = "context_bundle", Title = "Semantic context bundle", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ContextBundleDataSchema>))]
    [Description("Return a strictly bounded understand, contract-change, or debug-flow context package for one symbol, replacing multiple overlapping type/method/file overview tools.")]
    public async Task<McpToolResponse> ContextBundleAsync(
        [Description("Absolute trusted workspace path.")] string workspacePath,
        [Description("Stable symbol ID, metadata name, or qualified name.")] string symbol,
        [Description("Optional exact project name used to disambiguate the symbol.")] string? projectName = null,
        [Description("understand, contract-change, or debug-flow.")] string profile = "understand",
        [Description("Maximum results in each section, from 1 to 50.")] int maxResultsPerSection = 20,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteWithProgressAsync(
            () => analysisService.GetContextBundleAsync(
                workspacePath,
                symbol,
                projectName,
                profile,
                maxResultsPerSection,
                cancellationToken),
            progress,
            $"Building the bounded {profile} semantic context bundle.").ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly trusts one repository root for MSBuild evaluation and repository-supplied analysis components.
    /// </summary>
    [McpServerTool(Name = "trust_solution", Title = "Trust solution", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<TrustDataSchema>))]
    [Description("Authorize a solution, project, or repository directory for MSBuild evaluation and execution of its configured analyzers and source generators. This changes only the CSharpMCP trust store, never repository files.")]
    public McpToolResponse TrustSolution(
        [Description("Absolute .sln, .slnx, .slnf, .csproj, or repository-directory path to trust.")] string workspacePath,
        [Description("Persist trust for future server processes; false grants trust only for this server session.")] bool persist = false)
    {
        return Serialize(trustStore.Trust(workspacePath, persist));
    }

    /// <summary>
    /// Lists session and persisted solution trust roots.
    /// </summary>
    [McpServerTool(Name = "list_trusted_paths", Title = "List trusted paths", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<ValueArrayDataSchema>))]
    [Description("List normalized repository roots currently trusted by CSharpMCP and whether each decision is session-only or persisted.")]
    public McpToolResponse ListTrustedPaths()
    {
        return Serialize(trustStore.List());
    }

    /// <summary>
    /// Revokes session and persisted trust for one normalized repository root.
    /// </summary>
    [McpServerTool(Name = "revoke_trust", Title = "Revoke solution trust", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(ToolOutputSchema<TrustRevocationDataSchema>))]
    [Description("Revoke CSharpMCP session and persisted trust for a solution, project, or repository directory. This does not modify repository files.")]
    public McpToolResponse RevokeTrust(
        [Description("Absolute trusted solution, project, or repository-directory path to revoke.")] string workspacePath)
    {
        return Serialize(trustStore.Revoke(workspacePath));
    }

    private static McpToolResponse Serialize(object value)
    {
        var serialized = JsonSerializer.SerializeToElement(value, JsonOptions);
        if (serialized.ValueKind is JsonValueKind.Object &&
            serialized.TryGetProperty("data", out var data) &&
            serialized.TryGetProperty("returned", out var returned) &&
            serialized.TryGetProperty("truncated", out var truncated) &&
            serialized.TryGetProperty("workspaceLoadedAt", out var workspaceLoadedAt) &&
            serialized.TryGetProperty("workspaceDiagnostics", out var workspaceDiagnostics))
        {
            return new McpToolResponse(
                ToStructuredData(data),
                new McpResultMetadata(
                    "workspace-analysis",
                    returned.GetInt32(),
                    truncated.GetBoolean(),
                    workspaceLoadedAt.GetDateTimeOffset(),
                    workspaceDiagnostics.Deserialize<IReadOnlyList<string>>(JsonOptions),
                    data.ValueKind == JsonValueKind.Object && data.TryGetProperty("nextCursor", out var nextCursor) && nextCursor.ValueKind == JsonValueKind.String
                        ? nextCursor.GetString()
                        : null));
        }

        // Trust-management results do not load a workspace, so they use only the shared data field.
        return new McpToolResponse(ToStructuredData(serialized), new McpResultMetadata("trust-management"));
    }

    /// <summary>
    /// Keeps tool data object-shaped for schema validation and wraps naturally non-object management results.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> ToStructuredData(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.Clone(),
                StringComparer.Ordinal);
        }

        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["value"] = value.Clone()
        };
    }

    /// <summary>
    /// Reports request-scoped MCP progress around expensive workspace operations.
    /// </summary>
    private static async Task<McpToolResponse> ExecuteWithProgressAsync(
        Func<Task<object>> operation,
        IProgress<ProgressNotificationValue>? progress,
        string startMessage)
    {
        progress?.Report(new ProgressNotificationValue
        {
            Progress = 0,
            Total = 1,
            Message = startMessage
        });

        var result = await operation().ConfigureAwait(false);

        progress?.Report(new ProgressNotificationValue
        {
            Progress = 1,
            Total = 1,
            Message = "Roslyn analysis completed."
        });

        return Serialize(result);
    }
}
