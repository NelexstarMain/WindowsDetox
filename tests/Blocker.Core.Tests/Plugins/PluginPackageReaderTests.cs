using System.IO.Compression;
using System.Text;
using Blocker.Core.Plugins;

namespace Blocker.Core.Tests.Plugins;

public sealed class PluginPackageReaderTests
{
    private const string Class = "public class A { }";

    private static async Task<PluginPackageReadResult> ReadAsync(Stream zip)
    {
        await using (zip)
        {
            return await PluginPackageReader.ReadAsync(zip, CancellationToken.None);
        }
    }

    private static async Task AssertFailsAsync(Stream zip, PluginInstallStage stage, params string[] errors)
    {
        PluginPackageReadResult result = await ReadAsync(zip);

        Assert.False(result.Succeeded);
        Assert.Null(result.Package);
        Assert.Equal(stage, result.FailedStage);
        Assert.Equal(errors, result.Errors);
    }

    [Fact]
    public async Task Reads_sources_and_other_files_in_ordinal_order()
    {
        PluginPackageReadResult result = await ReadAsync(PluginTestSupport.ZipWithManifest(
            ("b/Second.cs", "class Second { }"),
            ("First.cs", Class),
            ("README.md", "# Hello"),
            ("data/Extra.JSON", "{}")));

        Assert.True(result.Succeeded);
        Assert.Null(result.FailedStage);
        Assert.Empty(result.Errors);
        PluginPackage package = result.Package!;
        Assert.Equal("com.test.sample", package.Manifest.Id.Value);
        Assert.Equal(["First.cs", "b/Second.cs"], package.Sources.Keys);
        Assert.Equal(Class, package.Sources["First.cs"]);
        Assert.Equal(["README.md", "data/Extra.JSON", "plugin.json"], package.Files.Keys);
        Assert.Equal("# Hello", package.Files["README.md"]);
        Assert.Equal(PluginTestSupport.ValidManifestJson, package.Files["plugin.json"]);
    }

    [Fact]
    public async Task Normalizes_backslashes_and_ignores_directory_entries()
    {
        PluginPackageReadResult result = await ReadAsync(PluginTestSupport.ZipWithManifest(
            ("src/", string.Empty),
            ("../", string.Empty),
            ("src\\Deep.cs", Class)));

        Assert.True(result.Succeeded);
        Assert.Equal(["src/Deep.cs"], result.Package!.Sources.Keys);
    }

    [Fact]
    public async Task Accepts_upper_case_extensions_and_a_utf8_bom()
    {
        using MemoryStream zip = PluginTestSupport.Zip([
            ("plugin.json", Encoding.UTF8.GetBytes(PluginTestSupport.ValidManifestJson)),
            ("A.CS", [0xEF, 0xBB, 0xBF, .. "class A { }"u8]),
        ]);

        PluginPackageReadResult result = await ReadAsync(zip);

        Assert.Equal("class A { }", result.Package!.Sources["A.CS"]);
    }

    [Fact]
    public async Task Accepts_exactly_the_maximum_number_of_entries()
    {
        (string, string)[] files = [.. Enumerable.Range(0, PluginPackageReader.MaxEntries - 1).Select(i => ($"F{i}.cs", Class))];

        PluginPackageReadResult result = await ReadAsync(PluginTestSupport.ZipWithManifest(files));

        Assert.True(result.Succeeded);
        Assert.Equal(PluginPackageReader.MaxEntries - 1, result.Package!.Sources.Count);
    }

    [Fact]
    public async Task Rejects_more_than_50_entries()
    {
        (string, string)[] files = [.. Enumerable.Range(0, PluginPackageReader.MaxEntries).Select(i => ($"F{i}.cs", Class))];

        await AssertFailsAsync(PluginTestSupport.ZipWithManifest(files), PluginInstallStage.Package, "The ZIP contains 51 entries; at most 50 are allowed.");
    }

