using System.Xml.Linq;
using CSharpMcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpMcp.Analysis;

internal sealed partial class RoslynAnalysisService
{
    private const int MaximumAuditedSymbols = 20_000;

    private static readonly HashSet<string> ConventionEntryPointNames = new(StringComparer.Ordinal)
    {
        "Configure",
        "ConfigureServices",
        "GetAwaiter",
        "GetEnumerator",
        "Invoke",
        "InvokeAsync"
    };

    private static readonly string[] RuntimeAttributeNameFragments =
    [
        "Action",
        "Benchmark",
        "Command",
        "Event",
        "Fact",
        "Function",
        "Handler",
        "Http",
        "JsonConstructor",
        "LibraryImport",
        "McpServerTool",
        "Message",
        "OnDeserializ",
        "OnSerializ",
        "Route",
        "Rpc",
        "Subscribe",
        "Test",
        "Theory",
        "Timer",
        "Trigger",
        "UnmanagedCallersOnly"
    ];

    /// <summary>
    /// Finds source types and members with no external source references or references only from test projects.
    /// </summary>
    public async Task<object> AuditUnusedSymbolsAsync(
        string workspacePath,
        string? projectName,
        bool includeTestProjectsAsCandidates,
        string? symbolKinds,
        int maxSymbols,
        int maxResults,
        int maxReferencesPerSymbol,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var solution = snapshot.Solution;
        var allProjects = solution.Projects
            .Where(project => project.Language == LanguageNames.CSharp)
            .OrderBy(project => project.Name, StringComparer.Ordinal)
            .ToArray();
        var testProjectIds = allProjects
            .Where(IsTestProject)
            .Select(project => project.Id)
            .ToHashSet();
        var selectedProjects = SelectProjects(solution, projectName)
            .Where(project => includeTestProjectsAsCandidates || !testProjectIds.Contains(project.Id))
            .ToArray();
        var referenceProjects = SelectReferenceProjects(solution, allProjects, selectedProjects);
        var requestedKinds = ParseSymbolKinds(symbolKinds);
        if (requestedKinds.Count == 0)
        {
            requestedKinds.UnionWith([SymbolKind.NamedType, SymbolKind.Method, SymbolKind.Property, SymbolKind.Field, SymbolKind.Event]);
        }

        var symbolLimit = Math.Clamp(maxSymbols, 1, MaximumAuditedSymbols);
        var resultLimit = BoundLimit(maxResults);
        var referenceExampleLimit = Math.Clamp(maxReferencesPerSymbol, 0, 50);
        var exclusions = new Dictionary<string, int>(StringComparer.Ordinal);
        var discoveredSymbols = new List<UnusedSymbolAuditState>();

        foreach (var project in selectedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var entryPoint = compilation.GetEntryPoint(cancellationToken);
            foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || semanticModel is null)
                {
                    continue;
                }

                var generatedDocument = IsGeneratedDocument(document.FilePath);
                foreach (var declaredSymbol in EnumerateAuditableDeclarations(root, semanticModel, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!requestedKinds.Contains(declaredSymbol.Kind))
                    {
                        continue;
                    }

                    var exclusion = GetSymbolExclusion(declaredSymbol, entryPoint, generatedDocument);
                    if (exclusion is not null)
                    {
                        Increment(exclusions, exclusion);
                        continue;
                    }

                    discoveredSymbols.Add(new UnusedSymbolAuditState(declaredSymbol, document));
                }
            }
        }

        var orderedSymbols = discoveredSymbols
            .GroupBy(state => CreateSymbolKey(state.Symbol), StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(state => state.Symbol.ContainingAssembly?.Identity.Name, StringComparer.Ordinal)
            .ThenBy(state => SymbolResolver.GetStableId(state.Symbol), StringComparer.Ordinal)
            .ToArray();
        var analyzedSymbols = orderedSymbols.Take(symbolLimit).ToArray();
        var symbolEnumerationTruncated = orderedSymbols.Length > analyzedSymbols.Length;
        var symbolsByKey = analyzedSymbols.ToDictionary(
            state => CreateSymbolKey(state.Symbol),
            StringComparer.Ordinal);
        var candidateNames = analyzedSymbols
            .Select(state => state.Symbol.Name)
            .ToHashSet(StringComparer.Ordinal);
        var scannedDocuments = await IndexSymbolReferencesAsync(
            referenceProjects,
            testProjectIds,
            symbolsByKey,
            candidateNames,
            referenceExampleLimit,
            cancellationToken).ConfigureAwait(false);

        var candidateStates = analyzedSymbols
            .Where(state => state.ProductionReferenceCount == 0)
            .OrderBy(state => state.TestReferenceCount > 0 ? 1 : 0)
            .ThenBy(state => state.Document.Project.Name, StringComparer.Ordinal)
            .ThenBy(state => SymbolResolver.GetStableId(state.Symbol), StringComparer.Ordinal)
            .ToArray();
        var noReferenceCount = candidateStates.Count(state => state.TestReferenceCount == 0);
        var testOnlyReferenceCount = candidateStates.Length - noReferenceCount;
        var results = new List<object>();

        foreach (var state in candidateStates.Take(resultLimit))
        {
            results.Add(new
            {
                category = state.TestReferenceCount > 0 ? "test-only-references" : "no-source-references",
                symbol = await DescribeSymbolAsync(state.Symbol, solution, cancellationToken).ConfigureAwait(false),
                accessibility = state.Symbol.DeclaredAccessibility.ToString(),
                riskFlags = GetSymbolRiskFlags(state.Symbol),
                productionReferenceCount = state.ProductionReferenceCount,
                testReferenceCount = state.TestReferenceCount,
                selfReferenceCount = state.SelfReferenceCount,
                referenceExamples = state.ReferenceExamples
            });
        }

        return Wrap(new
        {
            auditedProjects = selectedProjects.Select(project => project.Name).ToArray(),
            referenceScanProjects = referenceProjects.Select(project => project.Name).ToArray(),
            classifiedTestProjects = allProjects
                .Where(project => testProjectIds.Contains(project.Id))
                .Select(project => project.Name)
                .ToArray(),
            requestedSymbolKinds = requestedKinds.Select(kind => kind.ToString()).OrderBy(kind => kind, StringComparer.Ordinal).ToArray(),
            discoveredSymbolCount = orderedSymbols.Length,
            analyzedSymbolCount = analyzedSymbols.Length,
            scannedDocumentCount = scannedDocuments,
            symbolEnumerationTruncated,
            classifications = new
            {
                noSourceReferences = noReferenceCount,
                testOnlyReferences = testOnlyReferenceCount,
                productionReferenced = analyzedSymbols.Count(state => state.ProductionReferenceCount > 0)
            },
            excludedSymbols = exclusions.OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            candidates = results,
            limitations = new[]
            {
                "Candidates require review; no source reference does not prove runtime removability.",
                "Reflection, configuration, Razor/XAML binding, database contracts, serialization conventions, and dynamically loaded code may not produce Roslyn references.",
                "Public and protected candidates are flagged externally-visible and must receive contract review before removal.",
                "Self-recursion is reported separately and does not make an otherwise unreachable method production-referenced."
            }
        }, results.Count, symbolEnumerationTruncated || candidateStates.Length > results.Count, snapshot);
    }

    /// <summary>
    /// Limits reference indexing to audited projects and projects that can reference them through the project graph.
    /// </summary>
    private static IReadOnlyList<Project> SelectReferenceProjects(
        Solution solution,
        IReadOnlyList<Project> allProjects,
        IReadOnlyList<Project> auditedProjects)
    {
        var dependencyGraph = solution.GetProjectDependencyGraph();
        var projectIds = auditedProjects.Select(project => project.Id).ToHashSet();
        foreach (var project in auditedProjects)
        {
            projectIds.UnionWith(dependencyGraph.GetProjectsThatTransitivelyDependOnThisProject(project.Id));
        }

        return allProjects.Where(project => projectIds.Contains(project.Id)).ToArray();
    }

    /// <summary>
    /// Builds one compiler-bound reference index instead of running a whole-solution reference search per method.
    /// </summary>
    private static async Task<int> IndexSymbolReferencesAsync(
        IReadOnlyList<Project> projects,
        IReadOnlySet<ProjectId> testProjectIds,
        IReadOnlyDictionary<string, UnusedSymbolAuditState> symbolsByKey,
        IReadOnlySet<string> candidateNames,
        int referenceExampleLimit,
        CancellationToken cancellationToken)
    {
        var scannedDocuments = 0;
        foreach (var project in projects)
        {
            foreach (var document in project.Documents.OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || semanticModel is null)
                {
                    continue;
                }

                scannedDocuments++;
                foreach (var name in root.DescendantNodes(descendIntoTrivia: true).OfType<SimpleNameSyntax>())
                {
                    if (!candidateNames.Contains(name.Identifier.ValueText))
                    {
                        continue;
                    }

                    var referencedSymbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol;
                    if (referencedSymbol is null)
                    {
                        continue;
                    }

                    var referencedKey = CreateSymbolKey(referencedSymbol);
                    if (!symbolsByKey.TryGetValue(referencedKey, out var state))
                    {
                        continue;
                    }

                    var enclosingSymbol = semanticModel.GetEnclosingSymbol(name.SpanStart, cancellationToken);
                    if (IsSelfReference(enclosingSymbol, state.Symbol, referencedKey))
                    {
                        state.SelfReferenceCount++;
                        continue;
                    }

                    var isTestReference = testProjectIds.Contains(document.Project.Id);
                    if (isTestReference)
                    {
                        state.TestReferenceCount++;
                    }
                    else
                    {
                        state.ProductionReferenceCount++;
                    }

                    if (state.ReferenceExamples.Count < referenceExampleLimit)
                    {
                        state.ReferenceExamples.Add(new ReferenceDescriptor(
                            isTestReference ? "test-project" : "production-project",
                            await CreatePositionAsync(document, name.GetLocation(), cancellationToken).ConfigureAwait(false)));
                    }
                }
            }
        }

        return scannedDocuments;
    }

    /// <summary>
    /// Returns a stable cross-compilation symbol key so references from consuming projects map to source declarations.
    /// </summary>
    private static string CreateSymbolKey(ISymbol symbol)
    {
        var normalized = NormalizeAuditedSymbol(symbol);
        return $"{normalized.ContainingAssembly?.Identity.Name}|{SymbolResolver.GetStableId(normalized)}";
    }

    /// <summary>
    /// Normalizes constructed and accessor symbols to the declaration represented by an audit candidate.
    /// </summary>
    private static ISymbol NormalizeAuditedSymbol(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method when method.AssociatedSymbol is not null => NormalizeAuditedSymbol(method.AssociatedSymbol),
        IMethodSymbol method => NormalizeMethod(method),
        INamedTypeSymbol type => type.OriginalDefinition,
        IPropertySymbol property => property.OriginalDefinition,
        IFieldSymbol field => field.OriginalDefinition,
        IEventSymbol @event => @event.OriginalDefinition,
        _ => symbol.OriginalDefinition
    };

    /// <summary>
    /// Separates recursion and self-contained type/member references from externally reachable usage.
    /// </summary>
    private static bool IsSelfReference(ISymbol? enclosingSymbol, ISymbol candidate, string candidateKey)
    {
        if (enclosingSymbol is null)
        {
            return false;
        }

        if (CreateSymbolKey(enclosingSymbol).Equals(candidateKey, StringComparison.Ordinal))
        {
            return true;
        }

        return candidate is INamedTypeSymbol candidateType &&
               enclosingSymbol.ContainingType is not null &&
               SymbolEqualityComparer.Default.Equals(enclosingSymbol.ContainingType.OriginalDefinition, candidateType.OriginalDefinition);
    }

    /// <summary>
    /// Normalizes reduced extension, partial, and constructed generic methods to their declarations.
    /// </summary>
    private static IMethodSymbol NormalizeMethod(IMethodSymbol method)
    {
        var normalized = method.ReducedFrom ?? method;
        normalized = normalized.PartialDefinitionPart ?? normalized.PartialImplementationPart ?? normalized;
        return normalized.OriginalDefinition;
    }

    /// <summary>
    /// Enumerates the declaration kinds supported by unused-symbol analysis while preserving compiler identity.
    /// </summary>
    private static IEnumerable<ISymbol> EnumerateAuditableDeclarations(
        SyntaxNode root,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var node in root.DescendantNodes())
        {
            ISymbol? symbol = node switch
            {
                BaseTypeDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                DelegateDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                MethodDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                PropertyDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                EventDeclarationSyntax declaration => semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                VariableDeclaratorSyntax declaration when declaration.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax =>
                    semanticModel.GetDeclaredSymbol(declaration, cancellationToken),
                _ => null
            };
            if (symbol is not null)
            {
                yield return symbol;
            }
        }
    }

    /// <summary>
    /// Identifies declarations whose apparent lack of references is unsafe or meaningless for removal analysis.
    /// </summary>
    private static string? GetSymbolExclusion(
        ISymbol symbol,
        IMethodSymbol? entryPoint,
        bool generatedDocument)
    {
        if (generatedDocument || HasGeneratedCodeAttribute(symbol))
        {
            return "generated-code";
        }

        if (symbol.IsImplicitlyDeclared)
        {
            return "implicitly-declared";
        }

        if (HasRuntimeDiscoveryAttribute(symbol))
        {
            return "runtime-discovered-attribute";
        }

        return symbol switch
        {
            IMethodSymbol method => GetMethodExclusion(method, entryPoint),
            IPropertySymbol property when property.IsOverride || property.IsAbstract || property.IsVirtual => "override-or-contract-member",
            IPropertySymbol property when ImplementsInterfaceMember(property) => "interface-implementation",
            IEventSymbol @event when @event.IsOverride || @event.IsAbstract || @event.IsVirtual => "override-or-contract-member",
            IEventSymbol @event when ImplementsInterfaceMember(@event) => "interface-implementation",
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum-member",
            INamedTypeSymbol { TypeKind: TypeKind.Interface or TypeKind.Delegate } => "contract-type",
            _ => null
        };
    }

    /// <summary>
    /// Applies method-specific exclusions after common generated and runtime-discovery checks.
    /// </summary>
    private static string? GetMethodExclusion(IMethodSymbol method, IMethodSymbol? entryPoint)
    {

        if (method.MethodKind != MethodKind.Ordinary || method.IsImplicitlyDeclared)
        {
            return "non-ordinary-method";
        }

        if (entryPoint is not null && SymbolEqualityComparer.Default.Equals(method, entryPoint))
        {
            return "application-entry-point";
        }

        if (method.IsOverride)
        {
            return "override";
        }

        if (method.IsAbstract || method.IsVirtual)
        {
            return "virtual-or-abstract-contract";
        }

        if (method.IsExtern)
        {
            return "external-entry-point";
        }

        if (method.PartialDefinitionPart is not null || method.PartialImplementationPart is not null || method.IsPartialDefinition)
        {
            return "partial-method";
        }

        if (ImplementsInterfaceMember(method))
        {
            return "interface-implementation";
        }

        if (IsMvcOrRazorAction(method))
        {
            return "mvc-or-razor-action";
        }

        if (IsConventionEntryPoint(method))
        {
            return "runtime-convention-entry-point";
        }

        return null;
    }

    /// <summary>
    /// Detects explicit and implicit interface implementations without relying on textual naming.
    /// </summary>
    private static bool ImplementsInterfaceMember(IMethodSymbol method)
    {
        if (method.ExplicitInterfaceImplementations.Length > 0)
        {
            return true;
        }

        foreach (var interfaceType in method.ContainingType.AllInterfaces)
        {
            foreach (var interfaceMethod in interfaceType.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                var implementation = method.ContainingType.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol;
                if (implementation is not null &&
                    SymbolEqualityComparer.Default.Equals(NormalizeMethod(implementation), NormalizeMethod(method)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Detects property or event implementations through Roslyn's interface-member mapping.
    /// </summary>
    private static bool ImplementsInterfaceMember(ISymbol member)
    {
        if (member is IMethodSymbol method)
        {
            return ImplementsInterfaceMember(method);
        }

        if (member.ContainingType is null)
        {
            return false;
        }

        return member.ContainingType.AllInterfaces
            .SelectMany(interfaceType => interfaceType.GetMembers(member.Name))
            .Any(contract => SymbolEqualityComparer.Default.Equals(
                member.ContainingType.FindImplementationForInterfaceMember(contract), member));
    }

    /// <summary>
    /// Excludes public controller and page-handler methods that frameworks discover without normal call sites.
    /// </summary>
    private static bool IsMvcOrRazorAction(IMethodSymbol method)
    {
        if (method.DeclaredAccessibility != Accessibility.Public || method.IsStatic || HasAttribute(method, "NonActionAttribute"))
        {
            return false;
        }

        for (var type = method.ContainingType; type is not null; type = type.BaseType)
        {
            var metadataName = SymbolResolver.GetMetadataName(type);
            if (metadataName is "Microsoft.AspNetCore.Mvc.Controller" or
                "Microsoft.AspNetCore.Mvc.ControllerBase" or
                "Microsoft.AspNetCore.Mvc.RazorPages.PageModel" or
                "System.Web.Mvc.Controller")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects attributes commonly used by test runners, serializers, RPC systems, jobs, and framework dispatchers.
    /// </summary>
    private static bool HasRuntimeDiscoveryAttribute(ISymbol symbol)
    {
        return symbol.GetAttributes().Concat(symbol.ContainingType?.GetAttributes() ?? []).Any(attribute =>
        {
            var name = attribute.AttributeClass?.Name ?? string.Empty;
            var metadataName = attribute.AttributeClass is null
                ? string.Empty
                : SymbolResolver.GetMetadataName(attribute.AttributeClass) ?? string.Empty;
            return RuntimeAttributeNameFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase) ||
                metadataName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        });
    }

    /// <summary>
    /// Detects generated declarations even when generated code appears outside the conventional obj folder.
    /// </summary>
    private static bool HasGeneratedCodeAttribute(ISymbol symbol)
    {
        return symbol.GetAttributes()
                   .Concat(symbol.ContainingType?.GetAttributes() ?? [])
                   .Any(attribute => attribute.AttributeClass?.Name is "GeneratedCodeAttribute" or "CompilerGeneratedAttribute");
    }

    /// <summary>
    /// Detects known convention-based entry points that can be invoked without a C# call expression.
    /// </summary>
    private static bool IsConventionEntryPoint(IMethodSymbol method)
    {
        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
        {
            return false;
        }

        return ConventionEntryPointNames.Contains(method.Name) ||
               method.Name.StartsWith("OnGet", StringComparison.Ordinal) ||
               method.Name.StartsWith("OnPost", StringComparison.Ordinal) ||
               method.Name.StartsWith("OnPut", StringComparison.Ordinal) ||
               method.Name.StartsWith("OnDelete", StringComparison.Ordinal) ||
               method.Name.StartsWith("OnPatch", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns review flags for candidates that can still be invoked outside the loaded source graph.
    /// </summary>
    private static IReadOnlyList<string> GetSymbolRiskFlags(ISymbol symbol)
    {
        var flags = new List<string>();
        if (symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
        {
            flags.Add("externally-visible");
        }

        if (symbol.ContainingType?.DeclaredAccessibility == Accessibility.Public)
        {
            flags.Add("public-containing-type");
        }

        if (symbol.IsStatic)
        {
            flags.Add("static-symbol");
        }

        flags.Add(symbol.Kind.ToString().ToLowerInvariant());
        flags.Add("runtime-discovery-not-ruled-out");
        return flags;
    }

    /// <summary>
    /// Determines whether a project is test code from its name or evaluated project-file conventions.
    /// </summary>
    private static bool IsTestProject(Project project)
    {
        if (project.Name.Equals("Tests", StringComparison.OrdinalIgnoreCase) ||
            project.Name.Equals("Test", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith("-Tests", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith("-Test", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith("_Tests", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith("_Test", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (project.FilePath is null || !File.Exists(project.FilePath))
        {
            return false;
        }

        try
        {
            var projectDocument = XDocument.Load(project.FilePath, LoadOptions.None);
            var isTestProperty = projectDocument.Descendants().Any(element =>
                (element.Name.LocalName is "IsTestProject" or "TestProject") &&
                element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
            var hasTestPackage = projectDocument.Descendants().Any(element =>
                element.Name.LocalName == "PackageReference" &&
                IsTestPackage((string?)element.Attribute("Include") ?? (string?)element.Attribute("Update")));

            return isTestProperty || hasTestPackage;
        }
        catch (IOException)
        {
            return false;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Recognizes common test SDK and framework package IDs.
    /// </summary>
    private static bool IsTestPackage(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        return packageId.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) ||
               packageId.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
               packageId.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) ||
               packageId.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detects conventional generated file names and intermediate output folders.
    /// </summary>
    private static bool IsGeneratedDocument(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalized = filePath.Replace('/', '\\');
        var fileName = Path.GetFileName(normalized);
        return normalized.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks one attribute by CLR metadata name suffix.
    /// </summary>
    private static bool HasAttribute(IMethodSymbol method, string attributeName)
    {
        return method.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.Name.Equals(attributeName, StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Increments a stable exclusion-reason counter.
    /// </summary>
    private static void Increment(IDictionary<string, int> counts, string key)
    {
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    /// <summary>
    /// Accumulates exact semantic reference counts and bounded examples for one audited symbol.
    /// </summary>
    private sealed class UnusedSymbolAuditState
    {
        public UnusedSymbolAuditState(ISymbol symbol, Document document)
        {
            Symbol = symbol;
            Document = document;
        }

        public ISymbol Symbol { get; }

        public Document Document { get; }

        public int ProductionReferenceCount { get; set; }

        public int TestReferenceCount { get; set; }

        public int SelfReferenceCount { get; set; }

        public List<ReferenceDescriptor> ReferenceExamples { get; } = [];
    }
}
