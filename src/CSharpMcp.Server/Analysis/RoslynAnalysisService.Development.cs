using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpMcp.Analysis;

/// <summary>
/// Provides source, event, attribute, dependency-injection, and construction intelligence.
/// </summary>
internal sealed partial class RoslynAnalysisService
{
    /// <summary>
    /// Returns bounded original source declarations for a batch of symbols without failing the whole batch.
    /// </summary>
    public async Task<object> GetSymbolSourceAsync(
        string workspacePath,
        string[] symbolQueries,
        string? projectName,
        bool includeBody,
        int maxLines,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbolQueries);
        if (symbolQueries.Length is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolQueries), "Provide between 1 and 50 symbol queries.");
        }

        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var lineLimit = Math.Clamp(maxLines, 1, 500);
        var characterLimit = Math.Clamp(maxCharacters, 200, 50_000);
        var results = new List<object>();
        foreach (var query in symbolQueries)
        {
            try
            {
                var candidates = await SymbolResolver.ResolveManyAsync(snapshot.Solution, query, projectName, cancellationToken).ConfigureAwait(false);
                if (candidates.Count == 0)
                {
                    results.Add(new { query, status = "notFound" });
                    continue;
                }

                if (candidates.Count > 1)
                {
                    results.Add(new
                    {
                        query,
                        status = "ambiguous",
                        candidates = candidates.Take(20).Select(SymbolResolver.GetStableId).ToArray()
                    });
                    continue;
                }

                var symbol = candidates[0];
                if (symbol.DeclaringSyntaxReferences.Length == 0)
                {
                    results.Add(new
                    {
                        query,
                        status = "metadata",
                        symbol = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false)
                    });
                    continue;
                }

                var declarations = new List<object>();
                foreach (var syntaxReference in symbol.DeclaringSyntaxReferences.Take(10))
                {
                    var syntax = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                    var document = snapshot.Solution.GetDocument(syntax.SyntaxTree);
                    if (document is null)
                    {
                        continue;
                    }

                    var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var sourceSpan = includeBody ? syntax.FullSpan : GetSignatureSpan(syntax);
                    var source = text.ToString(sourceSpan);
                    var lines = source.ReplaceLineEndings("\n").Split('\n');
                    var bounded = string.Join(Environment.NewLine, lines.Take(lineLimit));
                    var truncated = lines.Length > lineLimit || bounded.Length > characterLimit;
                    declarations.Add(new
                    {
                        location = await CreatePositionAsync(document, syntax.GetLocation(), cancellationToken).ConfigureAwait(false),
                        source = Trim(bounded, characterLimit),
                        includeBody,
                        truncated
                    });
                }

                results.Add(new
                {
                    query,
                    status = declarations.Count == 0 ? "unsupportedKind" : "ok",
                    symbol = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                    declarations
                });
            }
            catch (InvalidOperationException exception)
            {
                results.Add(new { query, status = "ambiguous", message = exception.Message });
            }
        }

        return Wrap(new { includeBody, maxLines = lineLimit, maxCharacters = characterLimit, items = results }, results.Count, false, snapshot);
    }

    /// <summary>
    /// Finds event subscriptions, unsubscriptions, and raises with resolved handlers when available.
    /// </summary>
    public async Task<object> GetEventFlowAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        string? actions,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(snapshot.Solution, symbolQuery, projectName, cancellationToken).ConfigureAwait(false);
        if (symbol is not IEventSymbol eventSymbol)
        {
            throw new ArgumentException("event_flow requires an event symbol.", nameof(symbolQuery));
        }

        var requestedActions = ParseFilters(actions, ["subscribe", "unsubscribe", "raise", "reference"], nameof(actions));
        var references = await SymbolFinder.FindReferencesAsync(eventSymbol, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var candidates = references.SelectMany(group => group.Locations)
            .OrderBy(location => location.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.Location.SourceSpan.Start)
            .ToArray();
        var limit = BoundLimit(maxResults);
        var results = new List<object>();
        foreach (var reference in candidates)
        {
            var root = await reference.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await reference.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var node = root?.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is null || model is null)
            {
                continue;
            }

            var assignment = node.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault();
            var invocation = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault();
            var action = assignment?.Kind() switch
            {
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.AddAssignmentExpression => "subscribe",
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.SubtractAssignmentExpression => "unsubscribe",
                _ when invocation is not null && invocation.Expression.Span.Contains(node.Span) => "raise",
                _ => "reference"
            };
            if (requestedActions.Count > 0 && !requestedActions.Contains(action))
            {
                continue;
            }

            ISymbol? handler = assignment is null
                ? null
                : ResolveDelegateTarget(model.GetOperation(assignment.Right, cancellationToken));
            results.Add(new
            {
                action,
                handler = handler is null
                    ? null
                    : await DescribeSymbolAsync(handler, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                location = await CreatePositionAsync(reference.Document, reference.Location, cancellationToken).ConfigureAwait(false)
            });
            if (results.Count >= limit)
            {
                break;
            }
        }

        return Wrap(new
        {
            @event = await DescribeSymbolAsync(eventSymbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            actions = results,
            counts = results.GroupBy(item => (string)item.GetType().GetProperty("action")!.GetValue(item)!)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
        }, results.Count, candidates.Length > results.Count, snapshot);
    }

    /// <summary>
    /// Finds symbols decorated with one attribute and reports constructor arguments and named arguments.
    /// </summary>
    public async Task<object> GetAttributeUsageAsync(
        string workspacePath,
        string attributeQuery,
        string? projectName,
        string? targetKinds,
        bool includeInherited,
        bool includeMigrationGroups,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await ResolveNamedTypeIncludingMetadataAsync(
            snapshot.Solution, attributeQuery, projectName, cancellationToken).ConfigureAwait(false);
        if (symbol is not INamedTypeSymbol attributeType || !InheritsFrom(attributeType, "System.Attribute"))
        {
            throw new ArgumentException("attribute_usage requires an attribute type.", nameof(attributeQuery));
        }

        var kinds = ParseSymbolKinds(targetKinds);
        var references = await SymbolFinder.FindReferencesAsync(attributeType, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var locations = references.SelectMany(group => group.Locations)
            .OrderBy(location => location.Document.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(location => location.Location.SourceSpan.Start)
            .ToArray();
        var limit = BoundLimit(maxResults);
        var applications = new List<AttributeApplication>();
        foreach (var location in locations)
        {
            var root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var node = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
            var attribute = node?.AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault();
            if (attribute is null || model is null)
            {
                continue;
            }

            var target = FindAttributedSymbol(attribute, model, cancellationToken);
            if (target is null || kinds.Count > 0 && !kinds.Contains(target.Kind))
            {
                continue;
            }

            var arguments = attribute.ArgumentList?.Arguments.Select(argument => new AttributeArgumentDescriptor(
                argument.NameEquals?.Name.Identifier.ValueText ?? argument.NameColon?.Name.Identifier.ValueText,
                Trim(argument.Expression.ToString(), MaximumExcerptLength),
                model.GetTypeInfo(argument.Expression, cancellationToken).Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                model.GetConstantValue(argument.Expression, cancellationToken) is { HasValue: true } constant
                    ? constant.Value?.ToString()
                    : null)).ToArray() ?? [];
            var constructor = model.GetSymbolInfo(attribute, cancellationToken).Symbol as IMethodSymbol;
            applications.Add(new AttributeApplication(
                target,
                constructor,
                arguments,
                await CreatePositionAsync(location.Document, attribute.GetLocation(), cancellationToken).ConfigureAwait(false),
                InheritedFrom: null));
        }

        var directCount = applications.Count;
        if (includeInherited && IsInheritedAttribute(attributeType))
        {
            foreach (var direct in applications.ToArray())
            {
                var inheritedTargets = direct.Target switch
                {
                    INamedTypeSymbol namedType => (await SymbolFinder.FindDerivedClassesAsync(
                        namedType, snapshot.Solution, cancellationToken: cancellationToken).ConfigureAwait(false)).Cast<ISymbol>(),
                    IMethodSymbol or IPropertySymbol or IEventSymbol => await SymbolFinder.FindOverridesAsync(
                        direct.Target, snapshot.Solution, cancellationToken: cancellationToken).ConfigureAwait(false),
                    _ => []
                };
                foreach (var inheritedTarget in inheritedTargets)
                {
                    if (kinds.Count > 0 && !kinds.Contains(inheritedTarget.Kind))
                    {
                        continue;
                    }

                    var sourceLocation = inheritedTarget.Locations.FirstOrDefault(static item => item.IsInSource);
                    var document = sourceLocation?.SourceTree is null ? null : snapshot.Solution.GetDocument(sourceLocation.SourceTree);
                    var position = document is null || sourceLocation is null
                        ? null
                        : await CreatePositionAsync(document, sourceLocation, cancellationToken).ConfigureAwait(false);
                    applications.Add(new AttributeApplication(
                        inheritedTarget,
                        direct.Constructor,
                        direct.Arguments,
                        position,
                        direct.Target));
                }
            }
        }

        var deduplicated = applications
            .GroupBy(item => $"{SymbolResolver.GetStableId(item.Target)}|{SymbolResolver.GetStableId(item.InheritedFrom ?? item.Target)}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Location?.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Location?.Line)
            .ToArray();
        var results = new List<object>();
        foreach (var application in deduplicated.Take(limit))
        {
            results.Add(new
            {
                target = await DescribeSymbolAsync(application.Target, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                constructor = application.Constructor is null
                    ? null
                    : await DescribeSymbolAsync(application.Constructor, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                application.Arguments,
                application.Location,
                inherited = application.InheritedFrom is not null,
                inheritedFrom = application.InheritedFrom is null
                    ? null
                    : await DescribeSymbolAsync(application.InheritedFrom, snapshot.Solution, cancellationToken).ConfigureAwait(false)
            });
        }

        var isObsoleteAttribute = attributeType.ToDisplayString() == "System.ObsoleteAttribute" ||
            InheritsFrom(attributeType, "System.ObsoleteAttribute");
        var migrationGroups = includeMigrationGroups && isObsoleteAttribute
            ? applications.GroupBy(GetObsoleteMigrationKey)
                .Select(group => new
                {
                    group.Key.Message,
                    group.Key.IsError,
                    targetCount = group.Select(item => SymbolResolver.GetStableId(item.Target)).Distinct(StringComparer.Ordinal).Count(),
                    targets = group.Take(limit).Select(item => SymbolResolver.GetStableId(item.Target)).ToArray()
                })
                .OrderByDescending(group => group.IsError)
                .ThenBy(group => group.Message, StringComparer.Ordinal)
                .ToArray()
            : null;

        return Wrap(new
        {
            attribute = await DescribeSymbolAsync(attributeType, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            includeInherited,
            directCount,
            inheritedCount = applications.Count - directCount,
            usages = results,
            migrationGroups
        }, results.Count, deduplicated.Length > results.Count, snapshot);
    }

    /// <summary>
    /// Reads the inherited setting from AttributeUsageAttribute; the CLR default is inherited.
    /// </summary>
    private static bool IsInheritedAttribute(INamedTypeSymbol attributeType)
    {
        var usage = attributeType.GetAttributes().FirstOrDefault(item =>
            item.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute");
        return usage?.NamedArguments.FirstOrDefault(item => item.Key == nameof(AttributeUsageAttribute.Inherited)).Value.Value as bool? ?? true;
    }

    /// <summary>
    /// Resolves source attributes normally and falls back to compilation metadata for framework attributes.
    /// </summary>
    private static async Task<INamedTypeSymbol?> ResolveNamedTypeIncludingMetadataAsync(
        Solution solution,
        string query,
        string? projectName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SymbolResolver.ResolveSingleAsync(solution, query, projectName, cancellationToken).ConfigureAwait(false) as INamedTypeSymbol;
        }
        catch (InvalidOperationException) when (query.StartsWith("T:", StringComparison.Ordinal) || !query.Contains(':', StringComparison.Ordinal))
        {
            var metadataName = query.StartsWith("T:", StringComparison.Ordinal) ? query[2..] : query;
            foreach (var project in SelectProjects(solution, projectName))
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                var type = compilation?.GetTypeByMetadataName(metadataName);
                if (type is not null)
                {
                    return type;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Groups ObsoleteAttribute applications by the migration message and error severity encoded in constructor arguments.
    /// </summary>
    private static (string? Message, bool IsError) GetObsoleteMigrationKey(AttributeApplication application)
    {
        var positional = application.Arguments.Where(static argument => argument.Name is null).ToArray();
        var message = positional.ElementAtOrDefault(0)?.Constant;
        var isError = bool.TryParse(positional.ElementAtOrDefault(1)?.Constant, out var parsed) && parsed;
        return (message, isError);
    }

    /// <summary>
    /// Finds Microsoft DI and convention-shaped service registrations with explicit confidence.
    /// </summary>
    public async Task<object> GetDependencyInjectionMapAsync(
        string workspacePath,
        string? projectName,
        string? serviceSymbol,
        string? lifetimes,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        INamedTypeSymbol? serviceType = null;
        if (!string.IsNullOrWhiteSpace(serviceSymbol))
        {
            serviceType = await SymbolResolver.ResolveSingleAsync(snapshot.Solution, serviceSymbol, projectName, cancellationToken).ConfigureAwait(false) as INamedTypeSymbol
                ?? throw new ArgumentException("serviceSymbol must resolve to a named type.", nameof(serviceSymbol));
        }

        var lifetimeFilter = ParseFilters(lifetimes, ["singleton", "scoped", "transient", "unknown"], nameof(lifetimes));
        var limit = BoundLimit(maxResults);
        var registrations = await FindDiRegistrationsAsync(snapshot.Solution, projectName, limit * 2, cancellationToken).ConfigureAwait(false);
        var filtered = registrations
            .Where(registration => serviceType is null || SymbolEqualityComparer.Default.Equals(registration.ServiceType, serviceType))
            .Where(registration => lifetimeFilter.Count == 0 || lifetimeFilter.Contains(registration.Lifetime))
            .Take(limit)
            .ToArray();
        var results = new List<object>();
        foreach (var registration in filtered)
        {
            results.Add(new
            {
                registration.Lifetime,
                service = registration.ServiceType is null
                    ? null
                    : await DescribeSymbolAsync(registration.ServiceType, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                implementation = registration.ImplementationType is null
                    ? null
                    : await DescribeSymbolAsync(registration.ImplementationType, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                registration.FactoryExpression,
                method = SymbolResolver.GetStableId(registration.RegistrationMethod),
                registration.Confidence,
                registration.Shape,
                registration.Location
            });
        }

        return Wrap(new
        {
            serviceFilter = serviceType is null ? null : SymbolResolver.GetStableId(serviceType),
            registrations = results,
            limitation = "Compiler-bound invocation shapes are exact; custom registration extensions and runtime container mutation remain convention evidence."
        }, results.Count, registrations.Count > results.Count, snapshot);
    }

    /// <summary>
    /// Reports constructors, factories, required members, DI registrations, and accessibility from one project.
    /// </summary>
    public async Task<object> GetConstructionOptionsAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        string? fromProject,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(snapshot.Solution, symbolQuery, projectName, cancellationToken).ConfigureAwait(false);
        if (symbol is not INamedTypeSymbol type)
        {
            throw new ArgumentException("construction_options requires a named type.", nameof(symbolQuery));
        }

        var reachabilityProject = string.IsNullOrWhiteSpace(fromProject)
            ? SelectProjects(snapshot.Solution, projectName).FirstOrDefault()
            : SelectProjects(snapshot.Solution, fromProject).Single();
        var reachabilityCompilation = reachabilityProject is null
            ? null
            : await reachabilityProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var constructors = new List<object>();
        foreach (var constructor in type.InstanceConstructors.Where(constructor => !constructor.IsImplicitlyDeclared))
        {
            constructors.Add(new
            {
                symbol = await DescribeSymbolAsync(constructor, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                accessibleFromProject = reachabilityCompilation?.IsSymbolAccessibleWithin(constructor, reachabilityCompilation.Assembly) ?? false,
                parameters = constructor.Parameters.Select(DescribeParameter).ToArray()
            });
        }

        var resultLimit = BoundLimit(maxResults);
        var factories = new List<object>();
        foreach (var project in snapshot.Solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                foreach (var declaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(declaration, cancellationToken) is not IMethodSymbol method ||
                        !method.IsStatic ||
                        !SymbolEqualityComparer.Default.Equals(method.ReturnType, type))
                    {
                        continue;
                    }

                    factories.Add(new
                    {
                        symbol = await DescribeSymbolAsync(method, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                        accessibleFromProject = reachabilityCompilation?.IsSymbolAccessibleWithin(method, reachabilityCompilation.Assembly) ?? false,
                        parameters = method.Parameters.Select(DescribeParameter).ToArray()
                    });
                    if (factories.Count >= resultLimit)
                    {
                        break;
                    }
                }
            }
        }

        var registrations = (await FindDiRegistrationsAsync(snapshot.Solution, projectName: null, resultLimit, cancellationToken).ConfigureAwait(false))
            .Where(registration => SymbolEqualityComparer.Default.Equals(registration.ServiceType, type) ||
                                   SymbolEqualityComparer.Default.Equals(registration.ImplementationType, type))
            .Select(registration => new
            {
                registration.Lifetime,
                service = registration.ServiceType is null ? null : SymbolResolver.GetStableId(registration.ServiceType),
                implementation = registration.ImplementationType is null ? null : SymbolResolver.GetStableId(registration.ImplementationType),
                registration.Shape,
                registration.Confidence,
                registration.Location
            })
            .ToArray();
        var requiredMembers = type.GetMembers()
            .Where(member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })
            .Select(member => new
            {
                id = SymbolResolver.GetStableId(member),
                display = SymbolResolver.GetDisplay(member),
                accessibility = member.DeclaredAccessibility.ToString()
            })
            .ToArray();

        return Wrap(new
        {
            type = await DescribeSymbolAsync(type, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            fromProject = reachabilityProject?.Name,
            constructors,
            factories,
            dependencyInjectionRegistrations = registrations,
            requiredMembers
        }, constructors.Count + factories.Count + registrations.Length + requiredMembers.Length, false, snapshot);
    }

    private static Microsoft.CodeAnalysis.Text.TextSpan GetSignatureSpan(SyntaxNode syntax)
    {
        var end = syntax switch
        {
            BaseMethodDeclarationSyntax method when method.Body is not null => method.Body.OpenBraceToken.SpanStart,
            MethodDeclarationSyntax method when method.ExpressionBody is not null => method.ExpressionBody.ArrowToken.SpanStart,
            PropertyDeclarationSyntax property when property.AccessorList is not null => property.AccessorList.OpenBraceToken.SpanStart,
            PropertyDeclarationSyntax property when property.ExpressionBody is not null => property.ExpressionBody.ArrowToken.SpanStart,
            EventDeclarationSyntax @event when @event.AccessorList is not null => @event.AccessorList.OpenBraceToken.SpanStart,
            _ => syntax.Span.End
        };
        return Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(syntax.SpanStart, end);
    }

    private static ISymbol? ResolveDelegateTarget(IOperation? operation)
    {
        return operation switch
        {
            IDelegateCreationOperation creation => ResolveDelegateTarget(creation.Target),
            IMethodReferenceOperation method => method.Method,
            IAnonymousFunctionOperation anonymous => anonymous.Symbol,
            IConversionOperation conversion => ResolveDelegateTarget(conversion.Operand),
            _ => null
        };
    }

    private static ISymbol? FindAttributedSymbol(AttributeSyntax attribute, SemanticModel model, CancellationToken cancellationToken)
    {
        for (var node = attribute.Parent?.Parent; node is not null; node = node.Parent)
        {
            var symbol = model.GetDeclaredSymbol(node, cancellationToken);
            if (symbol is not null)
            {
                return symbol;
            }

            if (node is CompilationUnitSyntax)
            {
                break;
            }
        }

        return null;
    }

    private static bool InheritsFrom(INamedTypeSymbol type, string metadataName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString().Equals(metadataName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> ParseFilters(string? values, IReadOnlyCollection<string> allowed, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(values))
        {
            return [];
        }

        var filters = values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => value.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        var invalid = filters.Where(value => !allowed.Contains(value, StringComparer.Ordinal)).ToArray();
        if (invalid.Length > 0)
        {
            throw new ArgumentException($"Unsupported filter values: {string.Join(", ", invalid)}.", parameterName);
        }

        return filters;
    }

    private static object DescribeParameter(IParameterSymbol parameter)
    {
        return new
        {
            parameter.Name,
            type = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            refKind = parameter.RefKind.ToString(),
            parameter.IsOptional,
            parameter.IsParams,
            defaultValue = parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue?.ToString() : null
        };
    }

    private static async Task<IReadOnlyList<DiRegistration>> FindDiRegistrationsAsync(
        Solution solution,
        string? projectName,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var results = new List<DiRegistration>();
        foreach (var project in SelectProjects(solution, projectName))
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                foreach (var syntax in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetOperation(syntax, cancellationToken) is not IInvocationOperation invocation ||
                        !TryClassifyDiMethod(invocation.TargetMethod, out var lifetime, out var confidence))
                    {
                        continue;
                    }

                    INamedTypeSymbol? service = null;
                    INamedTypeSymbol? implementation = null;
                    string? factory = null;
                    var shape = "unknown";
                    if (invocation.TargetMethod.TypeArguments.Length >= 2)
                    {
                        service = invocation.TargetMethod.TypeArguments[0] as INamedTypeSymbol;
                        implementation = invocation.TargetMethod.TypeArguments[1] as INamedTypeSymbol;
                        shape = "two-generic";
                    }
                    else if (invocation.TargetMethod.TypeArguments.Length == 1)
                    {
                        service = invocation.TargetMethod.TypeArguments[0] as INamedTypeSymbol;
                        implementation = service;
                        shape = invocation.Arguments.Any(argument => argument.Value is IAnonymousFunctionOperation or IDelegateCreationOperation)
                            ? "single-generic-factory"
                            : "single-generic";
                    }

                    var typeOfArguments = invocation.Arguments.Select(argument => argument.Value)
                        .OfType<ITypeOfOperation>()
                        .Select(operation => operation.TypeOperand as INamedTypeSymbol)
                        .Where(type => type is not null)
                        .Cast<INamedTypeSymbol>()
                        .ToArray();
                    if (typeOfArguments.Length >= 2)
                    {
                        service = typeOfArguments[0];
                        implementation = typeOfArguments[1];
                        shape = "typeof-pair";
                    }

                    var factoryArgument = invocation.Arguments.FirstOrDefault(argument =>
                        argument.Value is IAnonymousFunctionOperation or IDelegateCreationOperation);
                    if (factoryArgument is not null)
                    {
                        factory = Trim(factoryArgument.Syntax.ToString(), MaximumExcerptLength);
                        var returnedType = factoryArgument.Value switch
                        {
                            IDelegateCreationOperation { Target: IAnonymousFunctionOperation anonymous } => anonymous.Symbol.ReturnType as INamedTypeSymbol,
                            IAnonymousFunctionOperation anonymous => anonymous.Symbol.ReturnType as INamedTypeSymbol,
                            _ => null
                        };
                        implementation ??= returnedType;
                    }

                    results.Add(new DiRegistration(
                        lifetime,
                        service,
                        implementation,
                        factory,
                        invocation.TargetMethod,
                        confidence,
                        shape,
                        await CreatePositionAsync(document, syntax.GetLocation(), cancellationToken).ConfigureAwait(false)));
                    if (results.Count >= maxResults)
                    {
                        return results;
                    }
                }
            }
        }

        return results;
    }

    private static bool TryClassifyDiMethod(IMethodSymbol method, out string lifetime, out string confidence)
    {
        var name = method.Name;
        lifetime = name.Contains("Singleton", StringComparison.OrdinalIgnoreCase)
            ? "singleton"
            : name.Contains("Scoped", StringComparison.OrdinalIgnoreCase)
                ? "scoped"
                : name.Contains("Transient", StringComparison.OrdinalIgnoreCase)
                    ? "transient"
                    : "unknown";
        if (lifetime == "unknown" && !name.StartsWith("TryAdd", StringComparison.Ordinal))
        {
            confidence = "none";
            return false;
        }

        var containingNamespace = method.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        confidence = containingNamespace.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal)
            ? "compiler-bound-framework"
            : "compiler-bound-convention";
        return true;
    }

    private sealed record DiRegistration(
        string Lifetime,
        INamedTypeSymbol? ServiceType,
        INamedTypeSymbol? ImplementationType,
        string? FactoryExpression,
        IMethodSymbol RegistrationMethod,
        string Confidence,
        string Shape,
        SourcePosition Location);

    private sealed record AttributeArgumentDescriptor(string? Name, string Expression, string? Type, string? Constant);

    private sealed record AttributeApplication(
        ISymbol Target,
        IMethodSymbol? Constructor,
        IReadOnlyList<AttributeArgumentDescriptor> Arguments,
        SourcePosition? Location,
        ISymbol? InheritedFrom);
}
