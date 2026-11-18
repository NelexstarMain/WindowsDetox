using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Blocker.Core.Plugins;
using Blocker.Plugin.Templates;
using Blocker.Plugin.Testing;
using Blocker.Plugin.Testing.Runner;
using Blocker.Sdk;
using Blocker.Sdk.Extensions;
using Blocker.UI.Sdk;
using FocusCounter;
using Xunit;

namespace Blocker.Plugin.Templates.Tests;

public sealed class PluginStarterTemplateTests
{
    private static readonly string[] ExpectedFiles =
    [
        "AGENT_INSTRUCTIONS.md",
        "PluginMain.cs",
        "PluginTests.cs",
        "README.md",
        "STYLE_GUIDE.md",
        "plugin.json",
        ".claude/skills/windowsdetox-plugin/SKILL.md",
        ".claude/skills/windowsdetox-plugin-icon/SKILL.md",
        ".claude/skills/windowsdetox-plugin-icon/preview_icon.py",
        ".claude/skills/windowsdetox-plugin-page/SKILL.md",
        ".claude/skills/windowsdetox-widget/SKILL.md",
    ];

    private static readonly string[] PluginFiles = [.. ExpectedFiles.Where(f => !f.StartsWith(".claude/", StringComparison.Ordinal))];

    private static readonly string[] AllowedExtensions = [".cs", ".json", ".md"];

