using System.Reflection;
using System.Runtime.CompilerServices;
using Blocker.Plugin.Testing.Runner;
using Blocker.Sdk;
using Microsoft.CodeAnalysis.CSharp;

namespace Blocker.Core.Plugins;

/// <summary>Stages of <see cref="PluginInstallPipeline"/>, in execution order.</summary>
public enum PluginInstallStage
{
    /// <summary>ZIP structure, names, sizes and encoding.</summary>
    Package,

    /// <summary><c>plugin.json</c> schema.</summary>
    Manifest,

    /// <summary>Static API policy analysis.</summary>
    Policy,

    /// <summary>Roslyn compilation.</summary>
    Compile,

    /// <summary>Loading the assembly and resolving the entry type.</summary>
    Load,

    /// <summary>Running the plugin's own tests.</summary>
    Tests,

    /// <summary>Every stage passed; the plugin may be installed.</summary>
    Ready,
}

/// <summary>Result of <see cref="PluginInstallPipeline.EvaluateAsync"/>.</summary>
/// <param name="Stage">The stage that failed, or <see cref="PluginInstallStage.Ready"/> when none did.</param>
/// <param name="Errors">User-facing errors; empty on success.</param>
/// <param name="TestReport">Test results; set once the tests ran.</param>
/// <param name="Manifest">The manifest; set once it was parsed (also on later failures, so the UI can show what was uploaded).</param>
public sealed record PluginInstallReport(
    PluginInstallStage Stage,
    IReadOnlyList<string> Errors,
    PluginTestReport? TestReport = null,
    PluginManifest? Manifest = null)
{
    /// <summary>Gets whether the plugin passed every stage, including all of its tests.</summary>
    public bool Succeeded => Stage == PluginInstallStage.Ready;
}

/// <summary>Result of <see cref="PluginInstallPipeline.BuildAsync"/>.</summary>
/// <param name="Manifest">The manifest; set once it was parsed.</param>
/// <param name="Image">The compiled plugin; set when every stage up to compilation passed.</param>
/// <param name="Failure">The failure report; set when <paramref name="Image"/> is <see langword="null"/>.</param>
public sealed record PluginBuildResult(PluginManifest? Manifest, byte[]? Image, PluginInstallReport? Failure);

/// <summary>
/// Validates, analyzes, compiles, loads and tests an uploaded plugin ZIP: package, manifest, API policy, compile, load,
/// tests. The plugin is unloaded again before the method returns.
/// </summary>
/// <remarks>
/// Run this inside <c>Blocker.PluginHost</c> (the restricted-token sandbox process): the plugin's tests execute
/// untrusted code, and the static API policy is a first line of defense, not a sandbox.
/// </remarks>
public static class PluginInstallPipeline
{
    /// <summary>Evaluates a plugin ZIP.</summary>
    /// <param name="zip">Readable stream with the ZIP. It is not disposed.</param>
    /// <param name="cancellationToken">Cancels the evaluation with <see cref="OperationCanceledException"/>.</param>
    /// <returns>A report; it never throws for problems in the plugin.</returns>
    public static async Task<PluginInstallReport> EvaluateAsync(Stream zip, CancellationToken cancellationToken = default)
    {
        PluginBuildResult build = await BuildAsync(zip, cancellationToken).ConfigureAwait(false);
        return build is { Manifest: { } manifest, Image: { } image }
            ? await LoadAndTestAsync(manifest, image, cancellationToken).ConfigureAwait(false)
            : build.Failure ?? new PluginInstallReport(PluginInstallStage.Package, ["The package could not be built."]);
    }