    [Theory]
    [InlineData("../x.cs", "Entry '../x.cs': '.' and '..' path segments are not allowed.")]
    [InlineData("a/../../x.cs", "Entry 'a/../../x.cs': '.' and '..' path segments are not allowed.")]
    [InlineData("a\\..\\x.cs", "Entry 'a\\..\\x.cs': '.' and '..' path segments are not allowed.")]
    [InlineData("./x.cs", "Entry './x.cs': '.' and '..' path segments are not allowed.")]
    [InlineData("/abs.cs", "Entry '/abs.cs': absolute paths are not allowed.")]
    [InlineData("\\abs.cs", "Entry '\\abs.cs': absolute paths are not allowed.")]
    [InlineData("C:\\x.cs", "Entry 'C:\\x.cs': the name contains ':' (drive letters and alternate data streams are not allowed).")]
    [InlineData("C:/x.cs", "Entry 'C:/x.cs': the name contains ':' (drive letters and alternate data streams are not allowed).")]
    [InlineData("a:b.cs", "Entry 'a:b.cs': the name contains ':' (drive letters and alternate data streams are not allowed).")]
    [InlineData("A.cs:stream.cs", "Entry 'A.cs:stream.cs': the name contains ':' (drive letters and alternate data streams are not allowed).")]
    [InlineData("a\u0001.cs", "Entry 'a?.cs': the name contains control characters.")]
    [InlineData("a//b.cs", "Entry 'a//b.cs': path segments must not be empty or end with '.' or a space.")]
    [InlineData("dir./b.cs", "Entry 'dir./b.cs': path segments must not be empty or end with '.' or a space.")]
    [InlineData("a/b/c/d.cs", "Entry 'a/b/c/d.cs': the path is nested deeper than 3 levels.")]
    public async Task Rejects_dangerous_entry_names(string name, string expected)
    {
        await AssertFailsAsync(PluginTestSupport.ZipWithManifest((name, Class)), PluginInstallStage.Package, expected);
    }

    [Fact]
    public async Task Rejects_names_longer_than_200_characters()
    {
        string name = new string('n', 198) + ".cs";

        await AssertFailsAsync(
            PluginTestSupport.ZipWithManifest((name, Class)),
            PluginInstallStage.Package,
            $"Entry '{new string('n', 197)}...': the name is longer than 200 characters.");
    }

    [Theory]
    [InlineData("tool.exe", "Entry 'tool.exe': file type '.exe' is not allowed (only .cs, .json and .md).")]
    [InlineData("lib/x.dll", "Entry 'lib/x.dll': file type '.dll' is not allowed (only .cs, .json and .md).")]
    [InlineData("LICENSE", "Entry 'LICENSE': file type '(none)' is not allowed (only .cs, .json and .md).")]
    [InlineData("x.cs.txt", "Entry 'x.cs.txt': file type '.txt' is not allowed (only .cs, .json and .md).")]
    [InlineData("x.csproj", "Entry 'x.csproj': file type '.csproj' is not allowed (only .cs, .json and .md).")]
    public async Task Rejects_disallowed_file_types(string name, string expected)
    {
        await AssertFailsAsync(PluginTestSupport.ZipWithManifest((name, "x")), PluginInstallStage.Package, expected);
    }

    [Theory]
    [InlineData("A.cs", "a.cs", "Entry 'a.cs' duplicates 'A.cs' (names are compared case-insensitively).")]
    [InlineData("dir/A.cs", "DIR\\a.CS", "Entry 'DIR\\a.CS' duplicates 'dir/A.cs' (names are compared case-insensitively).")]
    public async Task Rejects_duplicate_names_ignoring_case(string first, string second, string expected)
    {
        await AssertFailsAsync(PluginTestSupport.ZipWithManifest((first, Class), (second, Class)), PluginInstallStage.Package, expected);
    }

    [Fact]
    public async Task Rejects_a_package_without_plugin_json()
    {
        await AssertFailsAsync(PluginTestSupport.Zip(("A.cs", Class)), PluginInstallStage.Package, "plugin.json is missing at the ZIP root.");
    }

    [Fact]
    public async Task Rejects_a_plugin_json_that_is_not_at_the_root()
    {
        await AssertFailsAsync(
            PluginTestSupport.Zip(("sub/plugin.json", PluginTestSupport.ValidManifestJson)),
            PluginInstallStage.Package,
            "plugin.json is missing at the ZIP root.");
    }

    [Fact]
    public async Task Rejects_an_empty_archive()
    {
        await AssertFailsAsync(PluginTestSupport.Zip(), PluginInstallStage.Package, "plugin.json is missing at the ZIP root.");
    }

    [Fact]
    public async Task Rejects_a_highly_compressible_entry_as_a_zip_bomb()
    {
        byte[] zeros = new byte[3 * 1024 * 1024];
        using MemoryStream zip = PluginTestSupport.Zip(
            [("plugin.json", Encoding.UTF8.GetBytes(PluginTestSupport.ValidManifestJson)), ("Big.cs", zeros)],
            CompressionLevel.SmallestSize);

        await AssertFailsAsync(zip, PluginInstallStage.Package, "Entry 'Big.cs' has a compression ratio above 100:1 (possible ZIP bomb).");
    }