    private static async Task<ZipArchive> OpenZipAsync()
    {
        var stream = new MemoryStream();
        await PluginStarterTemplate.WriteZipAsync(stream, TestContext.Current.CancellationToken);
        Assert.True(stream.CanWrite, "WriteZipAsync must leave the destination stream open.");
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static async Task<JsonElement> ReadManifestAsync()
    {
        using ZipArchive zip = await OpenZipAsync();
        await using Stream entry = await zip.GetEntry("plugin.json")!.OpenAsync(TestContext.Current.CancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(entry, cancellationToken: TestContext.Current.CancellationToken);
        return document.RootElement.Clone();
    }

    [Fact]
    public void FileNames_lists_every_expected_file_with_forward_slashes()
    {
        Assert.Equal(ExpectedFiles.Order(StringComparer.Ordinal), PluginStarterTemplate.FileNames);
        Assert.DoesNotContain(PluginStarterTemplate.FileNames, name => name.Contains('\\'));
    }

    [Fact]
    public async Task Zip_contains_exactly_the_expected_files()
    {
        using ZipArchive zip = await OpenZipAsync();

        Assert.Equal(ExpectedFiles.Order(StringComparer.Ordinal), zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.All(zip.Entries, entry =>
        {
            Assert.True(entry.Length > 0, $"{entry.FullName} is empty.");
            Assert.True(entry.FullName.StartsWith(".claude/", StringComparison.Ordinal) || AllowedExtensions.Contains(Path.GetExtension(entry.FullName)), entry.FullName);
        });
        Assert.True(zip.Entries.Sum(e => e.Length) < 2 * 1024 * 1024, "Unpacked size must stay below the 2 MB plugin limit.");
    }

    [Fact]
    public async Task Zip_installs_with_the_agent_skills_ignored()
    {
        var stream = new MemoryStream();
        await PluginStarterTemplate.WriteZipAsync(stream, TestContext.Current.CancellationToken);
        stream.Position = 0;

        PluginPackageReadResult result = await PluginPackageReader.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(PluginFiles.Order(StringComparer.Ordinal), result.Package!.Sources.Keys.Concat(result.Package.Files.Keys).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Skills_have_front_matter_and_no_repository_paths()
    {
        using ZipArchive zip = await OpenZipAsync();
        foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.FullName.EndsWith("SKILL.md", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(await entry.OpenAsync(TestContext.Current.CancellationToken));
            string text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.StartsWith("---\nname: ", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            foreach (string forbidden in new[] { "plugins/", "eng/", "src/", "BundledPluginTests", "screenshots.ps1", "build-installer" })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Zip_can_be_written_twice_with_identical_content()
    {
        using ZipArchive first = await OpenZipAsync();
        using ZipArchive second = await OpenZipAsync();

        foreach (ZipArchiveEntry entry in first.Entries)
        {
            using var reader1 = new StreamReader(await entry.OpenAsync(TestContext.Current.CancellationToken));
            using var reader2 = new StreamReader(await second.GetEntry(entry.FullName)!.OpenAsync(TestContext.Current.CancellationToken));
            Assert.Equal(await reader1.ReadToEndAsync(TestContext.Current.CancellationToken), await reader2.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Manifest_has_a_valid_id_version_and_required_fields()
    {
        JsonElement manifest = await ReadManifestAsync();

        string id = manifest.GetProperty("id").GetString()!;
        Assert.True(PluginId.IsValid(id));
        Assert.Equal("com.example.focus-counter", id);
        Assert.True(Version.TryParse(manifest.GetProperty("version").GetString(), out _));
        Assert.Equal("1.0", manifest.GetProperty("sdkVersion").GetString());
        foreach (string field in new[] { "name", "author", "description", "entryType" })
        {
            Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty(field).GetString()), $"'{field}' must not be empty.");
        }
    }

    [Fact]
    public async Task Manifest_capabilities_parse_and_cover_everything_the_plugin_registers()
    {
        JsonElement manifest = await ReadManifestAsync();
        string[] names = [.. manifest.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        HashSet<PluginCapability> declared = [.. names.Select(name =>
        {
            Assert.True(PluginCapabilities.TryParse(name, out PluginCapability capability), $"Unknown capability '{name}'.");
            return capability;
        })];

        var ctx = new FakePluginContext("com.example.focus-counter");
        await new PluginMain().InitializeAsync(ctx, TestContext.Current.CancellationToken);
        HashSet<PluginCapability> used = [.. ctx.RegisteredExtensions.SelectMany(ExtensionCapabilities.GetRequired)];
        used.Add(PluginCapabilities.RequiredForEvent(typeof(Blocker.Sdk.Events.SessionStarted)));
        used.Add(PluginCapabilities.RequiredForEvent(typeof(Blocker.Sdk.Events.ProcessBlocked)));

        Assert.True(declared.SetEquals(used), $"Declared [{string.Join(", ", declared)}] but used [{string.Join(", ", used)}].");
    }

    [Fact]
    public async Task EntryType_resolves_to_a_public_plugin_class_with_a_parameterless_constructor()
    {
        JsonElement manifest = await ReadManifestAsync();
        string entryType = manifest.GetProperty("entryType").GetString()!;

        Type? type = typeof(PluginMain).Assembly.GetType(entryType, throwOnError: false);

        Assert.NotNull(type);
        Assert.True(type.IsPublic && !type.IsAbstract);
        Assert.True(typeof(IBlockerPlugin).IsAssignableFrom(type));
        Assert.NotNull(type.GetConstructor(Type.EmptyTypes));
        Assert.Equal(typeof(PluginMain), type);
    }

    [Fact]
    public async Task Plugin_runner_passes_every_test_of_the_template()
    {
        Type[] testClasses = [.. typeof(PluginMain).Assembly.GetTypes().Where(t => t.IsPublic && t.Namespace == "FocusCounter.Tests")];
        int expected = testClasses.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)).Sum(CountCases);

        PluginTestReport report = await new PluginTestRunner().RunAsync(testClasses, TestContext.Current.CancellationToken);

        Assert.True(report.Succeeded, report.ToText());
        Assert.Equal(0, report.Failed);
        Assert.True(expected >= 6, "The template should ship at least six tests.");
        Assert.Equal(expected, report.Passed);
    }

    private static int CountCases(MethodInfo method) =>
        method.GetCustomAttribute<TheoryAttribute>() is not null
            ? method.GetCustomAttributes<InlineDataAttribute>().Count()
            : method.GetCustomAttribute<FactAttribute>() is not null ? 1 : 0;
}
