using Blocker.Core.Plugins;
using Blocker.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Blocker.Core.Tests.Plugins;

public sealed class ApiPolicyAnalyzerTests
{
    /// <summary>Line of a statement placed by <see cref="InMethod"/>.</summary>
    private const int StatementLine = 5;

    /// <summary>Line of a member placed by <see cref="InClass"/>.</summary>
    private const int MemberLine = 3;

    private static string InMethod(string statement) => $$"""
        public static class C
        {
            public static void M()
            {
                {{statement}}
            }
        }
        """;

    private static string InClass(string member) => $$"""
        public static class C
        {
            {{member}}
        }
        """;

    private static PluginDiagnostic Single(string source, params PluginCapability[] capabilities) =>
        Assert.Single(PluginTestSupport.Analyze(source, capabilities));

    [Theory]
    [InlineData("unsafe { }", "BLK001")]
    [InlineData("int x = 0; fixed (int* p = &x) { }", "BLK002")]
    [InlineData("delegate*<int, void> f = null;", "BLK003")]
    [InlineData("int i = 0; var r = __makeref(i);", "BLK005")]
    [InlineData("dynamic d = 1;", "BLK006")]
    public void Statement_level_syntax_rules_report_file_and_line(string statement, string ruleId)
    {
        PluginDiagnostic diagnostic = Single(InMethod(statement));

        Assert.Equal("Test.cs", diagnostic.File);
        Assert.Equal(StatementLine, diagnostic.Line);
        Assert.Equal(ruleId, diagnostic.RuleId);
    }

    [Theory]
    [InlineData("static unsafe void F() { }", "BLK001")]
    [InlineData("static extern void F();", "BLK004")]
    [InlineData("static void F(__arglist) { }", "BLK005")]
    [InlineData("static async void F() { await System.Threading.Tasks.Task.Yield(); }", "BLK007")]
    [InlineData("[System.Runtime.CompilerServices.ModuleInitializer] public static void Init() { }", "BLK014")]
    public void Member_level_syntax_rules_report_file_and_line(string member, string ruleId)
    {
        PluginDiagnostic diagnostic = Single(InClass(member));

        Assert.Equal("Test.cs", diagnostic.File);
        Assert.Equal(MemberLine, diagnostic.Line);
        Assert.Equal(ruleId, diagnostic.RuleId);
    }

    [Fact]
    public void Async_void_lambdas_and_local_functions_are_rejected()
    {
        const string Source = """
            public static class C
            {
                public static void M()
                {
                    System.Action a = async () => await System.Threading.Tasks.Task.Yield();
                    async void Local() => await System.Threading.Tasks.Task.Yield();
                    Local();
                }
            }
            """;

        IReadOnlyList<PluginDiagnostic> diagnostics = PluginTestSupport.Analyze(Source);

        Assert.Equal([5, 6], diagnostics.Select(d => d.Line));
        Assert.All(diagnostics, d => Assert.Equal("BLK007", d.RuleId));
    }

    [Fact]
    public void Dllimport_is_reported_as_extern_and_interop()
    {
        IReadOnlyList<PluginDiagnostic> diagnostics = PluginTestSupport.Analyze(
            InClass("[System.Runtime.InteropServices.DllImport(\"kernel32\")] static extern int GetTickCount();"));

        Assert.Equal(["BLK004", "BLK013"], diagnostics.Select(d => d.RuleId));
        Assert.All(diagnostics, d => Assert.Equal(MemberLine, d.Line));
    }