    [Fact]
    public async Task Rejects_content_that_unpacks_to_more_than_2_mb()
    {
        var random = new Random(1);
        byte[] Text()
        {
            byte[] bytes = new byte[800 * 1024];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)('a' + random.Next(26));
            }

            return bytes;
        }

        using MemoryStream zip = PluginTestSupport.Zip(
        [
            ("plugin.json", Encoding.UTF8.GetBytes(PluginTestSupport.ValidManifestJson)),
            ("a.md", Text()),
            ("b.md", Text()),
            ("c.md", Text()),
        ]);

        await AssertFailsAsync(zip, PluginInstallStage.Package, "The ZIP unpacks to more than 2 MB.");
    }

    [Fact]
    public async Task Reads_a_large_realistic_source_file_unchanged()
    {
        string text = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"// line {i} {Guid.NewGuid():N}\n"));

        PluginPackageReadResult result = await ReadAsync(PluginTestSupport.ZipWithManifest(("Big.cs", text)));

        Assert.True(result.Succeeded);
        Assert.Equal(text, result.Package!.Sources["Big.cs"]);
    }

    [Fact]
    public async Task Rejects_a_zip_file_larger_than_5_mb_without_parsing_it()
    {
        using var zip = new MemoryStream(new byte[(int)PluginPackageReader.MaxCompressedBytes + 1]);

        await AssertFailsAsync(zip, PluginInstallStage.Package, "The ZIP file is larger than 5 MB.");
    }

    [Fact]
    public async Task Rejects_data_that_is_not_a_zip()
    {
        PluginPackageReadResult result = await ReadAsync(new MemoryStream("this is not a zip file"u8.ToArray()));

        Assert.Equal(PluginInstallStage.Package, result.FailedStage);
        Assert.StartsWith("The file is not a valid ZIP archive: ", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_truncated_zip()
    {
        byte[] full = PluginTestSupport.ZipWithManifest(("A.cs", Class)).ToArray();

        PluginPackageReadResult result = await ReadAsync(new MemoryStream(full, 0, full.Length / 2));

        Assert.Equal(PluginInstallStage.Package, result.FailedStage);
        Assert.StartsWith("The file is not a valid ZIP archive: ", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Bad.cs")]
    [InlineData("Bad.md")]
    public async Task Rejects_invalid_utf8(string name)
    {
        using MemoryStream zip = PluginTestSupport.Zip(
            [("plugin.json", Encoding.UTF8.GetBytes(PluginTestSupport.ValidManifestJson)), (name, [0x61, 0xC3, 0x28])]);

        await AssertFailsAsync(zip, PluginInstallStage.Package, $"Entry '{name}' is not valid UTF-8 text.");
    }

    [Fact]
    public async Task Rejects_utf16_encoded_source()
    {
        using MemoryStream zip = PluginTestSupport.Zip(
            [("plugin.json", Encoding.UTF8.GetBytes(PluginTestSupport.ValidManifestJson)), ("A.cs", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Class)).ToArray())]);

        await AssertFailsAsync(zip, PluginInstallStage.Package, "Entry 'A.cs' is not valid UTF-8 text.");
    }

    [Fact]
    public async Task Reports_manifest_errors_as_the_manifest_stage()
    {
        PluginPackageReadResult result = await ReadAsync(PluginTestSupport.Zip(("plugin.json", "{\"id\":\"x\"}"), ("A.cs", Class)));

        Assert.Equal(PluginInstallStage.Manifest, result.FailedStage);
        Assert.Contains("'id' is not a valid plugin id (expected e.g. 'com.example.my-plugin'): 'x'.", result.Errors);
        Assert.Contains("'name' is required.", result.Errors);
    }

    [Fact]
    public async Task Collects_name_errors_of_all_entries()
    {
        await AssertFailsAsync(
            PluginTestSupport.ZipWithManifest(("../x.cs", Class), ("y.exe", "x")),
            PluginInstallStage.Package,
            "Entry '../x.cs': '.' and '..' path segments are not allowed.",
            "Entry 'y.exe': file type '.exe' is not allowed (only .cs, .json and .md).");
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using MemoryStream zip = PluginTestSupport.ZipWithManifest(("A.cs", Class));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginPackageReader.ReadAsync(zip, cts.Token));
    }

    [Fact]
    public async Task Reads_from_a_non_seekable_stream()
    {
        byte[] bytes = PluginTestSupport.ZipWithManifest(("A.cs", Class)).ToArray();
        await using var forward = new ForwardOnlyStream(bytes);

        PluginPackageReadResult result = await PluginPackageReader.ReadAsync(forward, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
