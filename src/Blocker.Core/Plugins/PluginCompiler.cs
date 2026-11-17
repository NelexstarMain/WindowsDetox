using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Basic.Reference.Assemblies;
using Blocker.Plugin.Testing;
using Blocker.Sdk;
using Blocker.UI.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Time.Testing;

namespace Blocker.Core.Plugins;

/// <summary>Outcome of compiling a plugin.</summary>
/// <param name="Compilation">The compilation that was emitted (also available on failure, for analysis).</param>
/// <param name="Diagnostics">Compiler errors with file and line; empty on success.</param>
/// <param name="Assembly">The PE image with an embedded portable PDB; <see langword="null"/> when compilation failed.</param>
public sealed record CompileResult(CSharpCompilation Compilation, IReadOnlyList<PluginDiagnostic> Diagnostics, byte[]? Assembly)
{
    /// <summary>Gets whether an assembly was produced.</summary>
    public bool Succeeded => Assembly is not null;
}

/// <summary>
/// Compiles a plugin package with Roslyn against the .NET 10 reference assemblies, the Blocker SDKs, the plugin test kit
/// and xUnit. Only these references exist, so plugins cannot use NuGet packages or other DLLs.
/// </summary>
public static class PluginCompiler
{
    // The Microsoft.NET.Sdk implicit usings minus System.IO and System.Net.Http (plugins have no business with either).
    private const string GlobalUsings =
        "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n";

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Regular);

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    [
        .. Net100.References.All,
        .. new[]
        {
            typeof(IBlockerPlugin),
            typeof(UiNode),
            typeof(FakePluginContext),
            typeof(Xunit.Assert),
            typeof(Xunit.FactAttribute),
            typeof(Xunit.Sdk.IMessageSink),
            typeof(FakeTimeProvider),
        }.Select(type => type.Assembly.Location).Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)),
    ]);

    /// <summary>Creates the compilation for <paramref name="package"/> without emitting it, so it can be analyzed first.</summary>
    /// <param name="package">Validated package.</param>
    /// <returns>A release-mode DLL compilation named <c>BlockerPlugin_{id}_{guid}</c>.</returns>
    public static CSharpCompilation CreateCompilation(PluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        List<SyntaxTree> trees = [CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), ParseOptions, path: "<global-usings>")];
        trees.AddRange(package.Sources.Select(source =>
            CSharpSyntaxTree.ParseText(SourceText.From(source.Value, Encoding.UTF8), ParseOptions, path: source.Key)));

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            nullableContextOptions: NullableContextOptions.Enable,
            allowUnsafe: false,
            deterministic: true,
            concurrentBuild: false);
        string assemblyName = $"BlockerPlugin_{package.Manifest.Id.Value}_{Guid.NewGuid():N}";
        return CSharpCompilation.Create(assemblyName, trees, References.Value, options);
    }

    /// <summary>Compiles <paramref name="package"/>.</summary>
    /// <param name="package">Validated package.</param>
    /// <param name="cancellationToken">Cancels the compilation.</param>
    /// <returns>The result; on failure the diagnostics list every error as file, line and message.</returns>
    public static CompileResult Compile(PluginPackage package, CancellationToken cancellationToken = default) =>
        Compile(CreateCompilation(package), cancellationToken);

    /// <summary>Emits an existing plugin compilation.</summary>
    /// <param name="compilation">Compilation from <see cref="CreateCompilation"/>.</param>
    /// <param name="cancellationToken">Cancels the compilation.</param>
    /// <returns>The result.</returns>
    public static CompileResult Compile(CSharpCompilation compilation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        using var image = new MemoryStream();
        EmitResult emitted = compilation.Emit(
            image,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded),
            cancellationToken: cancellationToken);
        List<PluginDiagnostic> errors =
        [
            .. emitted.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(ToPluginDiagnostic)
                .OrderBy(d => d.File, StringComparer.Ordinal)
                .ThenBy(d => d.Line)
                .ThenBy(d => d.RuleId, StringComparer.Ordinal),
        ];
        return emitted.Success && errors.Count == 0
            ? new CompileResult(compilation, [], image.ToArray())
            : new CompileResult(compilation, errors, null);
    }

    private static PluginDiagnostic ToPluginDiagnostic(Diagnostic diagnostic)
    {
        string message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        return diagnostic.Location.IsInSource
            ? new PluginDiagnostic(
                diagnostic.Location.SourceTree?.FilePath ?? string.Empty,
                diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1,
                diagnostic.Id,
                message)
            : new PluginDiagnostic(string.Empty, 0, diagnostic.Id, message);
    }
}
