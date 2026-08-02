namespace CSharpMcp.Analysis;

/// <summary>
/// Identifies a source position without returning a full document to the model.
/// </summary>
internal sealed record SourcePosition(
    string? ProjectId,
    string? Project,
    string? File,
    int? Line,
    int? Column,
    string? Excerpt);

/// <summary>
/// Provides a stable, compact description of a Roslyn symbol.
/// </summary>
internal sealed record SymbolDescriptor(
    string Id,
    string Display,
    string Kind,
    string? MetadataName,
    string? ProjectId,
    string? Project,
    IReadOnlyList<SourcePosition> Declarations);

/// <summary>
/// Describes one exact source reference and its semantic role.
/// </summary>
internal sealed record ReferenceDescriptor(
    string Role,
    SourcePosition Location,
    IReadOnlyList<string>? Kinds = null);

/// <summary>
/// Identifies the analyzer implementation that declares one diagnostic ID.
/// </summary>
internal sealed record AnalyzerIdentityDescriptor(
    string Type,
    string Assembly,
    string? AssemblyVersion);

/// <summary>
/// Accumulates a bounded semantic namespace dependency edge.
/// </summary>
internal sealed class NamespaceDependencyState
{
    public NamespaceDependencyState(string project, string sourceNamespace, string targetNamespace)
    {
        Project = project;
        SourceNamespace = sourceNamespace;
        TargetNamespace = targetNamespace;
    }

    public string Project { get; }

    public string SourceNamespace { get; }

    public string TargetNamespace { get; }

    public int ReferenceCount { get; set; }

    public List<SourcePosition> Examples { get; } = [];
}

/// <summary>
/// Wraps tool output so callers can distinguish bounded results from complete results.
/// </summary>
internal sealed record BoundedResult<T>(
    T Data,
    int Returned,
    bool Truncated,
    DateTimeOffset WorkspaceLoadedAt,
    IReadOnlyList<string> WorkspaceDiagnostics);

/// <summary>
/// Defines one semantic namespace-boundary rule for architecture validation.
/// </summary>
internal sealed record ArchitectureRuleInput(
    string Name,
    string FromNamespace,
    string[]? Forbid,
    string[]? AllowOnly);
