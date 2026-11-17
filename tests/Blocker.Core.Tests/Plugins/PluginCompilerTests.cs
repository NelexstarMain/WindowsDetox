using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Blocker.Core.Plugins;
using Blocker.Sdk;

namespace Blocker.Core.Tests.Plugins;

public sealed class PluginCompilerTests
{
    [Fact]
    public void Compiles_valid_code_into_a_pe_image_with_an_embedded_pdb()
    {
        CompileResult result = PluginTestSupport.Compile(PluginTestSupport.Package("public class A { public int Value => 42; }"));

        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        byte[] image = Assert.IsType<byte[]>(result.Assembly);
        Assert.Equal((byte)'M', image[0]);
        Assert.Equal((byte)'Z', image[1]);
        using (var pe = new PEReader(new MemoryStream(image)))
        {
            Assert.Contains(pe.ReadDebugDirectory(), entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        }

        Assert.Matches("^BlockerPlugin_com\\.test\\.sample_[0-9a-f]{32}$", result.Compilation.AssemblyName);
        var context = new AssemblyLoadContext("compiler-test", isCollectible: true);
        using var stream = new MemoryStream(image);
        Assembly assembly = context.LoadFromStream(stream);
        try
        {
            Assert.Equal(result.Compilation.AssemblyName, assembly.GetName().Name);
            Type type = assembly.GetType("A")!;
            Assert.Equal(42, type.GetProperty("Value")!.GetValue(Activator.CreateInstance(type)));
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void Every_compilation_gets_a_unique_assembly_name()
    {
        PluginPackage package = PluginTestSupport.Package("public class A { }");

        string first = PluginCompiler.CreateCompilation(package).AssemblyName!;
        string second = PluginCompiler.CreateCompilation(package).AssemblyName!;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Reports_a_syntax_error_with_file_and_line()
    {
        const string Source = """
            public class A
            {
                public int X = 1
            }
            """;

        CompileResult result = PluginTestSupport.Compile(PluginTestSupport.Package(Source, "src/A.cs"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Assembly);
        PluginDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(new PluginDiagnostic("src/A.cs", 3, "CS1002", "; expected"), diagnostic);
        Assert.Equal("src/A.cs:3: CS1002: ; expected", diagnostic.ToString());
    }

    [Fact]
    public void Reports_semantic_errors_of_every_file()
    {
        var package = new PluginPackage(
            PluginTestSupport.Manifest(),
            new Dictionary<string, string>
            {
                ["A.cs"] = "public class A { public int X = \"text\"; }",
                ["B.cs"] = "\npublic class B { public Missing Y; }",
            },
            new Dictionary<string, string>());

        CompileResult result = PluginTestSupport.Compile(package);

        Assert.Equal(
            [
                new PluginDiagnostic("A.cs", 1, "CS0029", "Cannot implicitly convert type 'string' to 'int'"),
                new PluginDiagnostic("B.cs", 2, "CS0246", "The type or namespace name 'Missing' could not be found (are you missing a using directive or an assembly reference?)"),
            ],
            result.Diagnostics);
    }

    [Fact]
    public void Unsafe_code_does_not_compile()
    {
        CompileResult result = PluginTestSupport.Compile(PluginTestSupport.Package("public class A { public unsafe int* P; }"));

        Assert.False(result.Succeeded);
        Assert.Equal("CS0227", Assert.Single(result.Diagnostics).RuleId);
    }

    [Fact]
    public void Plugins_see_the_sdk_the_test_kit_xunit_and_the_standard_usings()
    {
        const string Source = """
            using Blocker.Plugin.Testing;
            using Blocker.Sdk;
            using Blocker.UI.Sdk;
            using Microsoft.Extensions.Time.Testing;
            using Xunit;

            public sealed class Tests
            {
                [Fact]
                public async Task Works()
                {
                    var ctx = new FakePluginContext("com.test.sample", []);
                    List<UiNode> nodes = [Ui.Text("x")];
                    var time = new FakeTimeProvider();
                    Assert.Single(nodes.Where(n => n is not null));
                    await ctx.Storage.WriteAsync("k", "v", CancellationToken.None);
                    Assert.Equal(PluginCapability.Tick, PluginCapability.Tick);
                    Assert.NotNull(time);
                }
            }
            """;

        CompileResult result = PluginTestSupport.Compile(PluginTestSupport.Package(Source));

        Assert.Empty(result.Diagnostics);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Other_assemblies_cannot_be_referenced()
    {
        CompileResult result = PluginTestSupport.Compile(PluginTestSupport.Package("public class A { public Microsoft.CodeAnalysis.SyntaxTree? T; }"));

        Assert.False(result.Succeeded);
        Assert.Equal("CS0234", Assert.Single(result.Diagnostics).RuleId);
    }

    [Fact]
    public void Compile_of_an_analyzed_compilation_returns_that_compilation()
    {
        Microsoft.CodeAnalysis.CSharp.CSharpCompilation compilation = PluginTestSupport.CompilationOf("public class A { }");

        CompileResult result = PluginTestSupport.Compile(compilation);

        Assert.Same(compilation, result.Compilation);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => PluginCompiler.Compile(PluginTestSupport.Package("public class A { }"), cts.Token));
    }
}

