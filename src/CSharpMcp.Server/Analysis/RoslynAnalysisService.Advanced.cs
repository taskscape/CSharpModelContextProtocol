using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpMcp.Analysis;

/// <summary>
/// Provides API compatibility, region flow, architecture, stack-trace, and context-bundle analysis.
/// </summary>
internal sealed partial class RoslynAnalysisService
{
    private static readonly Regex StackFramePattern = new(
        @"\bat\s+(?<symbol>[^\r\n]+?)(?:\s+in\s+(?<file>.*?):line\s+(?<line>\d+))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Returns a deterministic public API surface and optionally compares it with JSON or assembly baseline data.
    /// </summary>
    public async Task<object> GetApiCompatibilityAsync(
        string workspacePath,
        string? projectName,
        string? baselinePath,
        bool includeCurrentSurface,
        string? cursor,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var projects = SelectProjects(snapshot.Solution, projectName).Where(project => !IsTestProject(project)).ToArray();
        var current = new List<ApiSurfaceItem>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            CollectPublicApi(compilation.Assembly.GlobalNamespace, project.Name, sourceOnly: true, current);
        }

        current = current.DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
        IReadOnlyList<ApiSurfaceItem>? baseline = null;
        OfficialApiCompatResult? officialApiCompat = null;
        if (!string.IsNullOrWhiteSpace(baselinePath))
        {
            baseline = await ReadApiBaselineAsync(baselinePath, cancellationToken).ConfigureAwait(false);
            if (Path.GetExtension(baselinePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (projects.Length != 1)
                {
                    throw new ArgumentException("A DLL baseline requires projectName to select exactly one current project.", nameof(projectName));
                }

                officialApiCompat = await RunOfficialApiCompatAsync(
                    projects[0], Path.GetFullPath(baselinePath), maxResults, cancellationToken).ConfigureAwait(false);
            }
        }

        var limit = BoundLimit(maxResults);
        var changes = new List<object>();
        if (baseline is not null)
        {
            var currentById = current.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var baselineById = baseline.DistinctBy(item => item.Id, StringComparer.Ordinal).ToDictionary(item => item.Id, StringComparer.Ordinal);
            foreach (var item in baseline.Where(item => !currentById.ContainsKey(item.Id)))
            {
                changes.Add(new { severity = "Breaking", change = "removed", baseline = item, current = (ApiSurfaceItem?)null });
            }

            foreach (var item in current.Where(item => !baselineById.ContainsKey(item.Id)))
            {
                changes.Add(new { severity = "NonBreaking", change = "added", baseline = (ApiSurfaceItem?)null, current = item });
            }

            foreach (var item in current.Where(item => baselineById.ContainsKey(item.Id)))
            {
                var previous = baselineById[item.Id];
                if (!previous.Kind.Equals(item.Kind, StringComparison.Ordinal) ||
                    AccessibilityRank(item.Accessibility) < AccessibilityRank(previous.Accessibility) ||
                    !string.Equals(previous.ContractFingerprint, item.ContractFingerprint, StringComparison.Ordinal))
                {
                    changes.Add(new { severity = "Breaking", change = "contract-changed", baseline = previous, current = item });
                }
            }
        }

        var orderedChanges = changes.OrderBy(change =>
                ((string)change.GetType().GetProperty("severity")!.GetValue(change)!).Equals("Breaking", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(change => change.GetType().GetProperty("change")!.GetValue(change)?.ToString(), StringComparer.Ordinal)
            .ToArray();
        var baselineVersion = !string.IsNullOrWhiteSpace(baselinePath) && File.Exists(baselinePath)
            ? $"{File.GetLastWriteTimeUtc(baselinePath).Ticks}:{new FileInfo(baselinePath).Length}"
            : "none";
        var cursorFingerprint = $"{snapshot.LoadedAt.UtcTicks}:{projectName}:{baselinePath}:{baselineVersion}:{includeCurrentSurface}";
        var pageOffset = ParseCursor(cursor, "api-compatibility", cursorFingerprint);
        var pageTotal = baseline is null ? current.Count : orderedChanges.Length;
        var pageReturned = Math.Min(limit, Math.Max(0, pageTotal - pageOffset));
        var data = new
        {
            projects = projects.Select(project => project.Name).ToArray(),
            currentApiCount = current.Count,
            baselinePath,
            baselineApiCount = baseline?.Count,
            changes = baseline is null ? [] : orderedChanges.Skip(pageOffset).Take(limit).ToArray(),
            counts = new
            {
                breaking = orderedChanges.Count(change =>
                    ((string)change.GetType().GetProperty("severity")!.GetValue(change)!).Equals("Breaking", StringComparison.Ordinal)),
                nonBreaking = orderedChanges.Count(change =>
                    ((string)change.GetType().GetProperty("severity")!.GetValue(change)!).Equals("NonBreaking", StringComparison.Ordinal))
            },
            currentSurface = includeCurrentSurface
                ? current.Skip(baseline is null ? pageOffset : 0).Take(limit).ToArray()
                : null,
            pageOffset,
            pagedSection = baseline is null ? "currentSurface" : "changes",
            nextCursor = pageOffset + pageReturned < pageTotal
                ? CreateCursor("api-compatibility", cursorFingerprint, pageOffset + pageReturned)
                : null,
            officialApiCompat,
            compatibilityEngine = officialApiCompat is null ? "structured-surface" : "Microsoft.DotNet.ApiCompat.Tool",
            baselineFormat = "JSON baselines use the structured surface comparison. DLL baselines additionally run Microsoft's official ApiCompat rules against an emitted current assembly."
        };
        return Wrap(data, pageReturned, pageOffset + pageReturned < pageTotal, snapshot);
    }

    /// <summary>
    /// Runs Roslyn data-flow and/or control-flow analysis over a contiguous statement range.
    /// </summary>
    public async Task<object> GetRegionFlowAsync(
        string workspacePath,
        string documentPath,
        int startLine,
        int startColumn,
        int endLine,
        int endColumn,
        string kind,
        CancellationToken cancellationToken)
    {
        var normalizedKind = kind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("data" or "control" or "both"))
        {
            throw new ArgumentException("kind must be data, control, or both.", nameof(kind));
        }

        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var document = FindDocument(snapshot.Solution, documentPath);
        var (model, root, start) = await GetSemanticPositionAsync(document, startLine, startColumn, cancellationToken).ConfigureAwait(false);
        var (_, _, end) = await GetSemanticPositionAsync(document, endLine, endColumn, cancellationToken).ConfigureAwait(false);
        if (end < start)
        {
            throw new ArgumentException("The end position must not precede the start position.");
        }

        var statements = root.DescendantNodes()
            .OfType<StatementSyntax>()
            .Where(statement => statement.FullSpan.End >= start && statement.FullSpan.Start <= end)
            .Where(statement => statement.Parent is BlockSyntax or SwitchSectionSyntax)
            .OrderBy(statement => statement.SpanStart)
            .ToArray();
        if (statements.Length == 0)
        {
            throw new ArgumentException("The requested range does not contain a directly analyzable statement region.");
        }

        var first = statements[0];
        var last = statements[^1];
        var dataFlow = normalizedKind is "data" or "both" ? model.AnalyzeDataFlow(first, last) : null;
        var controlFlow = normalizedKind is "control" or "both" ? model.AnalyzeControlFlow(first, last) : null;
        var data = new
        {
            document = document.FilePath,
            analyzedSpan = new
            {
                start = await CreatePositionAsync(document, first.GetLocation(), cancellationToken).ConfigureAwait(false),
                endLine,
                endColumn,
                statementCount = statements.Length
            },
            dataFlow = dataFlow is null
                ? null
                : new
                {
                    dataFlow.Succeeded,
                    variablesDeclared = DescribeFlowSymbols(dataFlow.VariablesDeclared),
                    alwaysAssigned = DescribeFlowSymbols(dataFlow.AlwaysAssigned),
                    readInside = DescribeFlowSymbols(dataFlow.ReadInside),
                    writtenInside = DescribeFlowSymbols(dataFlow.WrittenInside),
                    readOutside = DescribeFlowSymbols(dataFlow.ReadOutside),
                    writtenOutside = DescribeFlowSymbols(dataFlow.WrittenOutside),
                    dataFlowsIn = DescribeFlowSymbols(dataFlow.DataFlowsIn),
                    dataFlowsOut = DescribeFlowSymbols(dataFlow.DataFlowsOut),
                    captured = DescribeFlowSymbols(dataFlow.Captured)
                },
            controlFlow = controlFlow is null
                ? null
                : new
                {
                    controlFlow.Succeeded,
                    controlFlow.StartPointIsReachable,
                    controlFlow.EndPointIsReachable,
                    entryPoints = controlFlow.EntryPoints.Select(node => node.Kind().ToString()).ToArray(),
                    exitPoints = controlFlow.ExitPoints.Select(node => new
                    {
                        kind = node.Kind().ToString(),
                        text = Trim(node.ToString(), MaximumExcerptLength)
                    }).ToArray(),
                    returnStatements = controlFlow.ReturnStatements.Select(node => Trim(node.ToString(), MaximumExcerptLength)).ToArray()
                }
        };
        var returned = (dataFlow?.VariablesDeclared.Length ?? 0) +
                       (dataFlow?.DataFlowsIn.Length ?? 0) +
                       (dataFlow?.DataFlowsOut.Length ?? 0) +
                       (controlFlow?.ExitPoints.Length ?? 0);
        return Wrap(data, returned, false, snapshot);
    }

    /// <summary>
    /// Checks supplied namespace-layering rules against exact source symbol references.
    /// </summary>
    public async Task<object> CheckArchitectureRulesAsync(
        string workspacePath,
        ArchitectureRuleInput[] rules,
        string? projectName,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Length is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(rules), "Provide between 1 and 50 architecture rules.");
        }

        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var limit = BoundLimit(maxResults);
        var violations = new Dictionary<string, ArchitectureViolation>(StringComparer.Ordinal);
        foreach (var project in SelectProjects(snapshot.Solution, projectName))
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                foreach (var node in root.DescendantNodes().Where(node => node is NameSyntax or MemberAccessExpressionSyntax))
                {
                    var target = model.GetSymbolInfo(node, cancellationToken).Symbol;
                    var targetType = target switch
                    {
                        INamedTypeSymbol namedType => namedType,
                        _ => target?.ContainingType
                    };
                    var enclosing = model.GetEnclosingSymbol(node.SpanStart, cancellationToken);
                    var source = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
                    if (source is null || targetType is null ||
                        !targetType.Locations.Any(location => location.IsInSource) ||
                        SymbolEqualityComparer.Default.Equals(source, targetType))
                    {
                        continue;
                    }

                    var sourceNamespace = source.ContainingNamespace.ToDisplayString();
                    var targetNamespace = targetType.ContainingNamespace.ToDisplayString();
                    foreach (var rule in rules.Where(rule => NamespaceMatches(sourceNamespace, rule.FromNamespace)))
                    {
                        var forbidden = (rule.Forbid ?? []).Any(prefix => NamespaceMatches(targetNamespace, prefix));
                        var allowOnlyViolation = (rule.AllowOnly ?? []).Length > 0 &&
                                                 !(rule.AllowOnly ?? []).Any(prefix => NamespaceMatches(targetNamespace, prefix));
                        if (!forbidden && !allowOnlyViolation)
                        {
                            continue;
                        }

                        var key = $"{rule.Name}|{SymbolResolver.GetStableId(source)}|{SymbolResolver.GetStableId(targetType)}";
                        if (!violations.TryGetValue(key, out var violation))
                        {
                            violation = new ArchitectureViolation(rule.Name, source, targetType, forbidden ? "forbidden" : "outside-allow-list", []);
                            violations[key] = violation;
                        }

                        if (violation.Examples.Count < 5)
                        {
                            violation.Examples.Add(await CreatePositionAsync(document, node.GetLocation(), cancellationToken).ConfigureAwait(false));
                        }
                    }
                }
            }
        }

        var ordered = violations.Values.OrderBy(item => item.Rule, StringComparer.Ordinal)
            .ThenBy(item => SymbolResolver.GetStableId(item.Source), StringComparer.Ordinal)
            .ToArray();
        var results = new List<object>();
        foreach (var violation in ordered.Take(limit))
        {
            results.Add(new
            {
                violation.Rule,
                violation.Reason,
                source = await DescribeSymbolAsync(violation.Source, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                target = await DescribeSymbolAsync(violation.Target, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                referenceCount = violation.Examples.Count,
                examples = violation.Examples
            });
        }

        return Wrap(new { rules, violations = results }, results.Count, ordered.Length > results.Count, snapshot);
    }

    /// <summary>
    /// Maps .NET stack frames to loaded source symbols and locations.
    /// </summary>
    public async Task<object> ResolveStackTraceAsync(
        string workspacePath,
        string stackTrace,
        int maxFrames,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stackTrace);
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(maxFrames, 1, 200);
        var parsedFrames = stackTrace.ReplaceLineEndings("\n").Split('\n')
            .Select(line => (Line: line, Match: StackFramePattern.Match(line)))
            .Where(item => item.Match.Success)
            .Take(limit)
            .ToArray();
        var methods = await EnumerateSourceMethodsAsync(snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var results = new List<object>();
        foreach (var frame in parsedFrames)
        {
            var rawSymbol = frame.Match.Groups["symbol"].Value.Trim();
            var normalized = NormalizeStackSymbol(rawSymbol);
            var openParen = normalized.IndexOf('(');
            var qualified = openParen >= 0 ? normalized[..openParen] : normalized;
            var lastDot = qualified.LastIndexOf('.');
            var methodName = lastDot >= 0 ? qualified[(lastDot + 1)..] : qualified;
            var typeName = lastDot >= 0 ? qualified[..lastDot] : string.Empty;
            var candidates = methods.Where(method =>
                    method.Name.Equals(methodName, StringComparison.Ordinal) &&
                    (typeName.Length == 0 || SymbolResolver.GetDisplay(method.ContainingType).Contains(typeName, StringComparison.Ordinal)))
                .Take(20)
                .ToArray();
            var exactSource = TryResolveFrameByFileAndLine(snapshot.Solution, frame.Match, cancellationToken);
            ISymbol? resolved = await exactSource.ConfigureAwait(false) ?? (candidates.Length == 1 ? candidates[0] : null);
            results.Add(new
            {
                raw = frame.Line.Trim(),
                normalized,
                status = resolved is not null ? "resolved" : candidates.Length > 1 ? "ambiguous" : "notFound",
                symbol = resolved is null
                    ? null
                    : await DescribeSymbolAsync(resolved, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                candidates = resolved is null ? candidates.Select(SymbolResolver.GetStableId).ToArray() : [],
                reportedFile = frame.Match.Groups["file"].Success ? frame.Match.Groups["file"].Value : null,
                reportedLine = frame.Match.Groups["line"].Success ? int.Parse(frame.Match.Groups["line"].Value) : (int?)null
            });
        }

        return Wrap(new { frames = results }, results.Count, parsedFrames.Length >= limit, snapshot);
    }

    /// <summary>
    /// Composes a strictly bounded goal-specific context package from existing semantic primitives.
    /// </summary>
    public async Task<object> GetContextBundleAsync(
        string workspacePath,
        string symbolQuery,
        string? projectName,
        string profile,
        int maxResultsPerSection,
        CancellationToken cancellationToken)
    {
        var normalizedProfile = profile.Trim().ToLowerInvariant();
        if (normalizedProfile is not ("understand" or "contract-change" or "debug-flow"))
        {
            throw new ArgumentException("profile must be understand, contract-change, or debug-flow.", nameof(profile));
        }

        var limit = Math.Clamp(maxResultsPerSection, 1, 50);
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var sections = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["symbol"] = await GetSymbolInfoAsync(workspacePath, symbolQuery, projectName, limit, cancellationToken).ConfigureAwait(false)
        };
        if (normalizedProfile == "understand")
        {
            var symbol = await SymbolResolver.ResolveSingleAsync(snapshot.Solution, symbolQuery, projectName, cancellationToken).ConfigureAwait(false);
            if (symbol is INamedTypeSymbol)
            {
                sections["members"] = await GetMemberSurfaceAsync(
                     workspacePath, symbolQuery, projectName, null, "non-private", includeInherited: false,
                     includeExplicitInterfaceImplementations: true, memberName: null,
                     includeApplicableExtensionMethods: false, mode: "all", limit, cancellationToken).ConfigureAwait(false);
                sections["inheritance"] = await GetInheritanceGraphAsync(
                    workspacePath, symbolQuery, projectName, "both", 1, includeInterfaces: true, limit, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (normalizedProfile == "contract-change")
        {
            sections["references"] = await FindReferencesAsync(workspacePath, symbolQuery, projectName, null, false, limit, cancellationToken).ConfigureAwait(false);
            sections["implementations"] = await GetImplementationMapAsync(workspacePath, symbolQuery, projectName, limit, cancellationToken).ConfigureAwait(false);
            var impactLimit = Math.Min(limit, 10);
            sections["impact"] = await GetAffectedSymbolsAsync(
                workspacePath,
                symbolQuery,
                projectName,
                impactLimit,
                impactLimit,
                impactLimit,
                impactLimit,
                impactLimit,
                impactLimit,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            sections["callHierarchy"] = await GetCallHierarchyAsync(
                workspacePath, symbolQuery, projectName, "both", 2, limit, cancellationToken).ConfigureAwait(false);
            sections["references"] = await FindReferencesAsync(workspacePath, symbolQuery, projectName, null, false, limit, cancellationToken).ConfigureAwait(false);
        }

        return Wrap(new
        {
            profile = normalizedProfile,
            maxResultsPerSection = limit,
            sections,
            recommendedNextTool = normalizedProfile switch
            {
                "contract-change" => "rename_preview or diagnostics_delta",
                "debug-flow" => "invocation_binding or region_flow",
                _ => "symbol_source"
            }
        }, sections.Count, false, snapshot);
    }

    private static void CollectPublicApi(INamespaceSymbol @namespace, string project, bool sourceOnly, ICollection<ApiSurfaceItem> results)
    {
        foreach (var type in @namespace.GetTypeMembers())
        {
            CollectPublicApi(type, project, sourceOnly, results);
        }

        foreach (var child in @namespace.GetNamespaceMembers())
        {
            CollectPublicApi(child, project, sourceOnly, results);
        }
    }

    private static void CollectPublicApi(INamedTypeSymbol type, string project, bool sourceOnly, ICollection<ApiSurfaceItem> results)
    {
        if ((!sourceOnly || type.Locations.Any(location => location.IsInSource)) && IsPublicApiAccessibility(type.DeclaredAccessibility))
        {
            results.Add(CreateApiItem(type, project));
            foreach (var member in type.GetMembers().Where(member =>
                         !member.IsImplicitlyDeclared &&
                         IsPublicApiAccessibility(member.DeclaredAccessibility) &&
                         (!sourceOnly || member.Locations.Any(location => location.IsInSource))))
            {
                results.Add(CreateApiItem(member, project));
            }
        }

        foreach (var nested in type.GetTypeMembers())
        {
            CollectPublicApi(nested, project, sourceOnly, results);
        }
    }

    private static ApiSurfaceItem CreateApiItem(ISymbol symbol, string project)
    {
        var baseType = symbol is INamedTypeSymbol type
            ? type.BaseType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : null;
        var interfaces = symbol is INamedTypeSymbol interfaceOwner
            ? interfaceOwner.Interfaces.Select(item => item.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .OrderBy(item => item, StringComparer.Ordinal).ToArray()
            : [];
        var constraints = symbol switch
        {
            INamedTypeSymbol namedType => DescribeTypeParameterConstraints(namedType.TypeParameters),
            IMethodSymbol method => DescribeTypeParameterConstraints(method.TypeParameters),
            _ => []
        };
        var contractFingerprint = string.Join("|", [baseType ?? string.Empty, .. interfaces, .. constraints]);
        return new ApiSurfaceItem(
            SymbolResolver.GetStableId(symbol),
            symbol.Kind.ToString(),
            symbol.DeclaredAccessibility.ToString(),
            SymbolResolver.GetDisplay(symbol),
            project,
            baseType,
            interfaces,
            constraints,
            contractFingerprint);
    }

    /// <summary>
    /// Produces deterministic generic-constraint text for baseline comparisons.
    /// </summary>
    private static string[] DescribeTypeParameterConstraints(IEnumerable<ITypeParameterSymbol> parameters)
    {
        return parameters.Select(parameter =>
            $"{parameter.Name}:{parameter.Variance}:{parameter.HasReferenceTypeConstraint}:{parameter.ReferenceTypeConstraintNullableAnnotation}:" +
            $"{parameter.HasValueTypeConstraint}:{parameter.HasUnmanagedTypeConstraint}:{parameter.HasNotNullConstraint}:" +
            $"{parameter.HasConstructorConstraint}:{string.Join(',', parameter.ConstraintTypes.Select(type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))}")
            .ToArray();
    }

    private static bool IsPublicApiAccessibility(Accessibility accessibility)
    {
        return accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
    }

    private static async Task<IReadOnlyList<ApiSurfaceItem>> ReadApiBaselineAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The API baseline does not exist.", fullPath);
        }

        if (Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            await using var stream = File.OpenRead(fullPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var element = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : document.RootElement.GetProperty("apis");
            return JsonSerializer.Deserialize<ApiSurfaceItem[]>(element.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }

        if (!Path.GetExtension(fullPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The API baseline must be a .json or .dll file.", nameof(path));
        }

        var reference = MetadataReference.CreateFromFile(fullPath);
        var compilation = CSharpCompilation.Create("CSharpMcp.ApiBaseline", references: [reference]);
        if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
        {
            throw new InvalidOperationException("Roslyn could not read the baseline assembly metadata.");
        }

        var results = new List<ApiSurfaceItem>();
        CollectPublicApi(assembly.GlobalNamespace, assembly.Identity.Name, sourceOnly: false, results);
        return results;
    }

    private static int AccessibilityRank(string accessibility)
    {
        return accessibility switch
        {
            nameof(Accessibility.Public) => 4,
            nameof(Accessibility.ProtectedOrInternal) => 3,
            nameof(Accessibility.Protected) => 2,
            nameof(Accessibility.Internal) => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Emits the selected current project and delegates DLL compatibility policy to Microsoft's ApiCompat tool.
    /// </summary>
    private static async Task<OfficialApiCompatResult> RunOfficialApiCompatAsync(
        Project project,
        string baselinePath,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project '{project.Name}' did not produce a compilation.");
        var tempDirectory = Path.Combine(Path.GetTempPath(), "CSharpMCP", "ApiCompat", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var currentAssembly = Path.Combine(tempDirectory, $"{compilation.AssemblyName ?? project.Name}.dll");
            await using (var stream = File.Create(currentAssembly))
            {
                var emit = compilation.Emit(stream, cancellationToken: cancellationToken);
                if (!emit.Success)
                {
                    var errors = emit.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                        .Take(20).Select(diagnostic => diagnostic.ToString());
                    throw new InvalidOperationException("The current project could not be emitted for ApiCompat: " + string.Join("; ", errors));
                }
            }

            var manifestRoot = FindToolManifestRoot();
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = manifestRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "tool", "run", "apicompat", "--", "--left", baselinePath, "--right", currentAssembly,
                         "--verbosity", "Low", "--enable-rule-attributes-must-match", "--enable-rule-cannot-change-parameter-name"
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the Microsoft ApiCompat tool.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var lines = string.Join(Environment.NewLine, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var limit = BoundLimit(maxResults);
            return new OfficialApiCompatResult(
                "Microsoft.DotNet.ApiCompat.Tool",
                process.ExitCode,
                process.ExitCode == 0,
                lines.Length,
                lines.Take(limit).ToArray(),
                lines.Length > limit);
        }
        finally
        {
            // This path is a uniquely-created child of the OS temp directory and contains only this request's emitted assembly.
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// Locates the checked-in tool manifest from the deployed server or test output directory.
    /// </summary>
    private static string FindToolManifestRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ".config", "dotnet-tools.json")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The checked-in Microsoft ApiCompat tool manifest could not be located.");
    }

    private static object[] DescribeFlowSymbols(IEnumerable<ISymbol> symbols)
    {
        return symbols.OrderBy(SymbolResolver.GetStableId, StringComparer.Ordinal)
            .Select(symbol => (object)new
            {
                id = SymbolResolver.GetStableId(symbol),
                display = SymbolResolver.GetDisplay(symbol),
                kind = symbol.Kind.ToString()
            })
            .ToArray();
    }

    private static bool NamespaceMatches(string candidate, string prefix)
    {
        return candidate.Equals(prefix, StringComparison.Ordinal) || candidate.StartsWith($"{prefix}.", StringComparison.Ordinal);
    }

    private static string NormalizeStackSymbol(string value)
    {
        var normalized = Regex.Replace(value, @"\+<(?<method>[^>]+)>d__\d+\.MoveNext", ".${method}", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"\+<>c(?:__DisplayClass\d+_\d+)?\.<(?<method>[^>]+)>b__[^\(]*", ".${method}", RegexOptions.CultureInvariant);
        normalized = Regex.Replace(normalized, @"`\d+", string.Empty, RegexOptions.CultureInvariant);
        return normalized.Replace('+', '.');
    }

    private static async Task<IReadOnlyList<IMethodSymbol>> EnumerateSourceMethodsAsync(Solution solution, CancellationToken cancellationToken)
    {
        var results = new List<IMethodSymbol>();
        foreach (var project in solution.Projects.Where(project => project.Language == LanguageNames.CSharp))
        {
            foreach (var document in project.Documents)
            {
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root is null || model is null)
                {
                    continue;
                }

                results.AddRange(root.DescendantNodes()
                    .Where(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax)
                    .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
                    .OfType<IMethodSymbol>());
            }
        }

        return results;
    }

    private static async Task<ISymbol?> TryResolveFrameByFileAndLine(
        Solution solution,
        Match frame,
        CancellationToken cancellationToken)
    {
        if (!frame.Groups["file"].Success || !frame.Groups["line"].Success ||
            !int.TryParse(frame.Groups["line"].Value, out var lineNumber))
        {
            return null;
        }

        var path = Path.GetFullPath(frame.Groups["file"].Value);
        var document = solution.Projects.SelectMany(project => project.Documents)
            .FirstOrDefault(candidate => candidate.FilePath is not null &&
                                         Path.GetFullPath(candidate.FilePath).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (document is null)
        {
            return null;
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (lineNumber < 1 || lineNumber > text.Lines.Count)
        {
            return null;
        }

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        return model?.GetEnclosingSymbol(text.Lines[lineNumber - 1].Start, cancellationToken);
    }

    private sealed record ApiSurfaceItem(
        string Id,
        string Kind,
        string Accessibility,
        string Signature,
        string Project,
        string? BaseType = null,
        IReadOnlyList<string>? Interfaces = null,
        IReadOnlyList<string>? TypeParameterConstraints = null,
        string? ContractFingerprint = null);

    private sealed record OfficialApiCompatResult(
        string Engine,
        int ExitCode,
        bool Compatible,
        int DiagnosticCount,
        IReadOnlyList<string> Diagnostics,
        bool Truncated);

    private sealed record ArchitectureViolation(
        string Rule,
        INamedTypeSymbol Source,
        INamedTypeSymbol Target,
        string Reason,
        List<SourcePosition> Examples);
}