    /// <summary>
    /// Runs the stages up to and including compilation (package, manifest, API policy, compile) without loading or testing the plugin.
    /// The plugin host uses it on every start to re-validate installed plugins; tests run only at install.
    /// </summary>
    /// <param name="zip">Readable stream with the ZIP. It is not disposed.</param>
    /// <param name="cancellationToken">Cancels the build with <see cref="OperationCanceledException"/>.</param>
    /// <returns>The compiled image, or the failure report; it never throws for problems in the plugin.</returns>
    public static async Task<PluginBuildResult> BuildAsync(Stream zip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        PluginPackageReadResult read = await PluginPackageReader.ReadAsync(zip, cancellationToken).ConfigureAwait(false);
        if (read.Package is not { } package)
        {
            return new PluginBuildResult(null, null, new PluginInstallReport(read.FailedStage ?? PluginInstallStage.Package, read.Errors));
        }

        PluginManifest manifest = package.Manifest;
        CSharpCompilation compilation = PluginCompiler.CreateCompilation(package);
        IReadOnlyList<PluginDiagnostic> violations = ApiPolicyAnalyzer.Analyze(compilation, manifest.Capabilities.ToHashSet(), cancellationToken);
        if (violations.Count > 0)
        {
            return new PluginBuildResult(manifest, null, Failed(PluginInstallStage.Policy, manifest, [.. violations.Select(v => v.ToString())]));
        }

        CompileResult compiled = PluginCompiler.Compile(compilation, cancellationToken);
        return compiled.Assembly is { } image
            ? new PluginBuildResult(manifest, image, null)
            : new PluginBuildResult(manifest, null, Failed(PluginInstallStage.Compile, manifest, [.. compiled.Diagnostics.Select(d => d.ToString())]));
    }

    /// <summary>Loads the plugin and runs its tests. Kept separate so no plugin type is referenced once the context is unloaded.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<PluginInstallReport> LoadAndTestAsync(PluginManifest manifest, byte[] image, CancellationToken cancellationToken)
    {
        var context = new PluginLoadContext(manifest.Id.Value);
        try
        {
            Type[] exported;
            try
            {
                Assembly assembly = context.LoadPlugin(image);
                if (EntryTypeProblem(assembly, manifest.EntryType) is { } problem)
                {
                    return Failed(PluginInstallStage.Load, manifest, [problem]);
                }

                exported = assembly.GetExportedTypes();
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or ReflectionTypeLoadException)
            {
                return Failed(PluginInstallStage.Load, manifest, [$"The plugin could not be loaded: {PluginText.Printable(ex.Message, 300)}"]);
            }

            PluginTestReport report = await new PluginTestRunner().RunAsync(exported, cancellationToken).ConfigureAwait(false);
            return report.Succeeded
                ? new PluginInstallReport(PluginInstallStage.Ready, [], report, manifest)
                : new PluginInstallReport(PluginInstallStage.Tests, TestErrors(report), report, manifest);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Returns why <paramref name="entryType"/> cannot serve as the plugin entry point, or <see langword="null"/> when it can.</summary>
    internal static string? EntryTypeProblem(Assembly assembly, string entryType)
    {
        Type? type = assembly.GetType(entryType, throwOnError: false);
        return type switch
        {
            null => $"Entry type '{entryType}' was not found in the plugin.",
            { IsPublic: false } => $"Entry type '{entryType}' must be public.",
            { IsClass: false } or { IsAbstract: true } => $"Entry type '{entryType}' must be a non-abstract class.",
            _ when !typeof(IBlockerPlugin).IsAssignableFrom(type) => $"Entry type '{entryType}' must implement IBlockerPlugin.",
            _ when type.GetConstructor(Type.EmptyTypes) is not { IsPublic: true } => $"Entry type '{entryType}' must have a public parameterless constructor.",
            _ => null,
        };
    }

    private static List<string> TestErrors(PluginTestReport report) => report.Results.Count == 0
        ? ["No tests were found: the plugin must contain at least one public [Fact] or [Theory] test."]
        : [.. report.Results.Where(r => r.Outcome != PluginTestOutcome.Passed).Select(r => $"{r.Outcome}: {r.DisplayName}: {r.Message}")];

    private static PluginInstallReport Failed(PluginInstallStage stage, PluginManifest manifest, IReadOnlyList<string> errors) =>
        new(stage, errors, null, manifest);
}
