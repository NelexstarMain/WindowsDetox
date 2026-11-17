using System.Reflection;
using System.Runtime.Loader;

namespace Blocker.Core.Plugins;

/// <summary>
/// Collectible load context for one compiled plugin. The Blocker SDKs, the test kit, xUnit and the framework come from
/// <see cref="AssemblyLoadContext.Default"/>, so SDK types are identical in host and plugin; any other assembly
/// reference is refused. Unload it (<see cref="AssemblyLoadContext.Unload"/>) when done.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedExact =
    [
        "mscorlib",
        "netstandard",
        "System",
        "Blocker.SDK",
        "Blocker.UI.SDK",
        "Blocker.Plugin.Testing",
        "Microsoft.Extensions.TimeProvider.Testing",
    ];

    private static readonly string[] SharedPrefixes = ["System.", "xunit."];

    /// <summary>Creates a collectible context.</summary>
    /// <param name="pluginId">Plugin id, used only to name the context.</param>
    public PluginLoadContext(string pluginId)
        : base($"plugin:{pluginId}", isCollectible: true)
    {
    }

    /// <summary>Returns whether <paramref name="name"/> is resolved from the default context.</summary>
    /// <param name="name">Referenced assembly.</param>
    /// <returns><see langword="true"/> for SDK, test kit, xUnit and framework assemblies.</returns>
    public static bool IsShared(AssemblyName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Name is { } simple
            && (SharedExact.Contains(simple, StringComparer.Ordinal) || SharedPrefixes.Any(p => simple.StartsWith(p, StringComparison.Ordinal)));
    }

    /// <summary>Loads a compiled plugin image into this context.</summary>
    /// <param name="image">PE image, optionally with an embedded PDB.</param>
    /// <returns>The plugin assembly.</returns>
    public Assembly LoadPlugin(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using var stream = new MemoryStream(image, writable: false);
        return LoadFromStream(stream);
    }

    /// <inheritdoc/>
    /// <exception cref="FileNotFoundException">The plugin references an assembly that plugins may not use.</exception>
    protected override Assembly? Load(AssemblyName assemblyName) =>
        IsShared(assemblyName)
            ? Default.LoadFromAssemblyName(assemblyName)
            : throw new FileNotFoundException($"Plugins may not reference the assembly '{assemblyName.Name}'.", assemblyName.Name);
}
