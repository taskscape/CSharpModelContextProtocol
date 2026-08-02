using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CSharpMcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CSharpMcp.Analysis;

/// <summary>
/// Performs bounded, read-only Roslyn queries over cached MSBuild solution snapshots.
/// </summary>
internal sealed partial class RoslynAnalysisService
{
    private const int MaximumResultLimit = 1_000;
    private const int MaximumExcerptLength = 240;
    private readonly SolutionWorkspaceCache workspaceCache;

    /// <summary>
    /// Initializes the Roslyn analysis service.
    /// </summary>
    public RoslynAnalysisService(SolutionWorkspaceCache workspaceCache)
    {
        this.workspaceCache = workspaceCache;
    }

    /// <summary>
    /// Returns the loaded solution's projects, target frameworks, references, and entry points.
    /// </summary>
    public async Task<object> GetSolutionOverviewAsync(
        string workspacePath,
        string configuration,
        string? targetFramework,
        int maxProjects,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(
            workspacePath,
            new WorkspaceLoadOptions(configuration, targetFramework),
            cancellationToken).ConfigureAwait(false);
        var limit = BoundLimit(maxProjects);
        var projects = new List<object>();

        foreach (var project in snapshot.Solution.Projects
                     .Where(project => project.Language == LanguageNames.CSharp)
                     .OrderBy(project => project.Name, StringComparer.Ordinal)
                     .Take(limit))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var entryPoint = compilation?.GetEntryPoint(cancellationToken);
            projects.Add(new
            {
                id = project.Id.Id,
                project.Name,
                project.FilePath,
                targetFrameworks = ReadTargetFrameworks(project.FilePath),
                evaluatedConfiguration = snapshot.Configuration,
                evaluatedTargetFramework = snapshot.TargetFramework,
                outputKind = project.CompilationOptions?.OutputKind.ToString(),
                optimizationLevel = project.CompilationOptions?.OptimizationLevel.ToString(),
                nullableContextOptions = (project.CompilationOptions as CSharpCompilationOptions)?.NullableContextOptions.ToString(),
                languageVersion = (project.ParseOptions as CSharpParseOptions)?.LanguageVersion.ToString(),
                preprocessorSymbols = (project.ParseOptions as CSharpParseOptions)?.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray() ?? [],
                entryPoint = entryPoint is null ? null : SymbolResolver.GetStableId(entryPoint),
                projectReferences = project.ProjectReferences
                    .Select(reference => snapshot.Solution.GetProject(reference.ProjectId)?.Name)
                    .Where(name => name is not null)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                documentCount = project.DocumentIds.Count,
                analyzerReferenceCount = project.AnalyzerReferences.Count
            });
        }

