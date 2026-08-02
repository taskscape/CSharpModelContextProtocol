using Microsoft.Build.Locator;

namespace CSharpMcp.Infrastructure;

/// <summary>
/// Registers the installed .NET SDK before Roslyn loads any MSBuild assemblies.
/// </summary>
internal static class MsBuildBootstrap
{
    private static readonly object SyncRoot = new();

    /// <summary>
    /// Gets the SDK-backed MSBuild instance selected for this server process.
    /// </summary>
    public static VisualStudioInstance? RegisteredInstance { get; private set; }

    /// <summary>
    /// Registers the highest available SDK once for the current process.
    /// </summary>
    public static void Register()
    {
        lock (SyncRoot)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                RegisteredInstance = MSBuildLocator.RegisterDefaults();
            }
        }
    }
}
