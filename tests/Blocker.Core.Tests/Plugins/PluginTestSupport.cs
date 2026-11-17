using System.IO.Compression;
using System.Text;
using Blocker.Core.Plugins;
using Blocker.Plugin.Templates;
using Blocker.Sdk;
using Microsoft.CodeAnalysis.CSharp;

namespace Blocker.Core.Tests.Plugins;

/// <summary>Builders for ZIPs, manifests and compilations used by the plugin pipeline tests.</summary>
internal static class PluginTestSupport
{
    public const string ValidManifestJson = """
        {
          "id": "com.test.sample",
          "name": "Sample",
          "version": "1.2.3",
          "author": "Tester",
          "description": "Does a thing.",
          "sdkVersion": "1.0",
          "entryType": "Sample.Plugin",
          "capabilities": ["events.session", "tick"]
        }
        """;

    /// <summary>Builds a ZIP in memory. Entry names are used verbatim (so malicious names can be tested).</summary>
    public static MemoryStream Zip(IEnumerable<(string Name, byte[] Data)> entries, CompressionLevel level = CompressionLevel.Optimal)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                using Stream entry = archive.CreateEntry(name, level).Open();
                entry.Write(data);
            }
        }

        stream.Position = 0;
        return stream;
    }

    public static MemoryStream Zip(params (string Name, string Text)[] entries) =>
        Zip(entries.Select(e => (e.Name, Encoding.UTF8.GetBytes(e.Text))));

    /// <summary>Returns a ZIP holding a valid <c>plugin.json</c> plus <paramref name="extra"/> entries.</summary>
    public static MemoryStream ZipWithManifest(params (string Name, string Text)[] extra) =>
        Zip([("plugin.json", ValidManifestJson), .. extra]);

    /// <summary>Reads the embedded starter template into a name-to-text map that tests can edit.</summary>
    public static async Task<Dictionary<string, string>> StarterFilesAsync()
    {
        using var stream = new MemoryStream();
        await PluginStarterTemplate.WriteZipAsync(stream, CancellationToken.None);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Dictionary<string, string> files = [];
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            files[entry.FullName] = await reader.ReadToEndAsync();
        }

        return files;
    }

    public static MemoryStream ZipOf(Dictionary<string, string> files) => Zip([.. files.Select(f => (f.Key, f.Value))]);

    public static PluginManifest Manifest(params PluginCapability[] capabilities) =>
        new(new PluginId("com.test.sample"), "Sample", new Version(1, 0, 0), "Tester", null, new Version(1, 0), "Sample.Plugin", capabilities);

    public static PluginPackage Package(string source, string path = "Test.cs") =>
        new(Manifest(), new Dictionary<string, string> { [path] = source }, new Dictionary<string, string>());

    public static CSharpCompilation CompilationOf(string source) => PluginCompiler.CreateCompilation(Package(source));

    public static IReadOnlyList<PluginDiagnostic> Analyze(string source, params PluginCapability[] capabilities) =>
        ApiPolicyAnalyzer.Analyze(CompilationOf(source), capabilities.ToHashSet(), TestContext.Current.CancellationToken);

    public static CompileResult Compile(PluginPackage package) =>
        PluginCompiler.Compile(package, TestContext.Current.CancellationToken);

    public static CompileResult Compile(CSharpCompilation compilation) =>
        PluginCompiler.Compile(compilation, TestContext.Current.CancellationToken);
}
