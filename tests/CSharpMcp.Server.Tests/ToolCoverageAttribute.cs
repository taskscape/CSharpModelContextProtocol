namespace CSharpMcp.Tests;

/// <summary>
/// Associates a behavioral test with the MCP tools whose analysis contract it exercises.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
internal sealed class ToolCoverageAttribute(params string[] toolNames) : Attribute
{
    /// <summary>
    /// Gets the exact advertised MCP tool names covered by the test.
    /// </summary>
    public IReadOnlyList<string> ToolNames { get; } = toolNames;
}
