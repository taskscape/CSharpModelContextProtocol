using CSharpMcp.Workspace;
using CSharpMcp.Infrastructure;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CSharpMcp.Analysis;

/// <summary>
/// Provides generated-source, conditional-compilation, and workspace-health intelligence.
/// </summary>
internal sealed partial class RoslynAnalysisService
{
    /// <summary>
    /// Inventories source-generator candidates, generated documents, declarations, and diagnostics.
    /// </summary>
    public async Task<object> GetSourceGeneratorInventoryAsync(
        string workspacePath,
        string? projectName,
        string? generatorName,
        string? generatedDocumentId,
        bool includeGeneratedSourceExcerpt,
        string? cursor,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var cursorFingerprint = $"{snapshot.LoadedAt.UtcTicks}:{projectName}:{generatorName}:{generatedDocumentId}:{includeGeneratedSourceExcerpt}";
        var pageOffset = ParseCursor(cursor, "source-generator-inventory", cursorFingerprint);
        var limit = BoundLimit(maxResults);
        var projects = SelectProjects(snapshot.Solution, projectName).ToArray();
        var projectResults = new List<object>();
        var returnedDocuments = 0;
        var totalDocuments = 0;
        var documentIndex = 0;

        foreach (var project in projects)
        {
            var generators = project.AnalyzerReferences
                .SelectMany(reference => reference.GetGenerators(project.Language))
                .Select(generator => new
                {
                    type = generator.GetType().FullName ?? generator.GetType().Name,
                    assembly = generator.GetType().Assembly.GetName().Name,
                    analyzerReference = project.AnalyzerReferences.FirstOrDefault(reference =>
                        reference.GetGenerators(project.Language).Any(candidate => ReferenceEquals(candidate, generator)))?.FullPath
                })
                .Where(generator => string.IsNullOrWhiteSpace(generatorName) ||
                                    generator.type.Contains(generatorName, StringComparison.OrdinalIgnoreCase))
                .DistinctBy(generator => $"{generator.assembly}|{generator.type}", StringComparer.Ordinal)
                .OrderBy(generator => generator.type, StringComparer.Ordinal)
                .ToArray();

            _ = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            var generatedDocuments = (await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
                .Where(document => string.IsNullOrWhiteSpace(generatedDocumentId) ||
                                   document.Id.Id.ToString().Equals(generatedDocumentId, StringComparison.OrdinalIgnoreCase) ||
                                   document.Name.Equals(generatedDocumentId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            totalDocuments += generatedDocuments.Length;
            var documentResults = new List<object>();
            foreach (var document in generatedDocuments.OrderBy(document => document.Name, StringComparer.Ordinal))
            {
                if (documentIndex++ < pageOffset || returnedDocuments >= limit)
                {
                    continue;
                }

                var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var declaredSymbols = root is null || model is null
                    ? []
                    : root.DescendantNodes()
                        .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                        .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
                        .Where(symbol => symbol is not null)
                        .Cast<ISymbol>()
                        .Select(symbol => new
                        {
                            id = SymbolResolver.GetStableId(symbol),
                            display = SymbolResolver.GetDisplay(symbol),
                            kind = symbol.Kind.ToString()
                        })
                        .Take(50)
                        .ToArray();
                var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
                var diagnostics = syntaxTree?.GetDiagnostics(cancellationToken)
                    .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
                    .Select(diagnostic => new
                    {
                        diagnostic.Id,
                        severity = diagnostic.Severity.ToString(),
                        message = diagnostic.GetMessage()
                    })
                    .Take(50)
                    .ToArray() ?? [];

                documentResults.Add(new
                {
                    documentId = document.Id.Id,
                    document.Name,
                    document.FilePath,
                    hintName = document.Name,
                    textLength = text.Length,
                    lineCount = text.Lines.Count,
                    declaredSymbols,
                    diagnostics,
                    excerpt = includeGeneratedSourceExcerpt ? CreateGeneratedExcerpt(text) : null,
                    generatorAssociation = "The public workspace document API does not expose an authoritative document-to-generator mapping."
                });
                returnedDocuments++;
            }

            projectResults.Add(new
            {
                projectId = project.Id.Id,
                project = project.Name,
                generators,
                generatedDocuments = documentResults,
                generatedDocumentCount = generatedDocuments.Length
            });

        }

        var data = new
        {
            generatorFilter = generatorName,
            generatedDocumentFilter = generatedDocumentId,
            includeGeneratedSourceExcerpt,
            pageOffset,
            nextCursor = pageOffset + returnedDocuments < totalDocuments
                ? CreateCursor("source-generator-inventory", cursorFingerprint, pageOffset + returnedDocuments)
                : null,
            projects = projectResults,
            limitations = new[]
            {
                "Generator candidates come from analyzer references; Roslyn's public workspace API does not expose per-generator elapsed time.",
                "Generated excerpts are deliberately capped and full generated files are never returned by this tool."
            }
        };
        return Wrap(data, returnedDocuments, totalDocuments > returnedDocuments, snapshot);
    }

    /// <summary>
    /// Recompiles a project under bounded explicit preprocessor-symbol variants.
    /// </summary>
    public async Task<object> GetConditionalCompilationMatrixAsync(
        string workspacePath,
        string projectName,
        string[] symbolSets,
        string[]? configurations,
        string[]? targetFrameworks,
        string? documentPath,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbolSets);
        if (symbolSets.Length is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolSets), "Provide between 1 and 16 symbol-set variants.");
        }

        var requestedConfigurations = NormalizeConfigurations(configurations);
        var requestedTargetFrameworks = NormalizeTargetFrameworks(targetFrameworks);
        var evaluationCount = requestedConfigurations.Length * requestedTargetFrameworks.Length * symbolSets.Length;
        if (evaluationCount > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(configurations), "The configuration, target-framework, and symbol-set matrix may contain at most 32 variants.");
        }