    [Theory]
    // BLK010 processes and debugger
    [InlineData("System.Diagnostics.Process.Start(\"cmd\");", "BLK010", "System.Diagnostics.Process")]
    [InlineData("var i = new System.Diagnostics.ProcessStartInfo(\"cmd\");", "BLK010", "System.Diagnostics.ProcessStartInfo")]
    [InlineData("System.Diagnostics.Debugger.Break();", "BLK010", "System.Diagnostics.Debugger")]
    // BLK011 dynamic code and loading
    [InlineData("System.Reflection.Assembly.Load(\"x\");", "BLK011", "System.Reflection.Assembly.Load")]
    [InlineData("System.Reflection.Assembly.LoadFrom(\"x.dll\");", "BLK011", "System.Reflection.Assembly.LoadFrom")]
    [InlineData("System.Reflection.Assembly.LoadFile(\"x.dll\");", "BLK011", "System.Reflection.Assembly.LoadFile")]
    [InlineData("var o = System.Activator.CreateInstance(typeof(object));", "BLK011", "System.Activator")]
    [InlineData("var t = System.Type.GetType(\"System.IO.File\");", "BLK011", "System.Type.GetType")]
    [InlineData("var c = System.Runtime.Loader.AssemblyLoadContext.Default;", "BLK011", "System.Runtime.Loader.AssemblyLoadContext")]
    [InlineData("System.Reflection.Emit.AssemblyBuilder? b = null;", "BLK011", "System.Reflection.Emit.AssemblyBuilder")]
    [InlineData("var f = System.Linq.Expressions.Expression.Lambda<System.Action>(System.Linq.Expressions.Expression.Empty()).Compile();", "BLK011", "System.Linq.Expressions.Expression.Compile")]
    // BLK012 reflection invoke
    [InlineData("typeof(object).GetMethod(\"ToString\")!.Invoke(null, null);", "BLK012", "System.Reflection.MethodBase.Invoke")]
    [InlineData("typeof(string).GetField(\"Empty\")!.GetValue(null);", "BLK012", "System.Reflection.FieldInfo.GetValue")]
    [InlineData("typeof(string).GetProperty(\"Length\")!.SetValue(\"a\", 1);", "BLK012", "System.Reflection.PropertyInfo.SetValue")]
    [InlineData("typeof(object).GetMethod(\"ToString\")!.CreateDelegate(typeof(System.Func<string>));", "BLK012", "System.Reflection.MethodInfo.CreateDelegate")]
    [InlineData("System.Func<int> f = () => 1; f.DynamicInvoke();", "BLK012", "System.Delegate.DynamicInvoke")]
    // BLK013 native interop and unsafe memory
    [InlineData("System.Runtime.InteropServices.Marshal.AllocHGlobal(1);", "BLK013", "System.Runtime.InteropServices.Marshal")]
    [InlineData("var s = System.Runtime.CompilerServices.Unsafe.SizeOf<int>();", "BLK013", "System.Runtime.CompilerServices.Unsafe")]
    [InlineData("var s = System.Runtime.InteropServices.MemoryMarshal.AsBytes(new System.Span<int>());", "BLK013", "System.Runtime.InteropServices.MemoryMarshal")]
    // BLK020 file system
    [InlineData("var s = System.IO.File.ReadAllText(\"a.txt\");", "BLK020", "System.IO.File")]
    [InlineData("System.IO.Directory.CreateDirectory(\"d\");", "BLK020", "System.IO.Directory")]
    [InlineData("var s = new System.IO.FileStream(\"a\", System.IO.FileMode.Open);", "BLK020", "System.IO.FileStream")]
    [InlineData("var i = new System.IO.FileInfo(\"a\");", "BLK020", "System.IO.FileInfo")]
    [InlineData("var i = new System.IO.DirectoryInfo(\"a\");", "BLK020", "System.IO.DirectoryInfo")]
    [InlineData("var d = System.IO.DriveInfo.GetDrives();", "BLK020", "System.IO.DriveInfo")]
    [InlineData("var w = new System.IO.FileSystemWatcher();", "BLK020", "System.IO.FileSystemWatcher")]
    [InlineData("var p = new System.IO.Pipes.AnonymousPipeServerStream();", "BLK020", "System.IO.Pipes.AnonymousPipeServerStream")]
    [InlineData("var m = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(\"m\", 1);", "BLK020", "System.IO.MemoryMappedFiles.MemoryMappedFile")]
    [InlineData("var r = new System.IO.StreamReader(\"a.txt\");", "BLK020", "System.IO.StreamReader")]
    [InlineData("var w = new System.IO.StreamWriter(\"a.txt\");", "BLK020", "System.IO.StreamWriter")]
    [InlineData("var t = System.IO.Path.GetTempPath();", "BLK020", "System.IO.Path.GetTempPath")]
    [InlineData("var d = System.Xml.Linq.XDocument.Load(\"a.xml\");", "BLK020", "System.Xml.Linq.XDocument.Load")]
    [InlineData("var r = System.Xml.XmlReader.Create(\"a.xml\");", "BLK020", "System.Xml.XmlReader.Create")]
    // BLK021 registry and Win32
    [InlineData("Microsoft.Win32.SafeHandles.SafeFileHandle? h = null;", "BLK021", "Microsoft.Win32.SafeHandles.SafeFileHandle")]
    [InlineData("_ = Microsoft.Win32.Registry.CurrentUser;", "BLK021", "Microsoft.Win32.Registry")]
    // BLK022 environment
    [InlineData("var m = System.Environment.MachineName;", "BLK022", "System.Environment.MachineName")]
    [InlineData("var v = System.Environment.GetEnvironmentVariable(\"PATH\");", "BLK022", "System.Environment.GetEnvironmentVariable")]
    [InlineData("var d = System.AppDomain.CurrentDomain;", "BLK022", "System.AppDomain")]
    [InlineData("System.GC.Collect();", "BLK022", "System.GC")]
    // BLK023 threads
    [InlineData("var t = new System.Threading.Thread(() => { });", "BLK023", "System.Threading.Thread")]
    [InlineData("System.Threading.Thread.Sleep(1);", "BLK023", "System.Threading.Thread.Sleep")]
    [InlineData("System.Threading.ThreadPool.QueueUserWorkItem(_ => { });", "BLK023", "System.Threading.ThreadPool")]
    // BLK024 blocking
    [InlineData("System.Threading.Tasks.Task.CompletedTask.Wait();", "BLK024", "System.Threading.Tasks.Task.Wait")]
    [InlineData("var r = System.Threading.Tasks.Task.FromResult(1).Result;", "BLK024", "System.Threading.Tasks.Task.Result")]
    [InlineData("System.Threading.Tasks.Task.CompletedTask.GetAwaiter().GetResult();", "BLK024", "System.Runtime.CompilerServices.TaskAwaiter.GetResult")]
    [InlineData("var r = System.Threading.Tasks.Task.FromResult(1).GetAwaiter().GetResult();", "BLK024", "System.Runtime.CompilerServices.TaskAwaiter.GetResult")]
    [InlineData("System.Threading.Tasks.Task.WaitAll();", "BLK024", "System.Threading.Tasks.Task.WaitAll")]
    [InlineData("var r = new System.Threading.Tasks.ValueTask<int>(1).Result;", "BLK024", "System.Threading.Tasks.ValueTask.Result")]
    [InlineData("new System.Threading.SemaphoreSlim(1).Wait();", "BLK024", "System.Threading.SemaphoreSlim.Wait")]
    // BLK025 real clocks
    [InlineData("var n = System.DateTime.Now;", "BLK025", "System.DateTime.Now")]
    [InlineData("var n = System.DateTime.UtcNow;", "BLK025", "System.DateTime.UtcNow")]
    [InlineData("var n = System.DateTime.Today;", "BLK025", "System.DateTime.Today")]
    [InlineData("var n = System.DateTimeOffset.Now;", "BLK025", "System.DateTimeOffset.Now")]
    [InlineData("var n = System.DateTimeOffset.UtcNow;", "BLK025", "System.DateTimeOffset.UtcNow")]
    [InlineData("var n = System.Diagnostics.Stopwatch.StartNew();", "BLK025", "System.Diagnostics.Stopwatch")]
    [InlineData("var n = System.Environment.TickCount;", "BLK025", "System.Environment.TickCount")]
    [InlineData("var n = System.Environment.TickCount64;", "BLK025", "System.Environment.TickCount64")]
    [InlineData("var n = System.TimeProvider.System.GetUtcNow();", "BLK025", "System.TimeProvider.System")]
    // BLK030 sockets
    [InlineData("var c = new System.Net.Sockets.TcpClient();", "BLK030", "System.Net.Sockets.TcpClient")]
    [InlineData("var n = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();", "BLK030", "System.Net.NetworkInformation.NetworkInterface")]
    // BLK031 high-level network
    [InlineData("var c = new System.Net.Http.HttpClient();", "BLK031", "System.Net.Http.HttpClient")]
    [InlineData("var c = new System.Net.WebClient();", "BLK031", "System.Net.WebClient")]
    [InlineData("var h = System.Net.Dns.GetHostName();", "BLK031", "System.Net.Dns")]
    // BLK040 security
    [InlineData("var a = System.Security.Cryptography.Aes.Create();", "BLK040", "System.Security.Cryptography.Aes")]
    [InlineData("var a = System.Security.Cryptography.MD5.HashData(new byte[1]);", "BLK040", "System.Security.Cryptography.MD5")]
    [InlineData("var p = System.Security.Principal.WindowsIdentity.GetCurrent();", "BLK040", "System.Security.Principal.WindowsIdentity")]
    // BLK041 console
    [InlineData("System.Console.WriteLine(\"x\");", "BLK041", "System.Console")]
    public void Denied_api_is_reported_with_file_line_rule_and_symbol(string statement, string ruleId, string symbol)
    {
        PluginDiagnostic diagnostic = Single(InMethod(statement));

        Assert.Equal("Test.cs", diagnostic.File);
        Assert.Equal(StatementLine, diagnostic.Line);
        Assert.Equal(ruleId, diagnostic.RuleId);
        Assert.StartsWith($"'{symbol}' is not allowed: ", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Message_explains_the_rule_and_formats_as_file_line_id_message()
    {
        PluginDiagnostic diagnostic = Single(InMethod("var n = System.DateTime.UtcNow;"));

        Assert.Equal(
            "'System.DateTime.UtcNow' is not allowed: Use context.Time (TimeProvider); the real clock makes plugins and tests non-deterministic.",
            diagnostic.Message);
        Assert.Equal(
            "Test.cs:5: BLK025: 'System.DateTime.UtcNow' is not allowed: Use context.Time (TimeProvider); the real clock makes plugins and tests non-deterministic.",
            diagnostic.ToString());
    }

    [Fact]
    public void Using_directives_and_simple_names_are_resolved_semantically()
    {
        const string Source = """
            using System.IO;
            using Clock = System.DateTime;
            using static System.Environment;
            public static class C
            {
                public static string M() => File.ReadAllText("a") + Clock.Now + MachineName;
            }
            """;

        IReadOnlyList<PluginDiagnostic> diagnostics = PluginTestSupport.Analyze(Source);

        Assert.Equal([("BLK020", 6), ("BLK022", 6), ("BLK025", 6)], diagnostics.Select(d => (d.RuleId, d.Line)).Order());
    }

    [Fact]
    public void Reports_every_violation_ordered_by_line_and_never_twice_per_line_and_rule()
    {
        const string Source = """
            public static class C
            {
                public static void M()
                {
                    var a = System.IO.File.ReadAllText("a") + System.IO.File.ReadAllText("b");
                    System.Console.WriteLine(System.DateTime.Now);
                    var p = System.Diagnostics.Process.GetCurrentProcess();
                }
            }
            """;

        IReadOnlyList<PluginDiagnostic> diagnostics = PluginTestSupport.Analyze(Source);

        Assert.Equal([("BLK020", 5), ("BLK025", 6), ("BLK041", 6), ("BLK010", 7)], diagnostics.Select(d => (d.RuleId, d.Line)));
    }

    [Fact]
    public void Diagnostics_name_the_file_they_are_in()
    {
        var package = new PluginPackage(
            PluginTestSupport.Manifest(),
            new Dictionary<string, string>
            {
                ["A.cs"] = "public class A { }",
                ["sub/B.cs"] = InClass("static string F() => System.IO.File.ReadAllText(\"x\");"),
            },
            new Dictionary<string, string>());

        IReadOnlyList<PluginDiagnostic> diagnostics = ApiPolicyAnalyzer.Analyze(PluginCompiler.CreateCompilation(package), new HashSet<PluginCapability>(), TestContext.Current.CancellationToken);

        PluginDiagnostic diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("sub/B.cs", MemberLine, "BLK020"), (diagnostic.File, diagnostic.Line, diagnostic.RuleId));
    }

    [Fact]
    public void Network_capability_unlocks_http_but_not_sockets()
    {
        const string Http = "var c = new System.Net.Http.HttpClient(); var s = c.GetStringAsync(\"https://example.org\");";
        const string Socket = "var c = new System.Net.Sockets.TcpClient();";

        Assert.Equal("BLK031", Single(InMethod(Http)).RuleId);
        Assert.Empty(PluginTestSupport.Analyze(InMethod(Http), PluginCapability.Network));
        Assert.Equal("BLK030", Single(InMethod(Socket), PluginCapability.Network).RuleId);
        Assert.Equal("BLK030", Single(InMethod("var n = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();"), PluginCapability.Network).RuleId);
    }

    [Fact]
    public void Network_capability_unlocks_the_other_high_level_network_apis()
    {
        string[] statements =
        [
            "var c = new System.Net.WebClient();",
            "var h = System.Net.Dns.GetHostName();",
            "System.Net.WebSockets.ClientWebSocket? w = null;",
        ];

        foreach (string statement in statements)
        {
            Assert.Equal("BLK031", Single(InMethod(statement)).RuleId);
            Assert.Empty(PluginTestSupport.Analyze(InMethod(statement), PluginCapability.Network));
        }
    }

    [Fact]
    public void Other_capabilities_do_not_unlock_anything()
    {
        PluginCapability[] others = [.. Enum.GetValues<PluginCapability>().Where(c => c != PluginCapability.Network)];

        Assert.Equal("BLK031", Single(InMethod("var c = new System.Net.Http.HttpClient();"), others).RuleId);
        Assert.Equal("BLK020", Single(InMethod("System.IO.File.Delete(\"a\");"), others).RuleId);
    }

    [Fact]
    public void Allowed_apis_produce_no_diagnostics()
    {
        const string Source = """
            using System.Collections.Concurrent;
            using System.Globalization;
            using System.IO;
            using System.Security.Cryptography;
            using System.Text;
            using System.Text.Json;
            using System.Text.RegularExpressions;

            public sealed class Allowed
            {
                private int _count;
                private long _ticks;
                private readonly List<string> _items = [];
                private readonly ConcurrentDictionary<string, int> _map = new();

                public async ValueTask<int> CountAsync(TimeProvider time, CancellationToken ct)
                {
                    Interlocked.Increment(ref _count);
                    Volatile.Write(ref _ticks, time.GetTimestamp());
                    _items.Add(Path.Combine("a", "b") + Path.GetExtension("x.cs") + Environment.NewLine);
                    await Task.Delay(TimeSpan.FromMilliseconds(1), time, ct);
                    await Task.Yield();
                    Task<int> task = Task.FromResult(_items.Count);
                    int awaited = await task;
                    var query = _items.Where(i => i.Length > 0).Select(i => i.ToUpperInvariant()).OrderBy(i => i).ToList();
                    using var reader = new StringReader("line");
                    using var writer = new StringWriter(CultureInfo.InvariantCulture);
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes("x"));
                    using var streamReader = new StreamReader(stream);
                    string json = JsonSerializer.Serialize(new { Value = awaited, query.Count });
                    byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
                    byte[] mac = HMACSHA256.HashData(hash, hash);
                    byte[] random = RandomNumberGenerator.GetBytes(8);
                    bool matches = Regex.IsMatch("abc", "a.c", RegexOptions.None, TimeSpan.FromMilliseconds(10));
                    Type type = typeof(System.IO.File);
                    string name = nameof(System.IO.File.ReadAllText);
                    DateTimeOffset now = time.GetUtcNow();
                    return awaited + hash.Length + mac.Length + random.Length + (matches ? 1 : 0) + type.Name.Length + name.Length + now.Year;
                }

                public static async Task<string> FetchAsync(Func<CancellationToken, Task<string>> fetch, CancellationToken ct) => await fetch(ct).ConfigureAwait(false);
            }
            """;

        Assert.Empty(PluginTestSupport.Analyze(Source));
    }

    [Fact]
    public void Template_style_state_class_is_allowed()
    {
        const string Source = """
            using System.Globalization;
            public sealed class State
            {
                private int _count;
                public int Count => Volatile.Read(ref _count);
                public int Increment() => Interlocked.Increment(ref _count);
                public string Format() => string.Create(CultureInfo.InvariantCulture, $"{Count}");
                public event Action? Changed;
                public void Raise() => Changed?.Invoke();
            }
            """;

        Assert.Empty(PluginTestSupport.Analyze(Source));
    }

    [Fact]
    public void Analyzer_works_on_code_with_compile_errors()
    {
        PluginDiagnostic diagnostic = Single(InMethod("var n = System.DateTime.Now; int x = \"text\";"));

        Assert.Equal("BLK025", diagnostic.RuleId);
    }

    [Theory]
    [InlineData("Blocker.Sdk.Extensions.IProcessInterceptor", PluginCapability.ProcessInterception)]
    [InlineData("Blocker.Sdk.Extensions.IWindowInterceptor", PluginCapability.WindowInterception)]
    [InlineData("Blocker.Sdk.Extensions.ITickListener", PluginCapability.Tick)]
    [InlineData("Blocker.Sdk.Extensions.ILockPolicyContributor", PluginCapability.LockPolicy)]
    [InlineData("Blocker.UI.Sdk.IUiPagePlugin", PluginCapability.UiPage)]
    [InlineData("Blocker.UI.Sdk.IDashboardWidgetPlugin", PluginCapability.UiDashboardWidget)]
    [InlineData("Blocker.UI.Sdk.ISettingsSectionPlugin", PluginCapability.UiSettingsSection)]
    [InlineData("Blocker.UI.Sdk.IBlockScreenPlugin", PluginCapability.UiBlockScreen)]
    public void Implementing_an_extension_interface_without_its_capability_is_reported_at_the_class(string contract, PluginCapability capability)
    {
        string source = $$"""
            // header
            public sealed class Ok { }

            public abstract class Mine : {{contract}}
            {
            }
            """;

        PluginDiagnostic diagnostic = Single(source);

        Assert.Equal("BLK008", diagnostic.RuleId);
        Assert.Equal(4, diagnostic.Line);
        Assert.Contains($"'{PluginCapabilities.ToManifestName(capability)}'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(PluginTestSupport.Analyze(source, capability));
    }

    [Fact]
    public void Several_missing_extension_capabilities_are_reported_once_per_class_and_the_granted_one_is_skipped()
    {
        const string Source = """
            public abstract class Mine : Blocker.Sdk.Extensions.ITickListener, Blocker.Sdk.Extensions.ILockPolicyContributor, Blocker.UI.Sdk.IBlockScreenPlugin
            {
            }
            """;

        PluginDiagnostic diagnostic = Single(Source, PluginCapability.Tick);

        Assert.Equal("BLK008", diagnostic.RuleId);
        Assert.Equal(1, diagnostic.Line);
        Assert.Contains("'lockpolicy'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("'ui.blockscreen'", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("'tick'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inherited_extension_interfaces_are_checked_on_the_derived_class()
    {
        const string Source = """
            public abstract class Base : Blocker.Sdk.Extensions.ITickListener { }

            public abstract class Derived : Base { }
            """;

        IReadOnlyList<PluginDiagnostic> diagnostics = PluginTestSupport.Analyze(Source, PluginCapability.Tick);
        Assert.Empty(diagnostics);

        diagnostics = PluginTestSupport.Analyze(Source);
        Assert.Equal([1, 3], diagnostics.Select(d => d.Line));
        Assert.All(diagnostics, d => Assert.Equal("BLK008", d.RuleId));
    }

    [Fact]
    public void Plain_classes_and_the_ui_contribution_base_interface_need_no_capability()
    {
        const string Source = """
            public abstract class Base : Blocker.UI.Sdk.IUiContribution { }

            public sealed class Plain : Blocker.Sdk.IBlockerPlugin
            {
                public System.Threading.Tasks.ValueTask InitializeAsync(Blocker.Sdk.IPluginContext context, System.Threading.CancellationToken cancellationToken) => default;

                public System.Threading.Tasks.ValueTask ShutdownAsync(System.Threading.CancellationToken cancellationToken) => default;
            }
            """;

        Assert.Empty(PluginTestSupport.Analyze(Source));
    }

    [Theory]
    [InlineData("SessionStarted", PluginCapability.SessionEvents)]
    [InlineData("SessionEnded", PluginCapability.SessionEvents)]
    [InlineData("ProcessBlocked", PluginCapability.ProcessEvents)]
    [InlineData("ActiveWindowChanged", PluginCapability.WindowEvents)]
    [InlineData("UserIdleChanged", PluginCapability.IdleEvents)]
    [InlineData("CustomPluginEvent", PluginCapability.CustomEvents)]
    public void Subscribing_to_a_core_event_without_its_capability_is_reported(string eventType, PluginCapability capability)
    {
        string source = $$"""
            using Blocker.Sdk.Events;
            public static class C
            {
                public static void M(IEventBus bus)
                {
                    bus.Subscribe<{{eventType}}>((e, ct) => System.Threading.Tasks.ValueTask.CompletedTask);
                }
            }
            """;

        PluginDiagnostic diagnostic = Single(source);

        Assert.Equal("BLK008", diagnostic.RuleId);
        Assert.Equal(6, diagnostic.Line);
        Assert.Contains($"'{PluginCapabilities.ToManifestName(capability)}'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(PluginTestSupport.Analyze(source, capability));
        Assert.Single(PluginTestSupport.Analyze(source, PluginCapability.Tick));
    }

    [Fact]
    public void Subscribe_with_an_inferred_event_type_is_checked_too()
    {
        const string Source = """
            using Blocker.Sdk.Events;
            public static class C
            {
                public static void M(IEventBus bus)
                {
                    bus.Subscribe((ProcessBlocked e, System.Threading.CancellationToken ct) => System.Threading.Tasks.ValueTask.CompletedTask);
                }
            }
            """;

        PluginDiagnostic diagnostic = Single(Source);

        Assert.Equal("BLK008", diagnostic.RuleId);
        Assert.Equal(6, diagnostic.Line);
        Assert.Empty(PluginTestSupport.Analyze(Source, PluginCapability.ProcessEvents));
    }

    [Fact]
    public void Subscribe_with_a_type_parameter_or_a_plugin_type_is_not_decidable_and_not_reported()
    {
        const string Source = """
            using Blocker.Sdk.Events;
            public sealed record ProcessBlocked : IBlockerEvent { public System.DateTimeOffset OccurredAt => default; }
            public static class C
            {
                public static void Generic<T>(IEventBus bus) where T : class, IBlockerEvent
                {
                    bus.Subscribe<T>((e, ct) => System.Threading.Tasks.ValueTask.CompletedTask);
                }

                public static void Local(IEventBus bus)
                {
                    bus.Subscribe<ProcessBlocked>((e, ct) => System.Threading.Tasks.ValueTask.CompletedTask);
                }
            }
            """;

        Assert.DoesNotContain(PluginTestSupport.Analyze(Source), d => d.RuleId == "BLK008");
    }

    [Theory]
    [InlineData("bus.SubscribeCustom(\"t\", (e, ct) => System.Threading.Tasks.ValueTask.CompletedTask);", "Blocker.Sdk.Events.IEventBus bus", PluginCapability.CustomEvents)]
    [InlineData("bus.PublishCustomAsync(\"name\", \"{}\");", "Blocker.Sdk.Events.IEventBus bus", PluginCapability.CustomEvents)]
    [InlineData("ipc.Register(\"ping\", (json, ct) => System.Threading.Tasks.ValueTask.FromResult(json));", "Blocker.Sdk.Ipc.IIpcRegistry ipc", PluginCapability.Ipc)]
    public void Custom_events_and_ipc_calls_without_their_capability_are_reported(string statement, string parameter, PluginCapability capability)
    {
        string source = $$"""
            public static class C
            {
                public static void M({{parameter}})
                {
                    {{statement}}
                }
            }
            """;

        PluginDiagnostic diagnostic = Single(source);

        Assert.Equal("BLK008", diagnostic.RuleId);
        Assert.Equal(5, diagnostic.Line);
        Assert.Contains($"'{PluginCapabilities.ToManifestName(capability)}'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(PluginTestSupport.Analyze(source, capability));
    }

    [Fact]
    public void Every_capability_table_entry_names_an_sdk_symbol_that_exists()
    {
        CSharpCompilation compilation = PluginTestSupport.CompilationOf("public class C { }");

        foreach (string contract in ApiPolicyRules.ExtensionInterfaces.Keys)
        {
            Assert.True(compilation.GetTypeByMetadataName(contract) is { TypeKind: TypeKind.Interface }, contract);
        }

        foreach (CapabilityCall call in ApiPolicyRules.CapabilityCalls)
        {
            INamedTypeSymbol? type = compilation.GetTypeByMetadataName(call.Type);
            Assert.True(type is not null && type.GetMembers(call.Method).Length > 0, $"{call.Type}.{call.Method}");
        }
    }

    [Fact]
    public async Task Agent_instructions_document_every_rule_id_and_denied_symbol()
    {
        string doc = (await PluginTestSupport.StarterFilesAsync())["AGENT_INSTRUCTIONS.md"];
        List<string> missing = [];
        foreach (string id in ApiPolicyRules.All.Select(r => r.Id).Concat(ApiPolicyRules.Syntax.Select(r => r.Id)).Append(ApiPolicyRules.MissingCapabilityId).Distinct())
        {
            if (!doc.Contains(id, StringComparison.Ordinal))
            {
                missing.Add(id);
            }
        }

        foreach (ApiRule rule in ApiPolicyRules.All)
        {
            string name = rule.Target[(rule.Target.LastIndexOf('.') + 1)..];
            if (rule.Scope == ApiRuleScope.Namespace)
            {
                name = rule.Target;
            }

            if (!doc.Contains(name, StringComparison.Ordinal))
            {
                missing.Add($"{rule.Id} {rule.Target}");
            }

            foreach (string member in rule.Members ?? [])
            {
                if (member != ".ctor" && !doc.Contains(member, StringComparison.Ordinal))
                {
                    missing.Add($"{rule.Id} {rule.Target}.{member}");
                }
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Rule_ids_are_well_formed_and_every_rule_has_a_reason()
    {
        Assert.All(ApiPolicyRules.All, r =>
        {
            Assert.Matches("^BLK0[1-4][0-9]$", r.Id);
            Assert.False(string.IsNullOrWhiteSpace(r.Reason));
        });
        Assert.All(ApiPolicyRules.Syntax, r => Assert.Matches("^BLK00[1-5]$", r.Id));
    }

    [Fact]
    public void Every_rule_targets_a_symbol_that_exists()
    {
        CSharpCompilation compilation = PluginTestSupport.CompilationOf("public class C { }");
        List<string> missing = [];
        foreach (ApiRule rule in ApiPolicyRules.All)
        {
            if (rule.Scope == ApiRuleScope.Namespace)
            {
                if (!NamespaceExists(compilation, rule.Target))
                {
                    missing.Add($"{rule.Id} namespace {rule.Target}");
                }

                foreach (string except in rule.Except ?? [])
                {
                    if (ResolveTypes(compilation, except).Count == 0)
                    {
                        missing.Add($"{rule.Id} exemption {except}");
                    }
                }

                continue;
            }

            IReadOnlyList<INamedTypeSymbol> types = ResolveTypes(compilation, rule.Target);
            if (types.Count == 0)
            {
                missing.Add($"{rule.Id} type {rule.Target}");
                continue;
            }

            foreach (string member in (rule.Members ?? []).Concat(rule.Scope == ApiRuleScope.Members ? rule.Except ?? [] : []))
            {
                if (!types.Any(t => member == ".ctor" ? t.InstanceConstructors.Length > 0 : t.GetMembers(member).Length > 0))
                {
                    missing.Add($"{rule.Id} member {rule.Target}.{member}");
                }
            }
        }

        Assert.Empty(missing);
    }

    private static bool NamespaceExists(CSharpCompilation compilation, string name)
    {
        INamespaceSymbol current = compilation.GlobalNamespace;
        foreach (string part in name.Split('.'))
        {
            INamespaceSymbol? next = current.GetNamespaceMembers().FirstOrDefault(n => n.Name == part);
            if (next is null)
            {
                return false;
            }

            current = next;
        }

        return true;
    }

    /// <summary>Resolves a rule target (generic arity ignored, nested types written with '.') to its type symbols.</summary>
    private static IReadOnlyList<INamedTypeSymbol> ResolveTypes(CSharpCompilation compilation, string target)
    {
        List<INamedTypeSymbol> result = [];
        string[] parts = target.Split('.');
        for (int split = parts.Length - 1; split >= 1; split--)
        {
            string namespaceName = string.Join('.', parts[..split]);
            string[] nested = parts[split..];
            foreach (string arity in new[] { string.Empty, "`1", "`2" })
            {
                string metadataName = namespaceName + "." + nested[0] + (nested.Length == 1 ? arity : string.Empty);
                INamedTypeSymbol? type = compilation.GetTypeByMetadataName(metadataName);
                foreach (string inner in nested.Skip(1))
                {
                    type = type?.GetTypeMembers(inner).FirstOrDefault();
                }

                if (type is not null)
                {
                    result.Add(type);
                }
            }
        }

        return result;
    }
}
