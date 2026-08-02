using System.Text.Json;

namespace CSharpMcp.Tools;

/// <summary>
/// Defines the stable MCP structured-output envelope shared by Roslyn analysis tools.
/// </summary>
/// <remarks>
/// Tool-specific data remains open because every Roslyn operation has a distinct bounded payload,
/// while metadata is strongly typed so MCP clients validate completeness, pagination, and workspace state.
/// </remarks>
public sealed record McpToolResponse(
    IReadOnlyDictionary<string, JsonElement> Data,
    McpResultMetadata Metadata);

/// <summary>
/// Describes the shared, schema-validated metadata returned by every tool.
/// </summary>
public sealed record McpResultMetadata(
    string Kind,
    int? Returned = null,
    bool? Truncated = null,
    DateTimeOffset? WorkspaceLoadedAt = null,
    IReadOnlyList<string>? WorkspaceDiagnostics = null,
    string? NextCursor = null);