        var total = snapshot.Solution.Projects.Count(project => project.Language == LanguageNames.CSharp);
        return Wrap(projects, projects.Count, total > projects.Count, snapshot);
    }

    /// <summary>
    /// Returns exact symbol declarations and semantic metadata.
    /// </summary>
    public async Task<object> GetSymbolInfoAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var limit = BoundLimit(maxResults);
        var symbols = await SymbolResolver.ResolveManyAsync(
            snapshot.Solution,
            symbolQuery,
            projectName,
            cancellationToken).ConfigureAwait(false);

        var results = new List<object>();
        foreach (var symbol in symbols.Take(limit))
        {
            var descriptor = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false);
            results.Add(new
            {
                symbol = descriptor,
                typeKind = (symbol as INamedTypeSymbol)?.TypeKind.ToString(),
                accessibility = symbol.DeclaredAccessibility.ToString(),
                modifiers = GetModifiers(symbol),
                containingSymbol = symbol.ContainingSymbol is null ? null : SymbolResolver.GetStableId(symbol.ContainingSymbol),
                baseType = (symbol as INamedTypeSymbol)?.BaseType is { } baseType
                    ? SymbolResolver.GetStableId(baseType)
                    : null,
                interfaces = GetInterfaces(symbol).Select(SymbolResolver.GetStableId).Order(StringComparer.Ordinal).ToArray(),
                documentation = GetDocumentation(symbol)
            });
        }

        return Wrap(results, results.Count, symbols.Count > results.Count, snapshot);
    }

    /// <summary>
    /// Finds exact compiler-bound references to one unambiguous source symbol.
    /// </summary>
    public async Task<object> FindReferencesAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        string? referenceKinds,
        bool includeDeclarations,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(
            snapshot.Solution,
            symbolQuery,
            projectName,
            cancellationToken).ConfigureAwait(false);
        var limit = BoundLimit(maxResults);
        var references = await SymbolFinder.FindReferencesAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var locations = references
            .SelectMany(reference => reference.Locations)
            .OrderBy(location => location.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.Location.SourceSpan.Start)
            .ToArray();

        var requestedKinds = string.IsNullOrWhiteSpace(referenceKinds)
            ? []
            : referenceKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => value.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
        var classified = new List<(ReferenceLocation Location, IReadOnlyList<string> Kinds)>();
        foreach (var location in locations)
        {
            var kinds = await ClassifyReferenceKindsAsync(symbol, location, cancellationToken).ConfigureAwait(false);
            if (requestedKinds.Count == 0 || kinds.Any(requestedKinds.Contains))
            {
                classified.Add((location, kinds));
            }
        }

        var results = new List<ReferenceDescriptor>();
        if (includeDeclarations && (requestedKinds.Count == 0 || requestedKinds.Contains("declaration")))
        {
            foreach (var declaration in await CreatePositionsAsync(
                         symbol.Locations.Where(location => location.IsInSource),
                         snapshot.Solution,
                         limit,
                         cancellationToken).ConfigureAwait(false))
            {
                results.Add(new ReferenceDescriptor("declaration", declaration, ["declaration"]));
            }
        }

        foreach (var item in classified.Take(Math.Max(0, limit - results.Count)))
        {
            results.Add(new ReferenceDescriptor(
                item.Kinds[0],
                await CreatePositionAsync(item.Location.Document, item.Location.Location, cancellationToken).ConfigureAwait(false),
                item.Kinds));
        }

        return Wrap(new
        {
            symbol = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            referenceKinds = requestedKinds.Order(StringComparer.Ordinal).ToArray(),
            references = results,
            counts = results.SelectMany(reference => reference.Kinds ?? [reference.Role])
                .GroupBy(role => role, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
        }, results.Count, classified.Count + (includeDeclarations ? symbol.DeclaringSyntaxReferences.Length : 0) > results.Count, snapshot);
    }

    /// <summary>
    /// Builds a bounded caller/callee graph for a method-like symbol.
    /// </summary>
    public async Task<object> GetCallHierarchyAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        string direction,
        int maxDepth,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var root = await SymbolResolver.ResolveSingleAsync(
            snapshot.Solution,
            symbolQuery,
            projectName,
            cancellationToken).ConfigureAwait(false);
        if (root is not IMethodSymbol)
        {
            throw new InvalidOperationException("call_hierarchy requires a method, constructor, property accessor, or local function symbol.");
        }

        var normalizedDirection = direction.Trim().ToLowerInvariant();
        if (normalizedDirection is not ("callers" or "callees" or "both"))
        {
            throw new ArgumentException("Direction must be callers, callees, or both.", nameof(direction));
        }

        var depthLimit = Math.Clamp(maxDepth, 1, 5);
        var resultLimit = BoundLimit(maxResults);
        var edges = new List<object>();
        var queue = new Queue<(ISymbol Symbol, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { SymbolResolver.GetStableId(root) };
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && edges.Count < resultLimit)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= depthLimit)
            {
                continue;
            }

            if (normalizedDirection is "callers" or "both")
            {
                var callers = await SymbolFinder.FindCallersAsync(current, snapshot.Solution, cancellationToken).ConfigureAwait(false);
                foreach (var caller in callers.OrderBy(item => SymbolResolver.GetStableId(item.CallingSymbol), StringComparer.Ordinal))
                {
                    edges.Add(new
                    {
                        direction = "caller",
                        depth = depth + 1,
                        from = await DescribeSymbolAsync(caller.CallingSymbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                        to = await DescribeSymbolAsync(current, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                        locations = await CreatePositionsAsync(caller.Locations, snapshot.Solution, 5, cancellationToken).ConfigureAwait(false)
                    });
                    EnqueueIfNew(caller.CallingSymbol, depth + 1, visited, queue);
                    if (edges.Count >= resultLimit)
                    {
                        break;
                    }
                }
            }

            if (edges.Count >= resultLimit)
            {
                break;
            }

            if (normalizedDirection is "callees" or "both")
            {
                var callees = await FindDirectCalleesAsync(current, snapshot.Solution, cancellationToken).ConfigureAwait(false);
                foreach (var callee in callees.OrderBy(SymbolResolver.GetStableId, StringComparer.Ordinal))
                {
                    edges.Add(new
                    {
                        direction = "callee",
                        depth = depth + 1,
                        from = await DescribeSymbolAsync(current, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                        to = await DescribeSymbolAsync(callee, snapshot.Solution, cancellationToken).ConfigureAwait(false)
                    });
                    EnqueueIfNew(callee, depth + 1, visited, queue);
                    if (edges.Count >= resultLimit)
                    {
                        break;
                    }
                }
            }
        }

        return Wrap(new
        {
            root = await DescribeSymbolAsync(root, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            direction = normalizedDirection,
            maxDepth = depthLimit,
            edges
        }, edges.Count, edges.Count >= resultLimit && queue.Count > 0, snapshot);
    }

    /// <summary>
    /// Maps an interface or abstract contract to source implementations and overrides.
    /// </summary>
    public async Task<object> GetImplementationMapAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(
            snapshot.Solution,
            symbolQuery,
            projectName,
            cancellationToken).ConfigureAwait(false);
        var limit = BoundLimit(maxResults);
        var implementations = await SymbolFinder.FindImplementationsAsync(
            symbol,
            snapshot.Solution,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var overrides = symbol is IMethodSymbol or IPropertySymbol or IEventSymbol
            ? await SymbolFinder.FindOverridesAsync(symbol, snapshot.Solution, cancellationToken: cancellationToken).ConfigureAwait(false)
            : [];
        var combined = implementations
            .Concat(overrides)
            .Where(candidate => candidate.Locations.Any(location => location.IsInSource))
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(SymbolResolver.GetStableId, StringComparer.Ordinal)
            .ToArray();

        var results = new List<SymbolDescriptor>();
        foreach (var candidate in combined.Take(limit))
        {
            results.Add(await DescribeSymbolAsync(candidate, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        return Wrap(new
        {
            contract = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            implementations = results
        }, results.Count, combined.Length > results.Count, snapshot);
    }

    /// <summary>
    /// Classifies construction, DI-like registration, public API, and other uses of a type.
    /// </summary>
    public async Task<object> GetTypeUsageAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(
            snapshot.Solution,
            symbolQuery,
            projectName,
            cancellationToken).ConfigureAwait(false);
        if (symbol is not INamedTypeSymbol)
        {
            throw new InvalidOperationException("type_usage requires a named type symbol.");
        }

        var limit = BoundLimit(maxResults);
        var references = await SymbolFinder.FindReferencesAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var locations = references.SelectMany(reference => reference.Locations).ToArray();
        var usages = new List<ReferenceDescriptor>();
        foreach (var location in locations.Take(limit))
        {
            usages.Add(new ReferenceDescriptor(
                await ClassifyTypeUsageAsync(location, cancellationToken).ConfigureAwait(false),
                await CreatePositionAsync(location.Document, location.Location, cancellationToken).ConfigureAwait(false)));
        }

        return Wrap(new
        {
            type = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            usages,
            summary = usages.GroupBy(usage => usage.Role, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
        }, usages.Count, locations.Length > usages.Count, snapshot);
    }

    /// <summary>
    /// Runs compiler diagnostics and optional project analyzers for affected projects.
    /// </summary>
    public async Task<object> GetDiagnosticsAsync(
        string workspacePath,
        string? projectName,
        string minimumSeverity,
        bool includeAnalyzers,
        string? documentPath,
        string? diagnosticIds,
        bool includeSuppressed,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var severity = ParseSeverity(minimumSeverity);
        var limit = BoundLimit(maxResults);
        var normalizedDocumentPath = string.IsNullOrWhiteSpace(documentPath) ? null : Path.GetFullPath(documentPath);
        var requestedIds = string.IsNullOrWhiteSpace(diagnosticIds)
            ? []
            : diagnosticIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<(Project Project, Diagnostic Diagnostic)>();
        var analyzerIdentities = new Dictionary<(ProjectId ProjectId, string DiagnosticId), IReadOnlyList<AnalyzerIdentityDescriptor>>();

        foreach (var project in SelectProjects(snapshot.Solution, projectName))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            ImmutableArray<Diagnostic> projectDiagnostics;
            var analyzers = includeAnalyzers
                ? project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(project.Language)).ToImmutableArray()
                : [];
            foreach (var diagnosticGroup in analyzers
                         .SelectMany(analyzer => analyzer.SupportedDiagnostics.Select(descriptor => new { analyzer, descriptor.Id }))
                         .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
            {
                analyzerIdentities[(project.Id, diagnosticGroup.Key)] = diagnosticGroup
                    .Select(item => item.analyzer.GetType())
                    .Distinct()
                    .Select(type => new AnalyzerIdentityDescriptor(
                        type.FullName ?? type.Name,
                        type.Assembly.GetName().Name ?? "unknown",
                        type.Assembly.GetName().Version?.ToString()))
                    .OrderBy(identity => identity.Assembly, StringComparer.Ordinal)
                    .ThenBy(identity => identity.Type, StringComparer.Ordinal)
                    .ToArray();
            }

            if (analyzers.Length > 0)
            {
                projectDiagnostics = await compilation
                    .WithAnalyzers(analyzers, options: null)
                    .GetAllDiagnosticsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                projectDiagnostics = compilation.GetDiagnostics(cancellationToken);
            }

            diagnostics.AddRange(projectDiagnostics
                .Where(diagnostic => diagnostic.Severity >= severity)
                .Where(diagnostic => includeSuppressed || !diagnostic.IsSuppressed)
                .Where(diagnostic => requestedIds.Count == 0 || requestedIds.Contains(diagnostic.Id))
                .Where(diagnostic => normalizedDocumentPath is null ||
                                     diagnostic.Location.IsInSource &&
                                     Path.GetFullPath(diagnostic.Location.GetLineSpan().Path)
                                         .Equals(normalizedDocumentPath, StringComparison.OrdinalIgnoreCase))
                .Select(diagnostic => (project, diagnostic)));
        }

        var ordered = diagnostics
            .OrderByDescending(item => item.Diagnostic.Severity)
            .ThenBy(item => item.Project.Name, StringComparer.Ordinal)
            .ThenBy(item => item.Diagnostic.Location.GetLineSpan().Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Diagnostic.Location.SourceSpan.Start)
            .ToArray();
        var results = new List<object>();

        foreach (var (project, diagnostic) in ordered.Take(limit))
        {
            results.Add(new
            {
                projectId = project.Id.Id,
                project = project.Name,
                diagnostic.Id,
                severity = diagnostic.Severity.ToString(),
                diagnostic.IsSuppressed,
                diagnostic.WarningLevel,
                message = diagnostic.GetMessage(),
                category = diagnostic.Descriptor.Category,
                title = diagnostic.Descriptor.Title.ToString(),
                customTags = diagnostic.Descriptor.CustomTags,
                origin = analyzerIdentities.ContainsKey((project.Id, diagnostic.Id)) ? "analyzer" : "compiler-or-generator",
                analyzers = analyzerIdentities.GetValueOrDefault((project.Id, diagnostic.Id), []),
                location = await CreatePositionAsync(project, diagnostic.Location, cancellationToken).ConfigureAwait(false),
                helpLink = diagnostic.Descriptor.HelpLinkUri
            });
        }

        return Wrap(new
        {
            minimumSeverity = severity.ToString(),
            includeAnalyzers,
            documentPath = normalizedDocumentPath,
            diagnosticIds = requestedIds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            includeSuppressed,
            diagnostics = results,
            counts = ordered.GroupBy(item => item.Diagnostic.Severity.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
        }, results.Count, ordered.Length > results.Count, snapshot);
    }

    /// <summary>
    /// Returns project-to-project and bounded assembly dependency information.
    /// </summary>
    public async Task<object> GetProjectDependenciesAsync(
        string workspacePath,
        string? projectName,
        bool includeNamespaceEdges,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var limit = BoundLimit(maxResults);
        var projects = SelectProjects(snapshot.Solution, projectName).ToArray();
        var results = new List<object>();
        var reverseDependencies = snapshot.Solution.Projects
            .SelectMany(project => project.ProjectReferences.Select(reference => new { From = project, reference.ProjectId }))
            .GroupBy(edge => edge.ProjectId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.From.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        foreach (var project in projects.Take(limit))
        {
            results.Add(new
            {
                id = project.Id.Id,
                project.Name,
                project.FilePath,
                targetFrameworks = ReadTargetFrameworks(project.FilePath),
                evaluatedConfiguration = snapshot.Configuration,
                evaluatedTargetFramework = snapshot.TargetFramework,
                projectReferences = project.ProjectReferences.Select(reference => new
                {
                    projectId = reference.ProjectId.Id,
                    project = snapshot.Solution.GetProject(reference.ProjectId)?.Name,
                    aliases = reference.Aliases
                }).OrderBy(reference => reference.project, StringComparer.Ordinal).ToArray(),
                referencedBy = reverseDependencies.GetValueOrDefault(project.Id, []),
                transitiveProjectDependencies = snapshot.Solution.GetProjectDependencyGraph()
                    .GetProjectsThatThisProjectTransitivelyDependsOn(project.Id)
                    .Select(snapshot.Solution.GetProject)
                    .Where(dependency => dependency is not null)
                    .Select(dependency => dependency!.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                transitiveReferencedBy = snapshot.Solution.GetProjectDependencyGraph()
                    .GetProjectsThatTransitivelyDependOnThisProject(project.Id)
                    .Select(snapshot.Solution.GetProject)
                    .Where(dependant => dependant is not null)
                    .Select(dependant => dependant!.Name)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                packageReferences = ReadPackageReferences(project.FilePath),
                assemblyReferences = project.MetadataReferences
                    .Select(reference => reference.Display)
                    .Where(display => display is not null)
                    .Select(display => Path.GetFileNameWithoutExtension(display))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(200)
                    .ToArray()
            });
        }

        var cycles = FindProjectDependencyCycles(snapshot.Solution);
        var namespaceEdges = includeNamespaceEdges
            ? await BuildNamespaceDependencyEdgesAsync(projects, limit, cancellationToken).ConfigureAwait(false)
            : [];
        return Wrap(new
        {
            projects = results,
            cycles,
            hasCycles = cycles.Count > 0,
            namespaceEdges,
            namespaceEdgesIncluded = includeNamespaceEdges
        }, results.Count + namespaceEdges.Length, projects.Length > results.Count || namespaceEdges.Length >= limit, snapshot);
    }

    /// <summary>
    /// Builds compiler-resolved namespace edges without treating using directives as proof of dependency.
    /// </summary>
    private static async Task<object[]> BuildNamespaceDependencyEdgesAsync(
        IReadOnlyList<Project> projects,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var edges = new Dictionary<string, NamespaceDependencyState>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var target = model.GetSymbolInfo(name, cancellationToken).Symbol;
                    var targetType = target as INamedTypeSymbol ?? target?.ContainingType;
                    var enclosing = model.GetEnclosingSymbol(name.SpanStart, cancellationToken);
                    var sourceType = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
                    if (sourceType is null || targetType is null ||
                        SymbolEqualityComparer.Default.Equals(sourceType, targetType))
                    {
                        continue;
                    }

                    var sourceNamespace = sourceType.ContainingNamespace.ToDisplayString();
                    var targetNamespace = targetType.ContainingNamespace.ToDisplayString();
                    if (sourceNamespace.Length == 0 || targetNamespace.Length == 0 ||
                        sourceNamespace.Equals(targetNamespace, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var key = $"{project.Id.Id}|{sourceNamespace}|{targetNamespace}";
                    if (!edges.TryGetValue(key, out var edge))
                    {
                        edge = new NamespaceDependencyState(project.Name, sourceNamespace, targetNamespace);
                        edges[key] = edge;
                    }

                    edge.ReferenceCount++;
                    if (edge.Examples.Count < 3)
                    {
                        edge.Examples.Add(await CreatePositionAsync(document, name.GetLocation(), cancellationToken).ConfigureAwait(false));
                    }
                }
            }
        }

        return edges.Values
            .OrderByDescending(edge => edge.ReferenceCount)
            .ThenBy(edge => edge.Project, StringComparer.Ordinal)
            .ThenBy(edge => edge.SourceNamespace, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetNamespace, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(edge => (object)new
            {
                project = edge.Project,
                sourceNamespace = edge.SourceNamespace,
                targetNamespace = edge.TargetNamespace,
                edge.ReferenceCount,
                examples = edge.Examples
            })
            .ToArray();
    }

    /// <summary>
    /// Searches source symbol names by concept tokens and ranks matches using qualified identity and documentation.
    /// </summary>
    public async Task<object> SemanticSearchAsync(
        string workspacePath,
        string query,
        string? projectName,
        string? symbolKinds,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var tokens = Regex.Split(query.Trim(), @"\W+")
            .Where(token => token.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
        {
            throw new ArgumentException("The semantic search query must contain at least one name token.", nameof(query));
        }

        var requestedKinds = ParseKinds(symbolKinds);
        var candidates = new Dictionary<string, (int Score, ISymbol Symbol)>(StringComparer.Ordinal);
        foreach (var project in SelectProjects(snapshot.Solution, projectName))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var symbols = compilation.GetSymbolsWithName(
                name => tokens.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)),
                SymbolFilter.TypeAndMember,
                cancellationToken);
            foreach (var symbol in symbols.Where(symbol => symbol.Locations.Any(location => location.IsInSource)))
            {
                if (requestedKinds.Count > 0 && !requestedKinds.Contains(symbol.Kind))
                {
                    continue;
                }

                var id = $"{project.Id.Id}:{SymbolResolver.GetStableId(symbol)}";
                var score = ScoreSymbol(symbol, tokens);
                if (!candidates.TryGetValue(id, out var current) || score > current.Score)
                {
                    candidates[id] = (score, symbol);
                }
            }
        }

        var ordered = candidates.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => SymbolResolver.GetStableId(candidate.Symbol), StringComparer.Ordinal)
            .ToArray();
        var limit = BoundLimit(maxResults);
        var results = new List<object>();
        foreach (var candidate in ordered.Take(limit))
        {
            results.Add(new
            {
                candidate.Score,
                symbol = await DescribeSymbolAsync(candidate.Symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false)
            });
        }

        return Wrap(new { query, symbols = results }, results.Count, ordered.Length > results.Count, snapshot);
    }

    /// <summary>
    /// Computes a conservative source impact set for a prospective contract change.
    /// </summary>
    public async Task<object> GetAffectedSymbolsAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        int maxContracts,
        int maxImplementations,
        int maxReferences,
        int maxCallers,
        int maxTests,
        int maxDependentProjects,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(
            snapshot.Solution,
            symbolQuery,
            projectName,
            cancellationToken).ConfigureAwait(false);
        var contractLimit = BoundLimit(maxContracts);
        var implementationLimit = BoundLimit(maxImplementations);
        var referenceLimit = BoundLimit(maxReferences);
        var callerLimit = BoundLimit(maxCallers);
        var testLimit = BoundLimit(maxTests);
        var dependentProjectLimit = BoundLimit(maxDependentProjects);

        var contractSymbols = new List<ISymbol> { symbol };
        if (symbol.ContainingSymbol is INamedTypeSymbol containingType)
        {
            contractSymbols.Add(containingType);
        }

        var implementationSymbols = (await SymbolFinder.FindImplementationsAsync(
            symbol,
            snapshot.Solution,
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();

        if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
        {
            var overrides = await SymbolFinder.FindOverridesAsync(
                symbol,
                snapshot.Solution,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var overrideSymbol in overrides)
            {
                implementationSymbols.Add(overrideSymbol);
            }
        }

        var distinctImplementations = implementationSymbols
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(SymbolResolver.GetStableId, StringComparer.Ordinal)
            .ToArray();
        var describedContracts = new List<SymbolDescriptor>();
        foreach (var contract in contractSymbols.Distinct(SymbolEqualityComparer.Default).Take(contractLimit))
        {
            describedContracts.Add(await DescribeSymbolAsync(contract, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        var describedImplementations = new List<object>();
        foreach (var implementation in distinctImplementations.Take(implementationLimit))
        {
            describedImplementations.Add(new
            {
                relation = implementation switch
                {
                    IMethodSymbol { IsOverride: true } or
                    IPropertySymbol { IsOverride: true } or
                    IEventSymbol { IsOverride: true } => "override",
                    _ => "implementation"
                },
                symbol = await DescribeSymbolAsync(implementation, snapshot.Solution, cancellationToken).ConfigureAwait(false)
            });
        }

        var referenceGroups = await SymbolFinder.FindReferencesAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var referenceLocations = referenceGroups.SelectMany(reference => reference.Locations)
            .OrderBy(location => location.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.Location.SourceSpan.Start)
            .ToArray();
        var productionReferences = new List<object>();
        var testReferences = new List<object>();
        foreach (var location in referenceLocations)
        {
            var enclosing = await FindEnclosingDeclaredSymbolAsync(
                location.Document,
                location.Location.SourceSpan.Start,
                cancellationToken).ConfigureAwait(false);
            var item = new
            {
                kinds = await ClassifyReferenceKindsAsync(symbol, location, cancellationToken).ConfigureAwait(false),
                location = await CreatePositionAsync(location.Document, location.Location, cancellationToken).ConfigureAwait(false),
                enclosingSymbol = enclosing is null
                    ? null
                    : await DescribeSymbolAsync(enclosing, snapshot.Solution, cancellationToken).ConfigureAwait(false)
            };

            if (IsTestProject(location.Document.Project))
            {
                if (testReferences.Count < testLimit)
                {
                    testReferences.Add(item);
                }
            }
            else if (productionReferences.Count < referenceLimit)
            {
                productionReferences.Add(item);
            }
        }

        var callerSymbols = symbol is IMethodSymbol
            ? (await SymbolFinder.FindCallersAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false))
                .Select(caller => caller.CallingSymbol)
                .Distinct(SymbolEqualityComparer.Default)
                .OrderBy(SymbolResolver.GetStableId, StringComparer.Ordinal)
                .ToArray()
            : [];
        var describedCallers = new List<SymbolDescriptor>();
        foreach (var caller in callerSymbols.Take(callerLimit))
        {
            describedCallers.Add(await DescribeSymbolAsync(caller, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        var declaringProjectIds = symbol.Locations
            .Where(location => location.IsInSource && location.SourceTree is not null)
            .Select(location => snapshot.Solution.GetDocument(location.SourceTree!)?.Project.Id)
            .Where(id => id is not null)
            .Cast<ProjectId>()
            .ToHashSet();
        var dependencyGraph = snapshot.Solution.GetProjectDependencyGraph();
        var dependentProjects = declaringProjectIds
            .SelectMany(dependencyGraph.GetProjectsThatTransitivelyDependOnThisProject)
            .Distinct()
            .Select(snapshot.Solution.GetProject)
            .Where(project => project is not null)
            .Select(project => new
            {
                projectId = project!.Id.Id,
                project = project.Name,
                isTestProject = IsTestProject(project)
            })
            .OrderBy(project => project.project, StringComparer.Ordinal)
            .ToArray();
        var boundedDependentProjects = dependentProjects.Take(dependentProjectLimit).ToArray();

        var contractCount = contractSymbols.Distinct(SymbolEqualityComparer.Default).Count();
        var productionReferenceCount = referenceLocations.Count(location => !IsTestProject(location.Document.Project));
        var testReferenceCount = referenceLocations.Length - productionReferenceCount;
        var sectionTruncation = new
        {
            contracts = contractCount > describedContracts.Count,
            implementations = distinctImplementations.Length > describedImplementations.Count,
            references = productionReferenceCount > productionReferences.Count,
            callers = callerSymbols.Length > describedCallers.Count,
            tests = testReferenceCount > testReferences.Count,
            dependentProjects = dependentProjects.Length > boundedDependentProjects.Length
        };
        var truncated = sectionTruncation.contracts || sectionTruncation.implementations ||
                        sectionTruncation.references || sectionTruncation.callers || sectionTruncation.tests ||
                        sectionTruncation.dependentProjects;

        return Wrap(new
        {
            changedSymbol = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            contracts = describedContracts,
            implementations = describedImplementations,
            references = productionReferences,
            callers = describedCallers,
            tests = testReferences,
            dependentProjects = boundedDependentProjects,
            summary = new
            {
                contracts = contractCount,
                implementations = distinctImplementations.Length,
                references = productionReferenceCount,
                callers = callerSymbols.Length,
                tests = testReferenceCount,
                dependentProjects = dependentProjects.Length
            },
            sectionLimits = new
            {
                contracts = contractLimit,
                implementations = implementationLimit,
                references = referenceLimit,
                callers = callerLimit,
                tests = testLimit,
                dependentProjects = dependentProjectLimit
            },
            sectionTruncation,
            limitation = "Compile-time conservative impact only; reflection, runtime configuration, databases, and distributed routing are not resolved."
        }, describedContracts.Count + describedImplementations.Count + productionReferences.Count +
           describedCallers.Count + testReferences.Count + boundedDependentProjects.Length, truncated, snapshot);
    }

    private static int BoundLimit(int requested)
    {
        return Math.Clamp(requested, 1, MaximumResultLimit);
    }

    private static object Wrap<T>(T data, int returned, bool truncated, WorkspaceSnapshot snapshot)
    {
        return new BoundedResult<T>(data, returned, truncated, snapshot.LoadedAt, snapshot.LoadDiagnostics);
    }

    private static IReadOnlyList<string> ReadTargetFrameworks(string? projectPath)
    {
        if (projectPath is null || !File.Exists(projectPath))
        {
            return [];
        }

        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.None);
            return document.Descendants()
                .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static IReadOnlyList<object> ReadPackageReferences(string? projectPath)
    {
        if (projectPath is null || !File.Exists(projectPath))
        {
            return [];
        }

        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.None);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => (object)new
                {
                    id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value,
                    version = element.Attribute("Version")?.Value ??
                              element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value,
                    privateAssets = element.Attribute("PrivateAssets")?.Value ??
                                    element.Elements().FirstOrDefault(child => child.Name.LocalName == "PrivateAssets")?.Value
                })
                .Where(package => package.GetType().GetProperty("id")!.GetValue(package) is not null)
                .OrderBy(package => package.GetType().GetProperty("id")!.GetValue(package)?.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    private static IReadOnlyList<IReadOnlyList<string>> FindProjectDependencyCycles(Solution solution)
    {
        var projects = solution.Projects.ToDictionary(project => project.Id);
        var state = new Dictionary<ProjectId, int>();
        var stack = new List<ProjectId>();
        var cycles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var project in projects.Values)
        {
            Visit(project.Id);
        }

        return cycles.Values.OrderBy(cycle => string.Join(" -> ", cycle), StringComparer.Ordinal).ToArray();

        void Visit(ProjectId projectId)
        {
            if (state.GetValueOrDefault(projectId) == 2)
            {
                return;
            }

            if (state.GetValueOrDefault(projectId) == 1)
            {
                var index = stack.IndexOf(projectId);
                if (index >= 0)
                {
                    var ids = stack.Skip(index).Append(projectId).ToArray();
                    var names = ids.Select(id => projects[id].Name).ToArray();
                    var canonical = string.Join("|", names.Take(names.Length - 1).Order(StringComparer.Ordinal));
                    cycles[canonical] = names;
                }

                return;
            }

            state[projectId] = 1;
            stack.Add(projectId);
            foreach (var reference in projects[projectId].ProjectReferences.Where(reference => projects.ContainsKey(reference.ProjectId)))
            {
                Visit(reference.ProjectId);
            }

            stack.RemoveAt(stack.Count - 1);
            state[projectId] = 2;
        }
    }

    private static IReadOnlyList<string> GetModifiers(ISymbol symbol)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic)
        {
            modifiers.Add("static");
        }

        if (symbol.IsAbstract)
        {
            modifiers.Add("abstract");
        }

        if (symbol.IsVirtual)
        {
            modifiers.Add("virtual");
        }

        if (symbol.IsOverride)
        {
            modifiers.Add("override");
        }

        if (symbol.IsSealed)
        {
            modifiers.Add("sealed");
        }

        return modifiers;
    }

    private static IEnumerable<INamedTypeSymbol> GetInterfaces(ISymbol symbol)
    {
        return symbol switch
        {
            INamedTypeSymbol type => type.AllInterfaces,
            IMethodSymbol method when method.ExplicitInterfaceImplementations.Length > 0 =>
                method.ExplicitInterfaceImplementations.Select(implementation => implementation.ContainingType),
            IPropertySymbol property when property.ExplicitInterfaceImplementations.Length > 0 =>
                property.ExplicitInterfaceImplementations.Select(implementation => implementation.ContainingType),
            _ => []
        };
    }

    private static string? GetDocumentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(expandIncludes: true);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var text = string.Join(" ", XDocument.Parse($"<root>{xml}</root>").Root!
                .DescendantNodesAndSelf()
                .OfType<XText>()
                .Select(node => node.Value.Trim())
                .Where(value => value.Length > 0));
            return Trim(text, 1_000);
        }
        catch (System.Xml.XmlException)
        {
            return Trim(xml, 1_000);
        }
    }

    private static async Task<SymbolDescriptor> DescribeSymbolAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var declarations = await CreatePositionsAsync(
            symbol.Locations.Where(location => location.IsInSource),
            solution,
            20,
            cancellationToken).ConfigureAwait(false);
        var firstProject = declarations.Select(position => (position.ProjectId, position.Project)).FirstOrDefault();

        return new SymbolDescriptor(
            SymbolResolver.GetStableId(symbol),
            SymbolResolver.GetDisplay(symbol),
            symbol.Kind.ToString(),
            SymbolResolver.GetMetadataName(symbol),
            firstProject.ProjectId,
            firstProject.Project,
            declarations);
    }

    private static async Task<IReadOnlyList<SourcePosition>> CreatePositionsAsync(
        IEnumerable<Location> locations,
        Solution solution,
        int limit,
        CancellationToken cancellationToken)
    {
        var results = new List<SourcePosition>();
        foreach (var location in locations.Take(limit))
        {
            var document = solution.GetDocument(location.SourceTree);
            results.Add(document is null
                ? CreatePosition(location)
                : await CreatePositionAsync(document, location, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<SourcePosition> CreatePositionAsync(
        Project project,
        Location location,
        CancellationToken cancellationToken)
    {
        var document = location.SourceTree is null ? null : project.Solution.GetDocument(location.SourceTree);
        return document is null
            ? CreatePosition(location, project)
            : await CreatePositionAsync(document, location, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SourcePosition> CreatePositionAsync(
        Document document,
        Location location,
        CancellationToken cancellationToken)
    {
        var lineSpan = location.GetLineSpan();
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var lineNumber = Math.Clamp(lineSpan.StartLinePosition.Line, 0, Math.Max(0, text.Lines.Count - 1));
        var excerpt = text.Lines.Count == 0 ? null : Trim(text.Lines[lineNumber].ToString().Trim(), MaximumExcerptLength);

        return new SourcePosition(
            document.Project.Id.Id.ToString(),
            document.Project.Name,
            document.FilePath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1,
            excerpt);
    }

    private static SourcePosition CreatePosition(Location location, Project? project = null)
    {
        var lineSpan = location.GetLineSpan();
        return new SourcePosition(
            project?.Id.Id.ToString(),
            project?.Name,
            lineSpan.Path,
            location.IsInSource ? lineSpan.StartLinePosition.Line + 1 : null,
            location.IsInSource ? lineSpan.StartLinePosition.Character + 1 : null,
            null);
    }

    private static async Task<IReadOnlyList<string>> ClassifyReferenceKindsAsync(
        ISymbol symbol,
        ReferenceLocation reference,
        CancellationToken cancellationToken)
    {
        var root = await reference.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await reference.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var node = root?.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
        if (node is null)
        {
            return ["reference"];
        }

        var kinds = new List<string>();
        var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation?.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
        {
            kinds.Add("nameof");
        }
        else if (node.AncestorsAndSelf().OfType<TypeOfExpressionSyntax>().Any())
        {
            kinds.Add("typeof");
        }
        else if (node.AncestorsAndSelf().OfType<AttributeSyntax>().Any())
        {
            kinds.Add("attribute");
        }
        else if (node.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().Any())
        {
            kinds.Add("object_creation");
        }
        else if (node.AncestorsAndSelf().OfType<CastExpressionSyntax>().Any())
        {
            kinds.Add("cast");
        }
        else if (node.AncestorsAndSelf().Any(ancestor => ancestor is IsPatternExpressionSyntax or BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression }))
        {
            kinds.Add("type_check");
        }
        else if (node.AncestorsAndSelf().OfType<BaseListSyntax>().Any())
        {
            kinds.Add("base_type");
        }
        else if (node.AncestorsAndSelf().OfType<TypeParameterConstraintClauseSyntax>().Any())
        {
            kinds.Add("type_constraint");
        }
        else if (node.AncestorsAndSelf().OfType<TypeArgumentListSyntax>().Any())
        {
            kinds.Add("type_argument");
        }
        else if (node.AncestorsAndSelf().OfType<CrefSyntax>().Any())
        {
            kinds.Add("xml_doc");
        }
        else if (node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault() is { } assignment &&
                 assignment.Left.Span.Contains(node.Span))
        {
            if (symbol is IEventSymbol && assignment.IsKind(SyntaxKind.AddAssignmentExpression))
            {
                kinds.Add("event_subscribe");
            }
            else if (symbol is IEventSymbol && assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
            {
                kinds.Add("event_unsubscribe");
            }
            else if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                kinds.Add("write");
            }
            else
            {
                kinds.Add("readwrite");
            }
        }
        else if (node.Parent is ArgumentSyntax argument && argument.RefKindKeyword.Kind() is SyntaxKind.RefKeyword)
        {
            kinds.Add("readwrite");
        }
        else if (node.Parent is ArgumentSyntax { RefKindKeyword.RawKind: (int)SyntaxKind.OutKeyword })
        {
            kinds.Add("write");
        }
        else if (node.AncestorsAndSelf().Any(ancestor => ancestor is PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax))
        {
            kinds.Add("readwrite");
        }
        else if (symbol is IMethodSymbol && invocation is not null)
        {
            kinds.Add("invocation");
        }
        else if (symbol is IMethodSymbol)
        {
            kinds.Add("method_group");
        }
        else
        {
            kinds.Add("read");
        }

        if (invocation is not null && model?.GetSymbolInfo(invocation, cancellationToken).Symbol is IMethodSymbol invokedMethod &&
            TryClassifyDiMethod(invokedMethod, out _, out _))
        {
            kinds.Add("dependency_injection_registration");
        }

        if (IsTestProject(reference.Document.Project))
        {
            kinds.Add("test_reference");
        }

        return kinds.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static async Task<string> ClassifyTypeUsageAsync(
        ReferenceLocation reference,
        CancellationToken cancellationToken)
    {
        var root = await reference.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var node = root?.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
        if (node?.AncestorsAndSelf().OfType<ObjectCreationExpressionSyntax>().Any() == true)
        {
            return "construction";
        }

        var invocation = node?.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation?.Expression.ToString().Contains("Add", StringComparison.Ordinal) == true)
        {
            return "di-registration-candidate";
        }

        if (node?.AncestorsAndSelf().Any(ancestor => ancestor is ParameterSyntax or PropertyDeclarationSyntax or MethodDeclarationSyntax) == true)
        {
            return "api-signature";
        }

        return "type-reference";
    }

    private static async Task<IReadOnlyList<ISymbol>> FindDirectCalleesAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var results = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var node = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
            var document = solution.GetDocument(node.SyntaxTree);
            var semanticModel = document is null
                ? null
                : await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null)
            {
                continue;
            }

            foreach (var invocation in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var called = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol;
                if (called is IMethodSymbol method)
                {
                    results.Add(method.OriginalDefinition);
                }
            }

            foreach (var creation in node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var constructor = semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol;
                if (constructor is IMethodSymbol method)
                {
                    results.Add(method.OriginalDefinition);
                }
            }
        }

        return results.OrderBy(SymbolResolver.GetStableId, StringComparer.Ordinal).ToArray();
    }

    private static void EnqueueIfNew(
        ISymbol symbol,
        int depth,
        ISet<string> visited,
        Queue<(ISymbol Symbol, int Depth)> queue)
    {
        var id = SymbolResolver.GetStableId(symbol);
        if (visited.Add(id))
        {
            queue.Enqueue((symbol, depth));
        }
    }

    private static IEnumerable<Project> SelectProjects(Solution solution, string? projectName)
    {
        var projects = solution.Projects.Where(project => project.Language == LanguageNames.CSharp);
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return projects.OrderBy(project => project.Name, StringComparer.Ordinal);
        }

        var selected = projects.Where(project => project.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return selected.Length > 0
            ? selected
            : throw new InvalidOperationException($"Project '{projectName}' was not found in the loaded solution.");
    }

    private static DiagnosticSeverity ParseSeverity(string minimumSeverity)
    {
        return minimumSeverity.Trim().ToLowerInvariant() switch
        {
            "hidden" => DiagnosticSeverity.Hidden,
            "info" => DiagnosticSeverity.Info,
            "warning" => DiagnosticSeverity.Warning,
            "error" => DiagnosticSeverity.Error,
            _ => throw new ArgumentException("Minimum severity must be hidden, info, warning, or error.", nameof(minimumSeverity))
        };
    }

    private static HashSet<SymbolKind> ParseKinds(string? symbolKinds)
    {
        if (string.IsNullOrWhiteSpace(symbolKinds))
        {
            return [];
        }

        var kinds = new HashSet<SymbolKind>();
        foreach (var value in symbolKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<SymbolKind>(value, ignoreCase: true, out var kind))
            {
                throw new ArgumentException($"Unknown symbol kind '{value}'.", nameof(symbolKinds));
            }

            kinds.Add(kind);
        }

        return kinds;
    }

    private static int ScoreSymbol(ISymbol symbol, IReadOnlyList<string> tokens)
    {
        var display = SymbolResolver.GetDisplay(symbol);
        var documentation = GetDocumentation(symbol) ?? string.Empty;
        var score = 0;

        foreach (var token in tokens)
        {
            if (symbol.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
            else if (symbol.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (display.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }

            if (documentation.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }
        }

        return score;
    }

    private static async Task<ISymbol?> FindEnclosingDeclaredSymbolAsync(
        Document document,
        int position,
        CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        return semanticModel?.GetEnclosingSymbol(position, cancellationToken);
    }

    private static void AddAffected(
        IDictionary<string, (string Reason, ISymbol Symbol)> affected,
        ISymbol? symbol,
        string reason)
    {
        if (symbol is null || !symbol.Locations.Any(location => location.IsInSource))
        {
            return;
        }

        var key = SymbolResolver.GetStableId(symbol);
        affected.TryAdd(key, (reason, symbol));
    }

    private static string Trim(string value, int maximumLength)
    {
        return value.Length <= maximumLength ? value : $"{value[..maximumLength]}…";
    }
}
