using System.Reflection;
using Blocker.Core.Plugins;
using Blocker.Sdk;

namespace Blocker.Core.Tests.Plugins;

public sealed class PluginLoadContextTests
{
    [Theory]
    [InlineData("Blocker.SDK", true)]
    [InlineData("Blocker.UI.SDK", true)]
    [InlineData("Blocker.Plugin.Testing", true)]
    [InlineData("Microsoft.Extensions.TimeProvider.Testing", true)]
    [InlineData("xunit.v3.core", true)]
    [InlineData("xunit.v3.assert", true)]
    [InlineData("xunit.v3.common", true)]
    [InlineData("System.Runtime", true)]
    [InlineData("System.Collections", true)]
    [InlineData("System.Private.CoreLib", true)]
    [InlineData("netstandard", true)]
    [InlineData("mscorlib", true)]
    [InlineData("Blocker.Core", false)]
    [InlineData("Microsoft.CodeAnalysis", false)]
    [InlineData("Microsoft.CodeAnalysis.CSharp", false)]
    [InlineData("Microsoft.Win32.Registry", false)]
    [InlineData("Newtonsoft.Json", false)]
    [InlineData("Systemx", false)]
    [InlineData("xunitx", false)]
    [InlineData("BlockerPlugin_other_1", false)]
    public void Only_sdk_test_kit_xunit_and_framework_assemblies_are_shared(string name, bool expected)
    {
        Assert.Equal(expected, PluginLoadContext.IsShared(new AssemblyName(name)));
    }

    [Fact]
    public void Shared_assemblies_are_the_very_same_instances_as_in_the_host()
    {
        var context = new PluginLoadContext("com.test.sample");
        try
        {
            Assembly sdk = context.LoadFromAssemblyName(new AssemblyName("Blocker.SDK"));

            Assert.Same(typeof(IBlockerPlugin).Assembly, sdk);
            Assert.Same(typeof(object).Assembly, context.LoadFromAssemblyName(new AssemblyName("System.Private.CoreLib")));
            Assert.Same(typeof(Xunit.FactAttribute).Assembly, context.LoadFromAssemblyName(new AssemblyName("xunit.v3.core")));
        }
        finally
        {
            context.Unload();
        }
    }

    [Theory]
    [InlineData("Microsoft.CodeAnalysis")]
    [InlineData("Blocker.Core")]
    [InlineData("Newtonsoft.Json")]
    public void Any_other_assembly_is_refused(string name)
    {
        var context = new PluginLoadContext("com.test.sample");
        try
        {
            var ex = Assert.Throws<FileNotFoundException>(() => context.LoadFromAssemblyName(new AssemblyName(name)));

            Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void The_context_is_collectible_and_named_after_the_plugin()
    {
        var context = new PluginLoadContext("com.test.sample");
        try
        {
            Assert.True(context.IsCollectible);
            Assert.Equal("plugin:com.test.sample", context.Name);
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void A_compiled_plugin_loads_with_sdk_types_identical_to_the_hosts()
    {
        CompileResult compiled = PluginTestSupport.Compile(PluginTestSupport.Package("""
            using Blocker.Sdk;
            public sealed class P : IBlockerPlugin
            {
                public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
                public ValueTask ShutdownAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            }
            """));
        var context = new PluginLoadContext("com.test.sample");
        try
        {
            Type type = context.LoadPlugin(compiled.Assembly!).GetType("P")!;

            Assert.True(typeof(IBlockerPlugin).IsAssignableFrom(type));
            Assert.Same(context, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly));
        }
        finally
        {
            context.Unload();
        }
    }
}
