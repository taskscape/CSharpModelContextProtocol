namespace CSharpMcp.Tools;

/// <summary>
/// Selects optional, high-cost MCP tool groups without adding aliases or mutable session state.
/// </summary>
internal sealed class ToolCatalogProfile
{
    private static readonly IReadOnlyDictionary<string, string> OptionalTools =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["api_compatibility"] = "api",
            ["architecture_rule_check"] = "architecture"
        };

    private readonly IReadOnlySet<string> enabledGroups;

    private ToolCatalogProfile(IReadOnlySet<string> enabledGroups)
    {
        this.enabledGroups = enabledGroups;
    }

    /// <summary>
    /// Reads the process-level catalog profile from CSHARPMCP_TOOL_GROUPS.
    /// </summary>
    public static ToolCatalogProfile FromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("CSHARPMCP_TOOL_GROUPS") ?? string.Empty;
        var groups = configured.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static group => group.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return new ToolCatalogProfile(groups);
    }

    /// <summary>
    /// Returns whether a tool belongs in this server process's advertised catalog.
    /// </summary>
    public bool IsEnabled(string toolName)
    {
        return !OptionalTools.TryGetValue(toolName, out var group) ||
               enabledGroups.Contains("all") ||
               enabledGroups.Contains(group);
    }

    /// <summary>
    /// Rejects direct calls to tools hidden by the active feature profile.
    /// </summary>
    public void EnsureEnabled(string toolName)
    {
        if (!IsEnabled(toolName))
        {
            var group = OptionalTools[toolName];
            throw new InvalidOperationException(
                $"Tool '{toolName}' is feature-gated. Add '{group}' to CSHARPMCP_TOOL_GROUPS and restart the server.");
        }
    }
}
