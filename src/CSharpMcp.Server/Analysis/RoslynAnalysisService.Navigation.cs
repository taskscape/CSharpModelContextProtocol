using CSharpMcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpMcp.Analysis;

/// <summary>
/// Provides position, binding, member-surface, and inheritance queries.
/// </summary>
internal sealed partial class RoslynAnalysisService
{
    /// <summary>
    /// Resolves the symbol and type information at a one-based source position.
    /// </summary>
    public async Task<object> GetSymbolAtPositionAsync(
        string workspacePath,
        string documentPath,
        int line,
        int column,
        bool includeCandidates,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var document = FindDocument(snapshot.Solution, documentPath);
        var (model, root, position) = await GetSemanticPositionAsync(document, line, column, cancellationToken).ConfigureAwait(false);
        var token = root.FindToken(position, findInsideTrivia: true);
        var nodes = token.Parent?.AncestorsAndSelf() ?? [];

        ISymbol? boundSymbol = null;
        ISymbol? declaredSymbol = null;
        SymbolInfo symbolInfo = default;
        SyntaxNode? selectedNode = null;
        foreach (var node in nodes)
        {
            var currentInfo = model.GetSymbolInfo(node, cancellationToken);
            var currentDeclared = model.GetDeclaredSymbol(node, cancellationToken);
            if (currentInfo.Symbol is not null || currentInfo.CandidateSymbols.Length > 0 || currentDeclared is not null)
            {
                boundSymbol = currentInfo.Symbol;
                declaredSymbol = currentDeclared;
                symbolInfo = currentInfo;
                selectedNode = node;
                break;
            }
        }

        selectedNode ??= token.Parent ?? root;
        var typeInfo = model.GetTypeInfo(selectedNode, cancellationToken);
        var enclosing = model.GetEnclosingSymbol(position, cancellationToken);
        var candidateLimit = BoundLimit(maxCandidates);
        var candidates = includeCandidates
            ? symbolInfo.CandidateSymbols.Take(candidateLimit).ToArray()
            : [];
        var candidateDescriptors = new List<SymbolDescriptor>(candidates.Length);
        foreach (var candidate in candidates)
        {
            candidateDescriptors.Add(await DescribeSymbolAsync(candidate, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        var sourceLocation = Location.Create(root.SyntaxTree, token.Span);
        var data = new
        {
            document = new { document.Project.Name, document.FilePath, line, column },
            syntax = new
            {
                kind = selectedNode.Kind().ToString(),
                token = token.Text,
                spanStart = selectedNode.SpanStart,
                spanLength = selectedNode.Span.Length,
                location = await CreatePositionAsync(document, sourceLocation, cancellationToken).ConfigureAwait(false)
            },
            symbol = boundSymbol is null
                ? null
                : await DescribeSymbolAsync(boundSymbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            declaredSymbol = declaredSymbol is null
                ? null
                : await DescribeSymbolAsync(declaredSymbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            enclosingSymbol = enclosing is null
                ? null
                : await DescribeSymbolAsync(enclosing, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            type = typeInfo.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            convertedType = typeInfo.ConvertedType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            candidateReason = symbolInfo.CandidateReason.ToString(),
            candidates = candidateDescriptors
        };

        return Wrap(data, candidateDescriptors.Count, includeCandidates && symbolInfo.CandidateSymbols.Length > candidateDescriptors.Count, snapshot);
    }

    /// <summary>
    /// Returns the compiler-selected callable and argument bindings at an invocation position.
    /// </summary>
    public async Task<object> GetInvocationBindingAsync(
        string workspacePath,
        string documentPath,
        int line,
        int column,
        int maxCandidates,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var document = FindDocument(snapshot.Solution, documentPath);
        var (model, root, position) = await GetSemanticPositionAsync(document, line, column, cancellationToken).ConfigureAwait(false);
        var token = root.FindToken(position, findInsideTrivia: true);
        var operation = FindCallableOperation(model, token.Parent, cancellationToken)
            ?? throw new ArgumentException("The requested position is not inside a supported invocation, construction, indexer, or operator expression.", nameof(line));

        var target = GetOperationTarget(operation);
        var symbolInfo = model.GetSymbolInfo(operation.Syntax, cancellationToken);
        var candidateLimit = BoundLimit(maxCandidates);
        var candidates = symbolInfo.CandidateSymbols.Take(candidateLimit).ToArray();
        var candidateDescriptors = new List<SymbolDescriptor>(candidates.Length);
        foreach (var candidate in candidates)
        {
            candidateDescriptors.Add(await DescribeSymbolAsync(candidate, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        var arguments = GetOperationArguments(operation)
            .Select(argument => new
            {
                syntax = Trim(argument.Syntax.ToString(), MaximumExcerptLength),
                argumentKind = argument.ArgumentKind.ToString(),
                parameter = argument.Parameter is null
                    ? null
                    : new
                    {
                        argument.Parameter.Name,
                        ordinal = argument.Parameter.Ordinal,
                        type = argument.Parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        refKind = argument.Parameter.RefKind.ToString(),
                        argument.Parameter.IsParams,
                        argument.Parameter.IsOptional
                    },
                valueType = argument.Value.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                constantValue = argument.Value.ConstantValue.HasValue ? argument.Value.ConstantValue.Value?.ToString() : null,
                inConversion = DescribeConversion(argument.InConversion),
                outConversion = DescribeConversion(argument.OutConversion)
            })
            .ToArray();
        var receiverType = operation switch
        {
            IInvocationOperation invocation => invocation.Instance?.Type,
            IPropertyReferenceOperation property => property.Instance?.Type,
            _ => null
        };
        var method = target as IMethodSymbol;
        var localDiagnostics = model.GetDiagnostics(operation.Syntax.Span, cancellationToken)
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
            .Select(diagnostic => new
            {
                diagnostic.Id,
                severity = diagnostic.Severity.ToString(),
                message = diagnostic.GetMessage()
            })
            .Take(50)
            .ToArray();

        var data = new
        {
            operationKind = operation.Kind.ToString(),
            expression = Trim(operation.Syntax.ToString(), MaximumExcerptLength),
            target = target is null
                ? null
                : await DescribeSymbolAsync(target, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            receiverType = receiverType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            isExtensionMethod = method?.IsExtensionMethod ?? false,
            reducedFrom = method?.ReducedFrom is null
                ? null
                : await DescribeSymbolAsync(method.ReducedFrom, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            typeArguments = method?.TypeArguments.Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).ToArray() ?? [],
            arguments,
            candidateReason = symbolInfo.CandidateReason.ToString(),
            candidates = candidateDescriptors,
            diagnostics = localDiagnostics
        };

        return Wrap(data, arguments.Length + candidateDescriptors.Count, symbolInfo.CandidateSymbols.Length > candidateDescriptors.Count, snapshot);
    }

    /// <summary>
    /// Enumerates a named type's compiler-visible member surface.
    /// </summary>
    public async Task<object> GetMemberSurfaceAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        string? memberKinds,
        string accessibility,
        bool includeInherited,
        bool includeExplicitInterfaceImplementations,
        string? memberName,
        bool includeApplicableExtensionMethods,
        string mode,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(snapshot.Solution, symbolQuery, projectName, cancellationToken).ConfigureAwait(false);
        if (symbol is not INamedTypeSymbol type)
        {
            throw new ArgumentException("member_surface requires a named type symbol.", nameof(symbolQuery));
        }

        var normalizedMode = mode.Trim().ToLowerInvariant();
        if (normalizedMode is not ("all" or "constructors" or "overloads" or "operators" or "extensions"))
        {
            throw new ArgumentException("Mode must be all, constructors, overloads, operators, or extensions.", nameof(mode));
        }

        if (normalizedMode == "overloads" && string.IsNullOrWhiteSpace(memberName))
        {
            throw new ArgumentException("The overloads mode requires memberName.", nameof(memberName));
        }

        var kinds = ParseSymbolKinds(memberKinds);
        var limit = BoundLimit(maxResults);
        var declaredCandidates = EnumerateMemberSurface(type, includeInherited)
            .Select(item => (item.Symbol, item.DeclaringType,
                Relation: SymbolEqualityComparer.Default.Equals(item.DeclaringType, type) ? "declared" : "inherited"));
        var includeExtensions = includeApplicableExtensionMethods || normalizedMode == "extensions";
        var extensionCandidates = includeExtensions
            ? await FindApplicableExtensionMethodsAsync(snapshot.Solution, type, cancellationToken).ConfigureAwait(false)
            : [];
        var candidates = declaredCandidates.Concat(extensionCandidates)
            .Where(item => MatchesMemberSurfaceMode(item.Symbol, item.Relation, normalizedMode, memberName))
            .Where(item => kinds.Count == 0 || kinds.Contains(item.Symbol.Kind))
            .Where(item => string.IsNullOrWhiteSpace(memberName) || item.Symbol.Name.Equals(memberName, StringComparison.Ordinal))
            .Where(item => MatchesAccessibility(item.Symbol.DeclaredAccessibility, accessibility))
            .Where(item => includeExplicitInterfaceImplementations || !IsExplicitInterfaceImplementation(item.Symbol))
            .GroupBy(item => $"{SymbolResolver.GetStableId(item.Symbol)}|{SymbolResolver.GetStableId(item.DeclaringType)}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Symbol.Kind)
            .ThenBy(item => item.Symbol.Name, StringComparer.Ordinal)
            .ToArray();
        var members = new List<object>();
        foreach (var item in candidates.Take(limit))
        {
            var interfaceContracts = item.Symbol.ContainingType.AllInterfaces
                .SelectMany(contract => contract.GetMembers())
                .Where(contractMember => SymbolEqualityComparer.Default.Equals(
                    item.Symbol.ContainingType.FindImplementationForInterfaceMember(contractMember),
                    item.Symbol))
                .ToArray();
            var interfaceDescriptors = new List<SymbolDescriptor>(interfaceContracts.Length);
            foreach (var contract in interfaceContracts)
            {
                interfaceDescriptors.Add(await DescribeSymbolAsync(contract, snapshot.Solution, cancellationToken).ConfigureAwait(false));
            }

            var overridden = GetOverriddenSymbol(item.Symbol);
            members.Add(new
            {
                symbol = await DescribeSymbolAsync(item.Symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                declaredBy = await DescribeSymbolAsync(item.DeclaringType, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                relation = item.Relation,
                accessibility = item.Symbol.DeclaredAccessibility.ToString(),
                modifiers = GetModifiers(item.Symbol),
                methodKind = (item.Symbol as IMethodSymbol)?.MethodKind.ToString(),
                parameters = (item.Symbol as IMethodSymbol)?.Parameters.Select(DescribeParameter).ToArray(),
                isExplicitInterfaceImplementation = IsExplicitInterfaceImplementation(item.Symbol),
                overriddenSymbol = overridden is null
                    ? null
                    : await DescribeSymbolAsync(overridden, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                implementedInterfaceMembers = interfaceDescriptors
            });
        }

        var data = new
        {
            type = await DescribeSymbolAsync(type, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            accessibilityFilter = accessibility,
            includeInherited,
            includeExplicitInterfaceImplementations,
            mode = normalizedMode,
            memberNameFilter = memberName,
            includeApplicableExtensionMethods = includeExtensions,
            members
        };
        return Wrap(data, members.Count, candidates.Length > members.Count, snapshot);
    }

    /// <summary>
    /// Finds source and metadata extension methods that Roslyn can reduce against the requested receiver type.
    /// </summary>
    private static async Task<IReadOnlyList<(ISymbol Symbol, INamedTypeSymbol DeclaringType, string Relation)>> FindApplicableExtensionMethodsAsync(
        Solution solution,
        INamedTypeSymbol receiverType,
        CancellationToken cancellationToken)
    {
        const int maximumMethodsInspected = 100_000;
        var inspected = 0;
        var results = new Dictionary<string, (ISymbol Symbol, INamedTypeSymbol DeclaringType, string Relation)>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            var assemblies = compilation.SourceModule.ReferencedAssemblySymbols
                .Prepend(compilation.Assembly)
                .Distinct<IAssemblySymbol>(SymbolEqualityComparer.Default);
            foreach (var candidateType in assemblies.SelectMany(assembly => EnumerateNamespaceTypes(assembly.GlobalNamespace)))
            {
                if (!candidateType.IsStatic)
                {
                    continue;
                }

                foreach (var method in candidateType.GetMembers().OfType<IMethodSymbol>())
                {
                    if (++inspected > maximumMethodsInspected)
                    {
                        return results.Values.ToArray();
                    }

                    if (!method.IsExtensionMethod || method.ReduceExtensionMethod(receiverType) is null)
                    {
                        continue;
                    }

                    var relation = method.Locations.Any(static location => location.IsInSource)
                        ? "applicable-source-extension"
                        : "applicable-metadata-extension";
                    results.TryAdd(SymbolResolver.GetStableId(method), (method, method.ContainingType, relation));
                }
            }
        }

        return results.Values.ToArray();
    }

    /// <summary>
    /// Enumerates namespace and nested types from a compilation without loading source text.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateNamespaceTypes(INamespaceSymbol rootNamespace)
    {
        foreach (var member in rootNamespace.GetMembers())
        {
            if (member is INamespaceSymbol childNamespace)
            {
                foreach (var nestedType in EnumerateNamespaceTypes(childNamespace))
                {
                    yield return nestedType;
                }
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var nestedType in EnumerateTypeAndNestedTypes(type))
                {
                    yield return nestedType;
                }
            }
        }
    }

    /// <summary>
    /// Enumerates a type and its nested types for metadata-aware member discovery.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> EnumerateTypeAndNestedTypes(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var candidate in EnumerateTypeAndNestedTypes(nested))
            {
                yield return candidate;
            }
        }
    }

    /// <summary>
    /// Applies the explicit member-surface mode before more detailed filters run.
    /// </summary>
    private static bool MatchesMemberSurfaceMode(ISymbol symbol, string relation, string mode, string? memberName) => mode switch
    {
        "constructors" => symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor },
        "overloads" => symbol is IMethodSymbol && symbol.Name.Equals(memberName, StringComparison.Ordinal),
        "operators" => symbol is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion },
        "extensions" => relation.Contains("extension", StringComparison.Ordinal),
        _ => true
    };

    /// <summary>
    /// Builds a bounded inheritance and implementation graph around one named type.
    /// </summary>
    public async Task<object> GetInheritanceGraphAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        string direction,
        int maxDepth,
        bool includeInterfaces,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var normalizedDirection = direction.Trim().ToLowerInvariant();
        if (normalizedDirection is not ("ancestors" or "descendants" or "both"))
        {
            throw new ArgumentException("Direction must be ancestors, descendants, or both.", nameof(direction));
        }

        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(snapshot.Solution, symbolQuery, projectName, cancellationToken).ConfigureAwait(false);
        if (symbol is not INamedTypeSymbol rootType)
        {
            throw new ArgumentException("inheritance_graph requires a named type symbol.", nameof(symbolQuery));
        }

        var depthLimit = Math.Clamp(maxDepth, 1, 5);
        var resultLimit = BoundLimit(maxResults);
        var edges = new List<(INamedTypeSymbol From, INamedTypeSymbol To, string Relation, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { SymbolResolver.GetStableId(rootType) };
        var queue = new Queue<(INamedTypeSymbol Type, int Depth)>();
        queue.Enqueue((rootType, 0));

        while (queue.Count > 0 && edges.Count < resultLimit)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= depthLimit)
            {
                continue;
            }

            if (normalizedDirection is "ancestors" or "both")
            {
                foreach (var ancestor in GetDirectAncestors(current, includeInterfaces))
                {
                    edges.Add((current, ancestor.Type, ancestor.Relation, depth + 1));
                    if (visited.Add(SymbolResolver.GetStableId(ancestor.Type)))
                    {
                        queue.Enqueue((ancestor.Type, depth + 1));
                    }

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

            if (normalizedDirection is "descendants" or "both")
            {
                var descendants = await GetDirectDescendantsAsync(current, snapshot.Solution, includeInterfaces, cancellationToken).ConfigureAwait(false);
                foreach (var descendant in descendants)
                {
                    edges.Add((descendant.Type, current, descendant.Relation, depth + 1));
                    if (visited.Add(SymbolResolver.GetStableId(descendant.Type)))
                    {
                        queue.Enqueue((descendant.Type, depth + 1));
                    }

                    if (edges.Count >= resultLimit)
                    {
                        break;
                    }
                }
            }
        }

        var describedEdges = new List<object>(edges.Count);
        foreach (var edge in edges)
        {
            describedEdges.Add(new
            {
                from = await DescribeSymbolAsync(edge.From, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                to = await DescribeSymbolAsync(edge.To, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                relation = edge.Relation,
                depth = edge.Depth
            });
        }

        var data = new
        {
            root = await DescribeSymbolAsync(rootType, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            direction = normalizedDirection,
            maxDepth = depthLimit,
            includeInterfaces,
            edges = describedEdges
        };
        return Wrap(data, describedEdges.Count, queue.Count > 0 || edges.Count >= resultLimit, snapshot);
    }

    private static Document FindDocument(Solution solution, string documentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        var fullPath = Path.GetFullPath(documentPath);
        return solution.Projects
                   .SelectMany(project => project.Documents)
                   .FirstOrDefault(document => document.FilePath is not null &&
                                               Path.GetFullPath(document.FilePath).Equals(fullPath, StringComparison.OrdinalIgnoreCase))
               ?? throw new FileNotFoundException("The document is not part of the loaded workspace.", fullPath);
    }

    private static async Task<(SemanticModel Model, SyntaxNode Root, int Position)> GetSemanticPositionAsync(
        Document document,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (line < 1 || line > text.Lines.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(line), $"Line must be between 1 and {text.Lines.Count}.");
        }

        var sourceLine = text.Lines[line - 1];
        if (column < 1 || column > sourceLine.Span.Length + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(column), $"Column must be between 1 and {sourceLine.Span.Length + 1}.");
        }

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn did not produce a semantic model for the document.");
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Roslyn did not produce a syntax root for the document.");
        return (model, root, sourceLine.Start + column - 1);
    }

    private static IOperation? FindCallableOperation(SemanticModel model, SyntaxNode? node, CancellationToken cancellationToken)
    {
        foreach (var candidate in node?.AncestorsAndSelf() ?? [])
        {
            var operation = model.GetOperation(candidate, cancellationToken);
            if (operation is IInvocationOperation or IObjectCreationOperation or IPropertyReferenceOperation { Property.IsIndexer: true } or IBinaryOperation or IUnaryOperation)
            {
                return operation;
            }
        }

        return null;
    }

    private static ISymbol? GetOperationTarget(IOperation operation)
    {
        return operation switch
        {
            IInvocationOperation invocation => invocation.TargetMethod,
            IObjectCreationOperation creation => creation.Constructor,
            IPropertyReferenceOperation property => property.Property,
            IBinaryOperation binary => binary.OperatorMethod,
            IUnaryOperation unary => unary.OperatorMethod,
            _ => null
        };
    }

    private static IEnumerable<IArgumentOperation> GetOperationArguments(IOperation operation)
    {
        return operation switch
        {
            IInvocationOperation invocation => invocation.Arguments,
            IObjectCreationOperation creation => creation.Arguments,
            IPropertyReferenceOperation property => property.Arguments,
            _ => []
        };
    }

    private static object DescribeConversion(CommonConversion conversion)
    {
        return new
        {
            conversion.Exists,
            conversion.IsIdentity,
            conversion.IsImplicit,
            conversion.IsNumeric,
            conversion.IsReference,
            conversion.IsUserDefined,
            method = conversion.MethodSymbol is null ? null : SymbolResolver.GetStableId(conversion.MethodSymbol)
        };
    }

    private static HashSet<SymbolKind> ParseSymbolKinds(string? memberKinds)
    {
        if (string.IsNullOrWhiteSpace(memberKinds))
        {
            return [];
        }

        var kinds = new HashSet<SymbolKind>();
        foreach (var value in memberKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<SymbolKind>(value, ignoreCase: true, out var kind))
            {
                throw new ArgumentException($"Unknown Roslyn SymbolKind '{value}'.", nameof(memberKinds));
            }

            kinds.Add(kind);
        }

        return kinds;
    }

    private static IEnumerable<(ISymbol Symbol, INamedTypeSymbol DeclaringType)> EnumerateMemberSurface(
        INamedTypeSymbol type,
        bool includeInherited)
    {
        for (var current = type; current is not null; current = includeInherited ? current.BaseType : null)
        {
            foreach (var member in current.GetMembers())
            {
                if (!SymbolEqualityComparer.Default.Equals(current, type) && member is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor })
                {
                    continue;
                }

                yield return (member, current);
            }
        }

        if (!includeInherited)
        {
            yield break;
        }

        foreach (var contract in type.AllInterfaces)
        {
            foreach (var member in contract.GetMembers())
            {
                yield return (member, contract);
            }
        }
    }

    private static bool MatchesAccessibility(Accessibility declaredAccessibility, string requested)
    {
        return requested.Trim().ToLowerInvariant() switch
        {
            "all" => true,
            "public" => declaredAccessibility == Accessibility.Public,
            "public-or-protected" => declaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal,
            "non-private" => declaredAccessibility is not Accessibility.Private,
            _ => throw new ArgumentException("Accessibility must be all, public, public-or-protected, or non-private.", nameof(requested))
        };
    }

    private static bool IsExplicitInterfaceImplementation(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0,
            IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
            IEventSymbol @event => @event.ExplicitInterfaceImplementations.Length > 0,
            _ => false
        };
    }

    private static ISymbol? GetOverriddenSymbol(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null
        };
    }

    private static IEnumerable<(INamedTypeSymbol Type, string Relation)> GetDirectAncestors(
        INamedTypeSymbol type,
        bool includeInterfaces)
    {
        if (type.BaseType is not null)
        {
            yield return (type.BaseType, "base-type");
        }

        if (!includeInterfaces)
        {
            yield break;
        }

        foreach (var contract in type.Interfaces)
        {
            yield return (contract, type.TypeKind == TypeKind.Interface ? "base-interface" : "implements");
        }
    }

    private static async Task<IReadOnlyList<(INamedTypeSymbol Type, string Relation)>> GetDirectDescendantsAsync(
        INamedTypeSymbol type,
        Solution solution,
        bool includeInterfaces,
        CancellationToken cancellationToken)
    {
        var results = new List<(INamedTypeSymbol Type, string Relation)>();
        if (type.TypeKind == TypeKind.Interface)
        {
            if (includeInterfaces)
            {
                var derivedInterfaces = await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                results.AddRange(derivedInterfaces.Select(derived => (derived, "base-interface")));
            }

            var implementations = await SymbolFinder.FindImplementationsAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            results.AddRange(implementations.OfType<INamedTypeSymbol>().Select(implementation => (implementation, "implements")));
        }
        else
        {
            var derivedTypes = await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            results.AddRange(derivedTypes.Select(derived => (derived, "base-type")));
        }

        return results
            .GroupBy(item => SymbolResolver.GetStableId(item.Type), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }
}
