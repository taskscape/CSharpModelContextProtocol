using System.Text.Json;

namespace CSharpMcp.Tools;

/// <summary>
/// Describes the common MCP result envelope while allowing each tool to advertise its own data fields.
/// </summary>
internal sealed record ToolOutputSchema<TData>(TData Data, McpResultMetadata Metadata);

internal sealed record ValueArrayDataSchema(IReadOnlyList<JsonElement> Value);

internal sealed record ReferenceDataSchema(JsonElement Symbol, IReadOnlyList<JsonElement> References, IReadOnlyDictionary<string, int> Counts);

internal sealed record CallHierarchyDataSchema(JsonElement Root, string Direction, int MaxDepth, IReadOnlyList<JsonElement> Edges);

internal sealed record ImplementationDataSchema(JsonElement Contract, IReadOnlyList<JsonElement> Implementations);

internal sealed record TypeUsageDataSchema(JsonElement Type, IReadOnlyList<JsonElement> Usages, IReadOnlyDictionary<string, int> Summary);

internal sealed record DiagnosticsDataSchema(string MinimumSeverity, bool IncludeAnalyzers, IReadOnlyList<JsonElement> Diagnostics, IReadOnlyDictionary<string, int> Counts);

internal sealed record DependenciesDataSchema(IReadOnlyList<JsonElement> Projects, IReadOnlyList<JsonElement> Cycles, bool HasCycles, IReadOnlyList<JsonElement> NamespaceEdges);

internal sealed record SearchDataSchema(string Query, IReadOnlyList<JsonElement> Symbols);

internal sealed record UnusedAuditDataSchema(IReadOnlyList<string> RequestedSymbolKinds, JsonElement Classifications, IReadOnlyList<JsonElement> Candidates, IReadOnlyDictionary<string, int> ExcludedSymbols);

internal sealed record ImpactDataSchema(JsonElement ChangedSymbol, IReadOnlyList<JsonElement> Contracts, IReadOnlyList<JsonElement> Implementations, IReadOnlyList<JsonElement> References, IReadOnlyList<JsonElement> Callers, IReadOnlyList<JsonElement> Tests, IReadOnlyList<JsonElement> DependentProjects, JsonElement SectionLimits);

internal sealed record PositionDataSchema(JsonElement Document, JsonElement Syntax, JsonElement? Symbol = null, JsonElement? DeclaredSymbol = null, JsonElement? EnclosingSymbol = null);

internal sealed record InvocationDataSchema(string OperationKind, string Expression, IReadOnlyList<JsonElement> Arguments, IReadOnlyList<JsonElement> Candidates, IReadOnlyList<JsonElement> Diagnostics, JsonElement? Target = null, string? ReceiverType = null);

internal sealed record MemberSurfaceDataSchema(JsonElement Type, string Mode, string AccessibilityFilter, IReadOnlyList<JsonElement> Members);

internal sealed record InheritanceDataSchema(JsonElement Root, string Direction, int MaxDepth, IReadOnlyList<JsonElement> Edges);

internal sealed record RefactorPreviewDataSchema(string RefactorKind, JsonElement Symbol, int ChangedDocuments, int TotalChanges, int PageOffset, IReadOnlyList<JsonElement> Changes, IReadOnlyList<JsonElement> Documents, JsonElement Freshness, JsonElement Conflicts, bool AppliedToDisk, string? NextCursor = null);

internal sealed record DiagnosticsDeltaDataSchema(string BaselineToken, DateTimeOffset CapturedAt, JsonElement Counts, int? DiagnosticCount = null, IReadOnlyList<JsonElement>? Introduced = null, IReadOnlyList<JsonElement>? Resolved = null);

internal sealed record TestImpactDataSchema(IReadOnlyList<JsonElement> Seeds, int MaxDepth, IReadOnlyList<JsonElement> Tests, int TraversedReferences, IReadOnlyList<string> Limitations);

internal sealed record GeneratorInventoryDataSchema(bool IncludeGeneratedSourceExcerpt, int PageOffset, IReadOnlyList<JsonElement> Projects, IReadOnlyList<string> Limitations, string? GeneratorFilter = null, string? GeneratedDocumentFilter = null, string? NextCursor = null);

internal sealed record ConditionalMatrixDataSchema(string Project, string? DocumentPath, IReadOnlyList<string> RequestedConfigurations, IReadOnlyList<string> RequestedTargetFrameworks, IReadOnlyList<JsonElement> Variants, string Limitation);

internal sealed record WorkspaceHealthDataSchema(string NormalizedPath, string RootPath, DateTimeOffset SnapshotLoadedAt, string EvaluatedConfiguration, int ProjectCount, int DocumentCount, IReadOnlyList<string> ExpectedProjectPaths, IReadOnlyList<string> SkippedProjectPaths, string? EvaluatedTargetFramework = null);

internal sealed record SymbolSourceDataSchema(bool IncludeBody, int MaxLines, int MaxCharacters, IReadOnlyList<JsonElement> Items);

internal sealed record EventFlowDataSchema(JsonElement Event, IReadOnlyList<JsonElement> Actions, IReadOnlyDictionary<string, int> Counts);

internal sealed record AttributeUsageDataSchema(JsonElement Attribute, bool IncludeInherited, int DirectCount, int InheritedCount, IReadOnlyList<JsonElement> Usages, IReadOnlyList<JsonElement>? MigrationGroups = null);

internal sealed record DependencyInjectionDataSchema(IReadOnlyList<JsonElement> Registrations, string Limitation, string? ServiceFilter = null);

internal sealed record ConstructionDataSchema(JsonElement Type, IReadOnlyList<JsonElement> Constructors, IReadOnlyList<JsonElement> Factories, IReadOnlyList<JsonElement> DependencyInjectionRegistrations, string? FromProject = null);

internal sealed record ApiCompatibilityDataSchema(IReadOnlyList<string> Projects, int CurrentApiCount, IReadOnlyList<JsonElement> Changes, JsonElement Counts, int PageOffset, string PagedSection, string CompatibilityEngine, string? BaselinePath = null, int? BaselineApiCount = null, string? NextCursor = null);

internal sealed record RegionFlowDataSchema(string Document, JsonElement AnalyzedSpan, JsonElement? DataFlow = null, JsonElement? ControlFlow = null);

internal sealed record ArchitectureDataSchema(IReadOnlyList<JsonElement> Rules, IReadOnlyList<JsonElement> Violations);

internal sealed record StackTraceDataSchema(IReadOnlyList<JsonElement> Frames);

internal sealed record ContextBundleDataSchema(string Profile, int MaxResultsPerSection, IReadOnlyDictionary<string, JsonElement> Sections, string RecommendedNextTool);

internal sealed record TrustDataSchema(string RootPath, bool SessionTrusted, bool Persisted);

internal sealed record TrustRevocationDataSchema(string RootPath, bool SessionTrustRemoved, bool PersistentTrustRemoved);