        WorkspaceSnapshot? firstSnapshot = null;
        var resultLimit = BoundLimit(maxResults);
        var variants = new List<object>();
        var totalItems = 0;
        foreach (var configuration in requestedConfigurations)
        {
            foreach (var targetFramework in requestedTargetFrameworks)
            {
                var snapshot = await workspaceCache.GetSnapshotAsync(
                    workspacePath,
                    new WorkspaceLoadOptions(configuration, targetFramework),
                    cancellationToken).ConfigureAwait(false);
                firstSnapshot ??= snapshot;
                var project = SelectProjects(snapshot.Solution, projectName).Single();
                var parseOptions = project.ParseOptions as CSharpParseOptions
                    ?? throw new InvalidOperationException("The selected project does not use C# parse options.");
                var selectedDocument = string.IsNullOrWhiteSpace(documentPath)
                    ? null
                    : FindDocument(snapshot.Solution, documentPath);
                if (selectedDocument is not null && selectedDocument.Project.Id != project.Id)
                {
                    throw new ArgumentException("The selected document is not part of the selected project.", nameof(documentPath));
                }

                foreach (var symbolSet in symbolSets)
                {
                    var symbols = ParsePreprocessorSymbols(symbolSet);
                    var variantProject = project.WithParseOptions(parseOptions.WithPreprocessorSymbols(symbols));
                    var compilation = await variantProject.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                    var allDiagnostics = compilation?.GetDiagnostics(cancellationToken)
                        .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)
                        .ToArray() ?? [];
                    var diagnostics = allDiagnostics.Take(resultLimit).Select(diagnostic => new
                    {
                        diagnostic.Id,
                        severity = diagnostic.Severity.ToString(),
                        message = diagnostic.GetMessage(),
                        file = diagnostic.Location.GetLineSpan().Path,
                        line = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1 : (int?)null,
                        column = diagnostic.Location.IsInSource ? diagnostic.Location.GetLineSpan().StartLinePosition.Character + 1 : (int?)null
                    }).ToArray();
                    var documentResults = new List<object>();
                    var documents = selectedDocument is null
                        ? variantProject.Documents
                        : [variantProject.GetDocument(selectedDocument.Id) ?? throw new InvalidOperationException("The selected document was lost from the variant solution.")];
                    foreach (var document in documents)
                    {
                        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                        if (root is null || model is null)
                        {
                            continue;
                        }

                        var declarations = root.DescendantNodes()
                            .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax)
                            .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
                            .Where(symbol => symbol is not null)
                            .Cast<ISymbol>()
                            .Select(symbol => new
                            {
                                id = SymbolResolver.GetStableId(symbol),
                                display = SymbolResolver.GetDisplay(symbol),
                                kind = symbol.Kind.ToString()
                            })
                            .Take(resultLimit)
                            .ToArray();
                        var inactiveRegions = root.DescendantTrivia(descendIntoTrivia: true)
                            .Where(trivia => trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                            .Select(trivia =>
                            {
                                var line = text.Lines.GetLineFromPosition(trivia.SpanStart);
                                return new
                                {
                                    line = line.LineNumber + 1,
                                    column = trivia.SpanStart - line.Start + 1,
                                    length = trivia.Span.Length,
                                    excerpt = Trim(trivia.ToString().Trim(), MaximumExcerptLength)
                                };
                            })
                            .Take(resultLimit)
                            .ToArray();
                        totalItems += declarations.Length + inactiveRegions.Length + diagnostics.Length;
                        documentResults.Add(new
                        {
                            document.Name,
                            document.FilePath,
                            declarations,
                            inactiveRegions
                        });
                    }

                    variants.Add(new
                    {
                        configuration,
                        targetFramework,
                        requestedSymbols = symbolSet,
                        symbols,
                        workspaceLoadDiagnostics = snapshot.LoadDiagnostics,
                        documents = documentResults,
                        diagnostics,
                        truncated = allDiagnostics.Length > diagnostics.Length
                    });
                }
            }
        }

        var representativeSnapshot = firstSnapshot ?? throw new InvalidOperationException("No matrix evaluation was produced.");
        var data = new
        {
            project = projectName,
            documentPath,
            requestedConfigurations,
            requestedTargetFrameworks,
            variants,
            limitation = "Each target framework and Configuration is a separate MSBuildWorkspace evaluation; missing SDKs, workloads, feeds, or generated prerequisites remain visible in workspaceLoadDiagnostics."
        };
        return Wrap(data, totalItems, variants.Any(variant => (bool)variant.GetType().GetProperty("truncated")!.GetValue(variant)!), representativeSnapshot);
    }

    /// <summary>
    /// Reports whether the cached workspace loaded completely and can compile its projects.
    /// </summary>
    public async Task<object> GetWorkspaceHealthAsync(
        string workspacePath,
        string configuration,
        string? targetFramework,
        bool includeProjectChecks,
        int maxProjects,
        CancellationToken cancellationToken)
    {
        var health = await workspaceCache.GetHealthAsync(
            workspacePath,
            new WorkspaceLoadOptions(configuration, targetFramework),
            cancellationToken).ConfigureAwait(false);
        var snapshot = health.Snapshot;
        var projects = snapshot.Solution.Projects.Where(project => project.Language == LanguageNames.CSharp).ToArray();
        var limit = BoundLimit(maxProjects);
        var projectChecks = new List<object>();
        var compilationFailureCount = 0;
        if (includeProjectChecks)
        {
            foreach (var project in projects.Take(limit))
            {
                var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
                if (compilation is null)
                {
                    compilationFailureCount++;
                }

                var errors = compilation?.GetDiagnostics(cancellationToken).Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
                projectChecks.Add(new
                {
                    projectId = project.Id.Id,
                    project = project.Name,
                    project.FilePath,
                    compilationAvailable = compilation is not null,
                    compilerErrorCount = errors,
                    documentCount = project.DocumentIds.Count,
                    generatedDocumentCount = (await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false)).Count()
                });
            }
        }

        var instance = MsBuildBootstrap.RegisteredInstance;
        var expectedProjectPaths = ReadExpectedProjectPaths(health.InputPath);
        var loadedProjectPaths = projects.Select(project => project.FilePath)
            .Where(path => path is not null)
            .Select(path => Path.GetFullPath(path!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skippedProjectPaths = expectedProjectPaths.Where(path => !loadedProjectPaths.Contains(path)).ToArray();
        var data = new
        {
            normalizedPath = health.InputPath,
            rootPath = health.RootPath,
            snapshotLoadedAt = snapshot.LoadedAt,
            snapshotVersion = snapshot.Solution.Version.ToString(),
            evaluatedConfiguration = snapshot.Configuration,
            evaluatedTargetFramework = snapshot.TargetFramework,
            health.IsInvalidated,
            health.ReloadedForThisCheck,
            health.LastInvalidationReason,
            health.LastInvalidatedPath,
            health.LastInvalidatedAt,
            loadDurationMilliseconds = health.LastLoadDuration.TotalMilliseconds,
            health.ReloadCount,
            health.CacheEntryCount,
            projectCount = projects.Length,
            documentCount = projects.Sum(project => project.DocumentIds.Count),
            expectedProjectPaths,
            skippedProjectPaths,
            loadDiagnostics = snapshot.LoadDiagnostics,
            msbuild = instance is null
                ? null
                : new
                {
                    instance.Name,
                    version = instance.Version.ToString(),
                    instance.MSBuildPath,
                    discoveryType = instance.DiscoveryType.ToString()
                },
            projectChecks,
            completeEnoughForSemanticQueries = snapshot.LoadDiagnostics.Count == 0 &&
                                               skippedProjectPaths.Length == 0 &&
                                               (!includeProjectChecks || compilationFailureCount == 0)
        };
        return Wrap(data, projectChecks.Count, includeProjectChecks && projects.Length > projectChecks.Count, snapshot);
    }

    /// <summary>
    /// Normalizes explicit MSBuild configurations and supplies the deterministic Debug default.
    /// </summary>
    private static string[] NormalizeConfigurations(string[]? configurations)
    {
        var values = (configurations ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? ["Debug"] : values;
    }

    /// <summary>
    /// Normalizes target frameworks; null requests the project's default MSBuild evaluation.
    /// </summary>
    private static string?[] NormalizeTargetFrameworks(string[]? targetFrameworks)
    {
        var values = (targetFrameworks ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string?>()
            .ToArray();
        return values.Length == 0 ? [null] : values;
    }

    private static string? CreateGeneratedExcerpt(Microsoft.CodeAnalysis.Text.SourceText text)
    {
        if (text.Length == 0)
        {
            return null;
        }

        return Trim(string.Join(Environment.NewLine, text.Lines.Take(8).Select(line => line.ToString())), 800);
    }

    private static IReadOnlyList<string> ParsePreprocessorSymbols(string symbolSet)
    {
        return symbolSet.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadExpectedProjectPaths(string workspacePath)
    {
        var extension = Path.GetExtension(workspacePath);
        var root = Path.GetDirectoryName(workspacePath)!;
        try
        {
            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                return [Path.GetFullPath(workspacePath)];
            }

            if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return XDocument.Load(workspacePath).Descendants()
                    .Where(element => element.Name.LocalName == "Project")
                    .Select(element => element.Attribute("Path")?.Value)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => Path.GetFullPath(path!, root))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            {
                return Regex.Matches(File.ReadAllText(workspacePath), "^Project\\([^\\r\\n]+?=\\s*[^,]+,\\s*\"(?<path>[^\"]+\\.csproj)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant)
                    .Select(match => Path.GetFullPath(match.Groups["path"].Value, root))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (extension.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(workspacePath));
                if (document.RootElement.TryGetProperty("solution", out var solution) &&
                    solution.TryGetProperty("projects", out var selectedProjects))
                {
                    return selectedProjects.EnumerateArray()
                        .Select(element => Path.GetFullPath(element.GetString()!, root))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        return [];
    }
}
