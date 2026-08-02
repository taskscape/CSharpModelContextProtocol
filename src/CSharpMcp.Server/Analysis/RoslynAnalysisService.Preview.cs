using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CSharpMcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace CSharpMcp.Analysis;

/// <summary>
/// Provides non-mutating edit previews, diagnostic comparisons, and test-impact analysis.
/// </summary>
internal sealed partial class RoslynAnalysisService
{
    private const int MaximumDiagnosticBaselines = 20;
    private readonly ConcurrentDictionary<string, DiagnosticBaseline> diagnosticBaselines = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies a semantic rename to an immutable solution and reports the resulting text and diagnostic delta.
    /// </summary>
    public async Task<object> GetRenamePreviewAsync(
        string workspacePath,
        string symbolQuery,
        string refactorKind,
        string? newName,
        string? newSignature,
        string? projectName,
        bool renameInStrings,
        bool renameInComments,
        bool renameOverloads,
        bool renameFile,
        string? expectedFingerprint,
        string? cursor,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var normalizedKind = refactorKind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("rename" or "signature"))
        {
            throw new ArgumentException("refactorKind must be rename or signature.", nameof(refactorKind));
        }

        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var symbol = await SymbolResolver.ResolveSingleAsync(snapshot.Solution, symbolQuery, projectName, cancellationToken).ConfigureAwait(false);
        var snapshotFingerprint = await CreateSolutionFingerprintAsync(snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var pageOffset = ParseCursor(cursor, $"rename-preview:{normalizedKind}", snapshotFingerprint);
        if (!string.IsNullOrWhiteSpace(expectedFingerprint) &&
            !snapshotFingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The supplied snapshot fingerprint is stale. Re-run the preview against the current workspace before applying edits.");
        }

        if (normalizedKind == "signature")
        {
            return await GetSignaturePreviewCoreAsync(
                snapshot,
                symbol,
                newSignature,
                snapshotFingerprint,
                pageOffset,
                maxResults,
                cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(newName) || !SyntaxFacts.IsValidIdentifier(newName))
        {
            throw new ArgumentException("newName must be a valid C# identifier for a rename preview.", nameof(newName));
        }

        if (symbol.Name.Equals(newName, StringComparison.Ordinal))
        {
            throw new ArgumentException("The new name is identical to the current symbol name.", nameof(newName));
        }

        var relatedSymbols = await GetRenameRelatedSymbolsAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var renameOptions = new SymbolRenameOptions(renameOverloads, renameInStrings, renameInComments, renameFile);
        var changedSolution = await Renamer.RenameSymbolAsync(
            snapshot.Solution,
            symbol,
            renameOptions,
            newName,
            cancellationToken).ConfigureAwait(false);
        var changedDocumentIds = changedSolution.GetChanges(snapshot.Solution)
            .GetProjectChanges()
            .SelectMany(change => change.GetChangedDocuments())
            .Distinct()
            .ToArray();
        var previewLimit = BoundLimit(maxResults);
        var previews = new List<object>();
        var documentPreviews = new List<object>();
        var affectedProjectIds = new HashSet<ProjectId>();
        var allLocations = 0;
        foreach (var documentId in changedDocumentIds)
        {
            var originalDocument = snapshot.Solution.GetDocument(documentId)
                ?? throw new InvalidOperationException("A rename location no longer maps to a source document.");
            var originalText = await originalDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var changedDocument = changedSolution.GetDocument(documentId)
                ?? throw new InvalidOperationException("Roslyn removed a changed document during rename preview.");
            var changedText = await changedDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var changes = changedText.GetTextChanges(originalText).OrderBy(change => change.Span.Start).ToArray();
            allLocations += changes.Length;
            affectedProjectIds.Add(originalDocument.Project.Id);
            documentPreviews.Add(new
            {
                projectId = originalDocument.Project.Id.Id,
                project = originalDocument.Project.Name,
                documentId = originalDocument.Id.Id,
                file = originalDocument.FilePath,
                changeCount = changes.Length,
                originalChecksum = Convert.ToHexString(originalText.GetChecksum().AsSpan()),
                changedChecksum = Convert.ToHexString(changedText.GetChecksum().AsSpan()),
                editsReturnedSeparately = true
            });

            foreach (var change in changes)
            {
                var changeIndex = allLocations - changes.Length + Array.IndexOf(changes, change);
                if (changeIndex < pageOffset)
                {
                    continue;
                }

                if (previews.Count >= previewLimit)
                {
                    break;
                }

                var line = originalText.Lines.GetLineFromPosition(change.Span.Start);
                previews.Add(new
                {
                    project = originalDocument.Project.Name,
                    file = originalDocument.FilePath,
                    line = line.LineNumber + 1,
                    column = change.Span.Start - line.Start + 1,
                    oldText = originalText.ToString(change.Span),
                    newText = change.NewText,
                    excerpt = Trim(line.ToString().Trim(), MaximumExcerptLength)
                });
            }
        }

        var originalDiagnostics = await CollectDiagnosticRecordsAsync(
            snapshot.Solution,
            affectedProjectIds,
            DiagnosticSeverity.Warning,
            includeAnalyzers: false,
            cancellationToken).ConfigureAwait(false);
        var renamedDiagnostics = await CollectDiagnosticRecordsAsync(
            changedSolution,
            affectedProjectIds,
            DiagnosticSeverity.Warning,
            includeAnalyzers: false,
            cancellationToken).ConfigureAwait(false);
        var originalKeys = originalDiagnostics.Select(record => record.StableKey).ToHashSet(StringComparer.Ordinal);
        var introduced = renamedDiagnostics.Where(record => !originalKeys.Contains(record.StableKey)).Take(200).ToArray();
        var containingConflict = symbol.ContainingType?.GetMembers(newName)
            .Where(member => !relatedSymbols.Any(related => SymbolEqualityComparer.Default.Equals(related, member)))
            .Select(SymbolResolver.GetStableId)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var roslynConflictAnnotations = new List<object>();
        foreach (var documentId in changedDocumentIds)
        {
            var changedDocument = changedSolution.GetDocument(documentId);
            var changedRoot = changedDocument is null
                ? null
                : await changedDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (changedDocument is null || changedRoot is null)
            {
                continue;
            }

            foreach (var annotated in changedRoot.GetAnnotatedNodesAndTokens(ConflictAnnotation.Kind).Take(50 - roslynConflictAnnotations.Count))
            {
                var conflictLocation = annotated.GetLocation();
                if (conflictLocation is null)
                {
                    continue;
                }

                roslynConflictAnnotations.Add(new
                {
                    location = await CreatePositionAsync(changedDocument, conflictLocation, cancellationToken).ConfigureAwait(false),
                    descriptions = annotated.GetAnnotations(ConflictAnnotation.Kind)
                        .Select(annotation => annotation.Data)
                        .Where(data => !string.IsNullOrWhiteSpace(data))
                        .ToArray()
                });
            }

            if (roslynConflictAnnotations.Count >= 50)
            {
                break;
            }
        }

        var relatedDescriptors = new List<SymbolDescriptor>(relatedSymbols.Count);
        foreach (var related in relatedSymbols)
        {
            relatedDescriptors.Add(await DescribeSymbolAsync(related, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        var data = new
        {
            symbol = await DescribeSymbolAsync(symbol, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            refactorKind = normalizedKind,
            newName,
            options = new { renameInStrings, renameInComments, renameOverloads, renameFile },
            relatedSymbols = relatedDescriptors,
            changedDocuments = changedDocumentIds.Length,
            totalChanges = allLocations,
            pageOffset,
            nextCursor = pageOffset + previews.Count < allLocations
                ? CreateCursor($"rename-preview:{normalizedKind}", snapshotFingerprint, pageOffset + previews.Count)
                : null,
            changes = previews,
            documents = documentPreviews,
            freshness = new
            {
                snapshotLoadedAt = snapshot.LoadedAt,
                snapshotFingerprint,
                expectedFingerprintAccepted = expectedFingerprint,
                requirement = "Re-run rename_preview if repository files change before applying this preview."
            },
            conflicts = new
            {
                existingMembersWithNewName = containingConflict,
                roslynConflictAnnotations,
                introducedDiagnostics = introduced,
                hasPotentialConflict = containingConflict.Length > 0 || roslynConflictAnnotations.Count > 0 ||
                    introduced.Any(record => record.Severity == DiagnosticSeverity.Error.ToString())
            },
            appliedToDisk = false
        };
        return Wrap(data, previews.Count, allLocations > previews.Count, snapshot);
    }

    /// <summary>
    /// Produces a stable identifier for the immutable solution version used by a preview.
    /// </summary>
    private static async Task<string> CreateSolutionFingerprintAsync(Solution solution, CancellationToken cancellationToken)
    {
        var documents = solution.Projects
            .OrderBy(project => project.FilePath, StringComparer.OrdinalIgnoreCase)
            .SelectMany(project => project.Documents.OrderBy(document => document.FilePath, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var identities = new List<string>(documents.Length);
        foreach (var document in documents)
        {
            var version = await document.GetTextVersionAsync(cancellationToken).ConfigureAwait(false);
            identities.Add($"{document.FilePath}:{version}");
        }

        var input = string.Join('|', identities);
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
    }

    /// <summary>
    /// Produces a compiler-bound signature preview for a method and its related declarations and call sites.
    /// </summary>
    private async Task<object> GetSignaturePreviewCoreAsync(
        WorkspaceSnapshot snapshot,
        ISymbol symbol,
        string? newSignature,
        string snapshotFingerprint,
        int pageOffset,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (symbol is not IMethodSymbol method)
        {
            throw new ArgumentException("A signature preview requires a method or constructor symbol.", nameof(symbol));
        }

        var newParameterList = ParseSignatureParameterList(newSignature);
        var relatedSymbols = await GetRenameRelatedSymbolsAsync(method, snapshot.Solution, cancellationToken).ConfigureAwait(false);
        var relatedMethods = relatedSymbols.OfType<IMethodSymbol>()
            .Append(method)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        var changesByDocument = new Dictionary<DocumentId, List<TextChange>>();

        foreach (var relatedMethod in relatedMethods)
        {
            foreach (var syntaxReference in relatedMethod.DeclaringSyntaxReferences)
            {
                var declaration = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                var parameterList = declaration switch
                {
                    BaseMethodDeclarationSyntax baseMethod => baseMethod.ParameterList,
                    _ => null
                };
                var document = snapshot.Solution.GetDocument(syntaxReference.SyntaxTree);
                if (parameterList is null || document is null)
                {
                    continue;
                }

                AddPreviewChange(
                    changesByDocument,
                    document.Id,
                    new TextChange(parameterList.Span, newParameterList.WithTriviaFrom(parameterList).ToFullString()));
            }

            var referenceGroups = await SymbolFinder.FindReferencesAsync(
                relatedMethod,
                snapshot.Solution,
                cancellationToken).ConfigureAwait(false);
            foreach (var reference in referenceGroups.SelectMany(group => group.Locations))
            {
                var root = await reference.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await reference.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var node = root?.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
                if (node is null || model is null ||
                    !TryCreateSignatureCallSiteChange(node, model, newParameterList, cancellationToken, out var textChange))
                {
                    continue;
                }

                AddPreviewChange(changesByDocument, reference.Document.Id, textChange);
            }
        }

        var duplicateOrOverlappingChanges = changesByDocument.Values
            .SelectMany(changes => changes)
            .GroupBy(change => (change.Span.Start, change.Span.Length, change.NewText))
            .Sum(group => group.Count() - 1);
        var changedSolution = snapshot.Solution;
        var affectedProjectIds = new HashSet<ProjectId>();
        foreach (var (documentId, rawChanges) in changesByDocument)
        {
            var document = snapshot.Solution.GetDocument(documentId)
                ?? throw new InvalidOperationException("A signature change no longer maps to a source document.");
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var changes = rawChanges.Distinct().OrderByDescending(change => change.Span.Start).ToArray();
            for (var index = 1; index < changes.Length; index++)
            {
                if (changes[index - 1].Span.OverlapsWith(changes[index].Span))
                {
                    throw new InvalidOperationException("Roslyn produced overlapping signature edits; narrow the symbol query and retry.");
                }
            }

            changedSolution = changedSolution.WithDocumentText(documentId, text.WithChanges(changes));
            affectedProjectIds.Add(document.Project.Id);
        }

        var previewLimit = BoundLimit(maxResults);
        var previews = new List<object>();
        var documents = new List<object>();
        var totalChanges = 0;
        foreach (var (documentId, rawChanges) in changesByDocument.OrderBy(pair => snapshot.Solution.GetDocument(pair.Key)?.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var originalDocument = snapshot.Solution.GetDocument(documentId)!;
            var originalText = await originalDocument.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var changedText = await changedSolution.GetDocument(documentId)!.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var changes = rawChanges.Distinct().OrderBy(change => change.Span.Start).ToArray();
            totalChanges += changes.Length;
            documents.Add(new
            {
                projectId = originalDocument.Project.Id.Id,
                project = originalDocument.Project.Name,
                documentId = originalDocument.Id.Id,
                file = originalDocument.FilePath,
                changeCount = changes.Length,
                originalChecksum = Convert.ToHexString(originalText.GetChecksum().AsSpan()),
                changedChecksum = Convert.ToHexString(changedText.GetChecksum().AsSpan()),
                editsReturnedSeparately = true
            });
            foreach (var change in changes)
            {
                var changeIndex = totalChanges - changes.Length + Array.IndexOf(changes, change);
                if (changeIndex < pageOffset)
                {
                    continue;
                }

                if (previews.Count >= previewLimit)
                {
                    break;
                }

                previews.Add(CreateTextEdit(change, originalText));
            }
        }

        var originalDiagnostics = await CollectDiagnosticRecordsAsync(
            snapshot.Solution,
            affectedProjectIds,
            DiagnosticSeverity.Warning,
            includeAnalyzers: false,
            cancellationToken).ConfigureAwait(false);
        var changedDiagnostics = await CollectDiagnosticRecordsAsync(
            changedSolution,
            affectedProjectIds,
            DiagnosticSeverity.Warning,
            includeAnalyzers: false,
            cancellationToken).ConfigureAwait(false);
        var originalKeys = originalDiagnostics.Select(record => record.StableKey).ToHashSet(StringComparer.Ordinal);
        var introduced = changedDiagnostics.Where(record => !originalKeys.Contains(record.StableKey)).Take(200).ToArray();
        var relatedDescriptors = new List<SymbolDescriptor>();
        foreach (var related in relatedMethods)
        {
            relatedDescriptors.Add(await DescribeSymbolAsync(related, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        return Wrap(new
        {
            refactorKind = "signature",
            symbol = await DescribeSymbolAsync(method, snapshot.Solution, cancellationToken).ConfigureAwait(false),
            requestedSignature = newSignature,
            normalizedParameterList = newParameterList.ToString(),
            relatedSymbols = relatedDescriptors,
            changedDocuments = documents.Count,
            totalChanges,
            pageOffset,
            nextCursor = pageOffset + previews.Count < totalChanges
                ? CreateCursor("rename-preview:signature", snapshotFingerprint, pageOffset + previews.Count)
                : null,
            changes = previews,
            documents,
            freshness = new
            {
                snapshotLoadedAt = snapshot.LoadedAt,
                snapshotFingerprint,
                requirement = "Pass this fingerprint as expectedFingerprint before relying on a subsequent preview."
            },
            conflicts = new
            {
                introducedDiagnostics = introduced,
                hasPotentialConflict = introduced.Any(record => record.Severity == DiagnosticSeverity.Error.ToString())
            },
            duplicateEditsRemoved = duplicateOrOverlappingChanges,
            limitation = "Added required parameters receive compiler-valid default(Type) arguments. Review those placeholders for intended runtime values before applying the patch.",
            appliedToDisk = false
        }, previews.Count, totalChanges > previews.Count, snapshot);
    }

    /// <summary>
    /// Parses a caller-supplied parameter list without accepting a method body or unrelated syntax.
    /// </summary>
    private static ParameterListSyntax ParseSignatureParameterList(string? newSignature)
    {
        if (string.IsNullOrWhiteSpace(newSignature))
        {
            throw new ArgumentException("newSignature is required for a signature preview.", nameof(newSignature));
        }

        var text = newSignature.Trim();
        var openParenthesis = text.IndexOf('(');
        if (openParenthesis < 0)
        {
            throw new ArgumentException("newSignature must contain a C# parameter list.", nameof(newSignature));
        }

        var parameterText = text[openParenthesis..];
        var parsed = SyntaxFactory.ParseMemberDeclaration($"void __SignaturePreview{parameterText} {{ }}") as MethodDeclarationSyntax;
        if (parsed is null || parsed.ContainsDiagnostics || !parameterText.EndsWith(')'))
        {
            throw new ArgumentException("newSignature must be a valid C# parameter list such as '(int id, string name)'.", nameof(newSignature));
        }

        return parsed.ParameterList.WithoutTrivia();
    }

    /// <summary>
    /// Rewrites one compiler-bound invocation or construction argument list to the requested parameter order.
    /// </summary>
    private static bool TryCreateSignatureCallSiteChange(
        SyntaxNode node,
        SemanticModel model,
        ParameterListSyntax newParameterList,
        CancellationToken cancellationToken,
        out TextChange textChange)
    {
        var call = node.AncestorsAndSelf().FirstOrDefault(candidate =>
            candidate is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ConstructorInitializerSyntax);
        var (argumentList, calledMethod) = call switch
        {
            InvocationExpressionSyntax invocation => (invocation.ArgumentList, model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol),
            ObjectCreationExpressionSyntax creation => (creation.ArgumentList, model.GetSymbolInfo(creation, cancellationToken).Symbol as IMethodSymbol),
            ConstructorInitializerSyntax initializer => (initializer.ArgumentList, model.GetSymbolInfo(initializer, cancellationToken).Symbol as IMethodSymbol),
            _ => (null, null)
        };
        if (argumentList is null || calledMethod is null)
        {
            textChange = default;
            return false;
        }

        var oldArguments = new Dictionary<string, ArgumentSyntax>(StringComparer.Ordinal);
        for (var index = 0; index < argumentList.Arguments.Count; index++)
        {
            var argument = argumentList.Arguments[index];
            var parameterName = argument.NameColon?.Name.Identifier.ValueText ??
                                (index < calledMethod.Parameters.Length ? calledMethod.Parameters[index].Name : null);
            if (parameterName is not null)
            {
                oldArguments[parameterName] = argument;
            }
        }

        var rewritten = new List<ArgumentSyntax>();
        foreach (var parameter in newParameterList.Parameters.Where(parameter => !parameter.Modifiers.Any(SyntaxKind.ThisKeyword)))
        {
            var name = parameter.Identifier.ValueText;
            if (oldArguments.TryGetValue(name, out var existing))
            {
                rewritten.Add(existing.WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(name))));
                continue;
            }

            if (parameter.Default is not null)
            {
                continue;
            }

            var type = parameter.Type ?? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));
            rewritten.Add(SyntaxFactory.Argument(SyntaxFactory.DefaultExpression(type.WithoutTrivia()))
                .WithNameColon(SyntaxFactory.NameColon(SyntaxFactory.IdentifierName(name))));
        }

        var replacement = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(rewritten)).WithTriviaFrom(argumentList);
        textChange = new TextChange(argumentList.Span, replacement.ToFullString());
        return true;
    }

    /// <summary>
    /// Adds one edit while preserving document grouping for immutable solution updates.
    /// </summary>
    private static void AddPreviewChange(
        IDictionary<DocumentId, List<TextChange>> changesByDocument,
        DocumentId documentId,
        TextChange change)
    {
        if (!changesByDocument.TryGetValue(documentId, out var changes))
        {
            changes = [];
            changesByDocument[documentId] = changes;
        }

        changes.Add(change);
    }

    /// <summary>
    /// Creates one structured text edit with original source coordinates.
    /// </summary>
    private static object CreateTextEdit(TextChange change, SourceText originalText)
    {
        var line = originalText.Lines.GetLineFromPosition(change.Span.Start);
        return new
        {
            start = change.Span.Start,
            length = change.Span.Length,
            line = line.LineNumber + 1,
            column = change.Span.Start - line.Start + 1,
            oldText = originalText.ToString(change.Span),
            newText = change.NewText,
            excerpt = Trim(line.ToString().Trim(), MaximumExcerptLength)
        };
    }

    /// <summary>
    /// Captures or compares a bounded compiler/analyzer diagnostic baseline.
    /// </summary>
    public async Task<object> GetDiagnosticsDeltaAsync(
        string workspacePath,
        string? baselineToken,
        string? projectName,
        string minimumSeverity,
        bool includeAnalyzers,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var severity = ParseSeverity(minimumSeverity);
        var selectedProjectIds = SelectProjects(snapshot.Solution, projectName).Select(project => project.Id).ToHashSet();
        var current = await CollectDiagnosticRecordsAsync(
            snapshot.Solution,
            selectedProjectIds,
            severity,
            includeAnalyzers,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(baselineToken))
        {
            var token = Guid.NewGuid().ToString("N");
            var baseline = new DiagnosticBaseline(
                token,
                Path.GetFullPath(workspacePath),
                projectName,
                severity,
                includeAnalyzers,
                DateTimeOffset.UtcNow,
                current);
            diagnosticBaselines[token] = baseline;
            TrimDiagnosticBaselines();
            return Wrap(new
            {
                baselineToken = token,
                capturedAt = baseline.CapturedAt,
                projectName,
                minimumSeverity = severity.ToString(),
                includeAnalyzers,
                diagnosticCount = current.Count,
                counts = CountDiagnostics(current)
            }, current.Count, false, snapshot);
        }

        if (!diagnosticBaselines.TryGetValue(baselineToken, out var stored))
        {
            throw new KeyNotFoundException("The diagnostic baseline token is unknown or has expired.");
        }

        if (!stored.WorkspacePath.Equals(Path.GetFullPath(workspacePath), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(stored.ProjectName, projectName, StringComparison.OrdinalIgnoreCase) ||
            stored.MinimumSeverity != severity ||
            stored.IncludeAnalyzers != includeAnalyzers)
        {
            throw new ArgumentException("The baseline token was captured with different workspace, project, severity, or analyzer options.", nameof(baselineToken));
        }

        var limit = BoundLimit(maxResults);
        var beforeKeys = stored.Diagnostics.Select(record => record.StableKey).ToHashSet(StringComparer.Ordinal);
        var currentKeys = current.Select(record => record.StableKey).ToHashSet(StringComparer.Ordinal);
        var introducedAll = current.Where(record => !beforeKeys.Contains(record.StableKey)).ToArray();
        var resolvedAll = stored.Diagnostics.Where(record => !currentKeys.Contains(record.StableKey)).ToArray();
        var introduced = introducedAll.Take(limit).ToArray();
        var remaining = Math.Max(0, limit - introduced.Length);
        var resolved = resolvedAll.Take(remaining).ToArray();

        return Wrap(new
        {
            baselineToken,
            capturedAt = stored.CapturedAt,
            comparedAt = DateTimeOffset.UtcNow,
            introduced,
            resolved,
            counts = new
            {
                baseline = stored.Diagnostics.Count,
                current = current.Count,
                introduced = introducedAll.Length,
                resolved = resolvedAll.Length
            }
        }, introduced.Length + resolved.Length, introducedAll.Length > introduced.Length || resolvedAll.Length > resolved.Length, snapshot);
    }

    /// <summary>
    /// Finds test methods that can reach one or more changed source symbols through compiler-bound references.
    /// </summary>
    public async Task<object> GetTestImpactAsync(
        string workspacePath,
        string[]? symbols,
        string[]? documentPaths,
        string? projectName,
        int maxDepth,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if ((symbols is null || symbols.Length == 0) && (documentPaths is null || documentPaths.Length == 0))
        {
            throw new ArgumentException("At least one symbol or document path is required.");
        }

        var snapshot = await workspaceCache.GetSnapshotAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        var seeds = new List<ISymbol>();
        foreach (var query in symbols ?? [])
        {
            seeds.Add(await SymbolResolver.ResolveSingleAsync(snapshot.Solution, query, projectName, cancellationToken).ConfigureAwait(false));
        }

        foreach (var path in documentPaths ?? [])
        {
            var document = FindDocument(snapshot.Solution, path);
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
            {
                continue;
            }

            seeds.AddRange(root.DescendantNodesAndSelf()
                .Select(node => model.GetDeclaredSymbol(node, cancellationToken))
                .Where(symbol => symbol is not null)
                .Cast<ISymbol>());
        }

        seeds = seeds.GroupBy(SymbolResolver.GetStableId, StringComparer.Ordinal).Select(group => group.First()).ToList();
        var depthLimit = Math.Clamp(maxDepth, 1, 5);
        var resultLimit = BoundLimit(maxResults);
        var visited = new HashSet<string>(seeds.Select(SymbolResolver.GetStableId), StringComparer.Ordinal);
        var queue = new Queue<(ISymbol Symbol, int Depth, IReadOnlyList<string> Path)>();
        foreach (var seed in seeds)
        {
            queue.Enqueue((seed, 0, [SymbolResolver.GetStableId(seed)]));
        }

        var impacted = new Dictionary<string, (IMethodSymbol Method, int Depth, IReadOnlyList<string> Path, SourcePosition Evidence)>(StringComparer.Ordinal);
        var traversedReferences = 0;
        while (queue.Count > 0 && impacted.Count < resultLimit)
        {
            var (current, depth, path) = queue.Dequeue();
            if (depth >= depthLimit)
            {
                continue;
            }

            var references = await SymbolFinder.FindReferencesAsync(current, snapshot.Solution, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var reference in references.SelectMany(group => group.Locations).Take(2_000))
            {
                traversedReferences++;
                var model = await reference.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (model?.GetEnclosingSymbol(reference.Location.SourceSpan.Start, cancellationToken) is not IMethodSymbol caller)
                {
                    continue;
                }

                var callerId = SymbolResolver.GetStableId(caller);
                var callerPath = path.Append(callerId).ToArray();
                var evidence = await CreatePositionAsync(reference.Document, reference.Location, cancellationToken).ConfigureAwait(false);
                if (IsTestProject(reference.Document.Project))
                {
                    if (!impacted.TryGetValue(callerId, out var existing) || existing.Depth > depth + 1)
                    {
                        impacted[callerId] = (caller, depth + 1, callerPath, evidence);
                    }
                }

                if (visited.Add(callerId))
                {
                    queue.Enqueue((caller, depth + 1, callerPath));
                }
            }
        }

        var tests = new List<object>();
        foreach (var item in impacted.Values.OrderBy(value => value.Depth).ThenBy(value => SymbolResolver.GetStableId(value.Method), StringComparer.Ordinal).Take(resultLimit))
        {
            tests.Add(new
            {
                symbol = await DescribeSymbolAsync(item.Method, snapshot.Solution, cancellationToken).ConfigureAwait(false),
                depth = item.Depth,
                confidence = HasTestAttribute(item.Method) ? "high" : "medium",
                evidencePath = item.Path,
                reference = item.Evidence
            });
        }

        var seedDescriptors = new List<SymbolDescriptor>(seeds.Count);
        foreach (var seed in seeds)
        {
            seedDescriptors.Add(await DescribeSymbolAsync(seed, snapshot.Solution, cancellationToken).ConfigureAwait(false));
        }

        var data = new
        {
            seeds = seedDescriptors,
            maxDepth = depthLimit,
            tests,
            traversedReferences,
            limitations = new[]
            {
                "Reflection, runtime discovery, configuration, generated tests, and external test assemblies are not resolved.",
                "A reported test is a static dependency candidate, not proof that the changed behavior executes at runtime."
            }
        };
        return Wrap(data, tests.Count, queue.Count > 0 || impacted.Count >= resultLimit, snapshot);
    }

    private static async Task<IReadOnlyList<ISymbol>> GetRenameRelatedSymbolsAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var results = new List<ISymbol> { symbol };
        var implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
        results.AddRange(implementations);
        var overrides = await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
        results.AddRange(overrides);

        if (symbol is IMethodSymbol method)
        {
            results.AddRange(method.ExplicitInterfaceImplementations);
            if (method.OverriddenMethod is not null)
            {
                results.Add(method.OverriddenMethod);
            }
        }
        else if (symbol is IPropertySymbol property)
        {
            results.AddRange(property.ExplicitInterfaceImplementations);
            if (property.OverriddenProperty is not null)
            {
                results.Add(property.OverriddenProperty);
            }
        }

        return results.GroupBy(SymbolResolver.GetStableId, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

    private static async Task<IReadOnlyList<(Document Document, TextSpan Span)>> GetRenameLocationsAsync(
        IReadOnlyList<ISymbol> symbols,
        Solution solution,
        CancellationToken cancellationToken)
    {
        var results = new List<(Document Document, TextSpan Span)>();
        foreach (var symbol in symbols)
        {
            foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                var document = solution.GetDocument(syntaxReference.SyntaxTree);
                if (document is null)
                {
                    continue;
                }

                var syntax = await syntaxReference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                var identifier = syntax.DescendantTokens().FirstOrDefault(token => token.ValueText.Equals(symbol.Name, StringComparison.Ordinal));
                if (identifier.RawKind != 0)
                {
                    results.Add((document, identifier.Span));
                }
            }

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false);
            results.AddRange(references.SelectMany(group => group.Locations)
                .Where(location => !location.IsImplicit)
                .Select(location => (location.Document, location.Location.SourceSpan)));
        }

        return results
            .GroupBy(item => $"{item.Document.Id}:{item.Span.Start}:{item.Span.Length}", StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static async Task AddTextualRenameLocationsAsync(
        Solution solution,
        string oldName,
        bool renameInStrings,
        bool renameInComments,
        IDictionary<DocumentId, List<TextSpan>> locations,
        CancellationToken cancellationToken)
    {
        var namePattern = new Regex($@"\b{Regex.Escape(oldName)}\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        foreach (var document in solution.Projects.SelectMany(project => project.Documents))
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
            {
                continue;
            }

            foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
            {
                if (renameInStrings && token.IsKind(SyntaxKind.StringLiteralToken))
                {
                    AddMatches(token.Span, token.Text, document.Id, namePattern, locations);
                }

                if (renameInComments)
                {
                    foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia).Where(trivia =>
                                 trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) ||
                                 trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) ||
                                 trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                                 trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)))
                    {
                        AddMatches(trivia.Span, trivia.ToString(), document.Id, namePattern, locations);
                    }
                }
            }
        }
    }

    private static void AddMatches(
        TextSpan containerSpan,
        string text,
        DocumentId documentId,
        Regex pattern,
        IDictionary<DocumentId, List<TextSpan>> locations)
    {
        if (!locations.TryGetValue(documentId, out var spans))
        {
            spans = [];
            locations[documentId] = spans;
        }

        spans.AddRange(pattern.Matches(text).Select(match => new TextSpan(containerSpan.Start + match.Index, match.Length)));
    }

    private static async Task<IReadOnlyList<DiagnosticRecord>> CollectDiagnosticRecordsAsync(
        Solution solution,
        IReadOnlySet<ProjectId> projectIds,
        DiagnosticSeverity minimumSeverity,
        bool includeAnalyzers,
        CancellationToken cancellationToken)
    {
        var results = new List<DiagnosticRecord>();
        foreach (var project in solution.Projects.Where(project => projectIds.Contains(project.Id) && project.Language == LanguageNames.CSharp))
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                continue;
            }

            ImmutableArray<Diagnostic> diagnostics;
            var analyzers = includeAnalyzers
                ? project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(project.Language)).ToImmutableArray()
                : [];
            if (analyzers.Length > 0)
            {
                diagnostics = await compilation.WithAnalyzers(analyzers).GetAllDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                diagnostics = compilation.GetDiagnostics(cancellationToken);
            }

            foreach (var diagnostic in diagnostics.Where(diagnostic => diagnostic.Severity >= minimumSeverity))
            {
                var lineSpan = diagnostic.Location.GetLineSpan();
                results.Add(new DiagnosticRecord(
                    project.Name,
                    diagnostic.Id,
                    diagnostic.Severity.ToString(),
                    diagnostic.GetMessage(),
                    lineSpan.Path,
                    diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Line + 1 : null,
                    diagnostic.Location.IsInSource ? lineSpan.StartLinePosition.Character + 1 : null));
            }
        }

        return results.OrderByDescending(record => record.Severity, StringComparer.Ordinal)
            .ThenBy(record => record.Project, StringComparer.Ordinal)
            .ThenBy(record => record.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(record => record.Line)
            .ToArray();
    }

    private void TrimDiagnosticBaselines()
    {
        foreach (var baseline in diagnosticBaselines.Values
                     .OrderByDescending(item => item.CapturedAt)
                     .Skip(MaximumDiagnosticBaselines))
        {
            diagnosticBaselines.TryRemove(baseline.Token, out _);
        }
    }

    private static IReadOnlyDictionary<string, int> CountDiagnostics(IEnumerable<DiagnosticRecord> diagnostics)
    {
        return diagnostics.GroupBy(record => record.Severity)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }

    private static bool HasTestAttribute(IMethodSymbol method)
    {
        return method.GetAttributes().Any(attribute => attribute.AttributeClass?.Name is
            "FactAttribute" or "TheoryAttribute" or "TestAttribute" or "TestCaseAttribute" or "TestMethodAttribute" or "DataTestMethodAttribute");
    }

    private sealed record DiagnosticBaseline(
        string Token,
        string WorkspacePath,
        string? ProjectName,
        DiagnosticSeverity MinimumSeverity,
        bool IncludeAnalyzers,
        DateTimeOffset CapturedAt,
        IReadOnlyList<DiagnosticRecord> Diagnostics);

    private sealed record DiagnosticRecord(
        string Project,
        string Id,
        string Severity,
        string Message,
        string? File,
        int? Line,
        int? Column)
    {
        public string StableKey => $"{Project}|{Id}|{Severity}|{Message}|{File}|{Line}|{Column}";
    }
}
