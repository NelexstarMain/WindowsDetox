using Blocker.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Blocker.Core.Plugins;

/// <summary>How an <see cref="ApiRule"/> selects symbols.</summary>
internal enum ApiRuleScope
{
    /// <summary>Every type in the namespace <c>Target</c> (and nested namespaces), and all their members. <c>Except</c> lists exempt type full names.</summary>
    Namespace,

    /// <summary>The type <c>Target</c> (generic arity ignored) and all its members.</summary>
    Type,

    /// <summary>Every type whose full name starts with <c>Target</c>, and all their members.</summary>
    TypePrefix,

    /// <summary>Only members of the type <c>Target</c>: those named in <c>Members</c>, or all except <c>Except</c> when <c>Members</c> is <see langword="null"/>. Mentioning the type is fine.</summary>
    Members,
}

/// <summary>One entry of the API deny list. A rule denies the symbols it selects unless <see cref="UnlockedBy"/> was declared.</summary>
internal sealed record ApiRule(string Id, string Reason, string Target, ApiRuleScope Scope)
{
    /// <summary>Gets the member names a <see cref="ApiRuleScope.Members"/> rule denies (<c>.ctor</c> is a constructor).</summary>
    public IReadOnlyCollection<string>? Members { get; init; }

    /// <summary>Gets exempt type names (<see cref="ApiRuleScope.Namespace"/>) or exempt member names (<see cref="ApiRuleScope.Members"/>).</summary>
    public IReadOnlyCollection<string>? Except { get; init; }

    /// <summary>Gets the capability that lifts this rule.</summary>
    public PluginCapability? UnlockedBy { get; init; }

    /// <summary>Gets an extra condition on the selected symbol.</summary>
    public Func<ISymbol, bool>? Where { get; init; }
}

/// <summary>A rule on syntax alone (language features that have no safe subset).</summary>
internal sealed record SyntaxRule(string Id, string Reason, SyntaxKind Kind);

/// <summary>
/// The auditable API policy: every rule the plugin analyzer applies is in <see cref="All"/> (symbol rules, first match
/// wins, so specific entries precede general ones) or <see cref="Syntax"/>. Anything not listed is allowed, in
/// particular <c>System.Threading.Interlocked</c>/<c>Volatile</c>, <c>CancellationToken</c>, <c>Task</c>/<c>ValueTask</c>
/// (async), <c>System.Text.Json</c>, collections, LINQ, <c>Regex</c>, <c>Path</c> string helpers, <c>StringReader</c>,
/// <c>StringWriter</c> and <c>MemoryStream</c>.
/// </summary>
internal static class ApiPolicyRules
{
    public const string AsyncVoidId = "BLK007";
    public const string AsyncVoidReason = "'async void' hides exceptions from the host; return Task or ValueTask.";
    public const string DynamicId = "BLK006";
    public const string DynamicReason = "'dynamic' binds members at run time and bypasses the API policy.";

    private const string Clock = "Use context.Time (TimeProvider); the real clock makes plugins and tests non-deterministic.";
    private const string FileSystem = "Plugins cannot access the file system; use context.Storage.";
    private const string Blocking = "Blocking waits stall the callback budget; use async/await.";
    private const string NeedsNetwork = "Requires the 'network' capability.";
    private const string Reflection = "Reflection that invokes members by name bypasses the API policy.";
    private const string Loading = "Plugins cannot create code or load assemblies dynamically.";

    private static readonly string[] HashingTypes =
    [
        "System.Security.Cryptography.SHA256",
        "System.Security.Cryptography.SHA384",
        "System.Security.Cryptography.SHA512",
        "System.Security.Cryptography.HMACSHA256",
        "System.Security.Cryptography.HMACSHA384",
        "System.Security.Cryptography.HMACSHA512",
        "System.Security.Cryptography.HashAlgorithm",
        "System.Security.Cryptography.HMAC",
        "System.Security.Cryptography.KeyedHashAlgorithm",
        "System.Security.Cryptography.IncrementalHash",
        "System.Security.Cryptography.HashAlgorithmName",
        "System.Security.Cryptography.RandomNumberGenerator",
        "System.Security.Cryptography.CryptographicOperations",
        "System.Security.Cryptography.CryptographicException",
    ];

    /// <summary>Gets the syntax rules.</summary>
    public static IReadOnlyList<SyntaxRule> Syntax { get; } =
    [
        new("BLK001", "'unsafe' code is not allowed.", SyntaxKind.UnsafeKeyword),
        new("BLK002", "'fixed' (pinned pointers and buffers) is not allowed.", SyntaxKind.FixedKeyword),
        new("BLK003", "Function pointers are not allowed.", SyntaxKind.FunctionPointerType),
        new("BLK004", "'extern' (native) methods are not allowed.", SyntaxKind.ExternKeyword),
        new("BLK005", "'__arglist' and the typed-reference keywords are not allowed.", SyntaxKind.ArgListKeyword),
        new("BLK005", "'__arglist' and the typed-reference keywords are not allowed.", SyntaxKind.MakeRefKeyword),
        new("BLK005", "'__arglist' and the typed-reference keywords are not allowed.", SyntaxKind.RefTypeKeyword),
        new("BLK005", "'__arglist' and the typed-reference keywords are not allowed.", SyntaxKind.RefValueKeyword),
    ];

    /// <summary>Gets the symbol rules in evaluation order.</summary>
    public static IReadOnlyList<ApiRule> All { get; } =
    [
        // BLK025: real clocks
        Members("BLK025", Clock, "System.DateTime", "Now", "UtcNow", "Today"),
        Members("BLK025", Clock, "System.DateTimeOffset", "Now", "UtcNow"),
        Members("BLK025", Clock, "System.TimeProvider", "System"),
        Members("BLK025", Clock, "System.Environment", "TickCount", "TickCount64"),
        .. Types("BLK025", Clock, null, "System.Diagnostics.Stopwatch"),

        // BLK010: processes and debugging
        new("BLK010", "Plugins cannot start or inspect processes.", "System.Diagnostics.Process", ApiRuleScope.TypePrefix),
        .. Types("BLK010", "Plugins cannot attach or interact with debuggers.", null, "System.Diagnostics.Debugger"),

        // BLK011: dynamic code and assembly loading
        new("BLK011", Loading, "System.Reflection.Emit", ApiRuleScope.Namespace),
        new("BLK011", Loading, "System.Runtime.Loader", ApiRuleScope.Namespace),
        Members("BLK011", Loading, "System.Reflection.Assembly", "Load", "LoadFrom", "LoadFile", "UnsafeLoadFrom", "CreateInstance"),
        Members("BLK011", Loading, "System.Type", "GetType", "GetTypeFromProgID", "GetTypeFromCLSID") with { Where = s => s is IMethodSymbol { IsStatic: true } },
        Members("BLK011", Loading, "System.Linq.Expressions.LambdaExpression", "Compile"),
        Members("BLK011", Loading, "System.Linq.Expressions.Expression", "Compile"),
        .. Types("BLK011", Loading, null, "System.Activator"),

        // BLK012: reflection that invokes by name
        Members("BLK012", Reflection, "System.Reflection.MethodBase", "Invoke"),
        Members("BLK012", Reflection, "System.Reflection.ConstructorInfo", "Invoke"),
        Members("BLK012", Reflection, "System.Reflection.MethodInfo", "CreateDelegate"),
        Members("BLK012", Reflection, "System.Reflection.PropertyInfo", "GetValue", "SetValue", "GetConstantValue"),
        Members("BLK012", Reflection, "System.Reflection.FieldInfo", "GetValue", "SetValue", "GetValueDirect", "SetValueDirect"),
        Members("BLK012", Reflection, "System.Type", "InvokeMember"),
        Members("BLK012", Reflection, "System.Delegate", "DynamicInvoke", "CreateDelegate"),

        // BLK013 / BLK014: native interop, unsafe memory, load-time code
        new("BLK013", "Native interop and unmanaged memory are not allowed.", "System.Runtime.InteropServices", ApiRuleScope.Namespace),
        .. Types("BLK013", "Unsafe memory access is not allowed.", null, "System.Runtime.CompilerServices.Unsafe", "System.Runtime.CompilerServices.UnsafeAccessorAttribute"),
        .. Types("BLK014", "Module initializers run code outside the plugin lifecycle.", null, "System.Runtime.CompilerServices.ModuleInitializerAttribute"),

        // BLK020: file system (and file-based APIs)
        .. Types("BLK020", FileSystem, null,
            "System.IO.File",
            "System.IO.Directory",
            "System.IO.FileStream",
            "System.IO.FileInfo",
            "System.IO.DirectoryInfo",
            "System.IO.DriveInfo",
            "System.IO.FileSystemInfo",
            "System.IO.FileSystemWatcher",
            "System.IO.RandomAccess",
            "System.IO.Compression.ZipFile",
            "System.IO.Compression.ZipFileExtensions",
            "System.Diagnostics.FileVersionInfo",
            "System.Diagnostics.TextWriterTraceListener",
            "System.Diagnostics.DelimitedListTraceListener",
            "System.Resources.ResourceReader",
            "System.Resources.ResourceWriter",
            "System.Net.FileWebRequest",
            "System.Net.FileWebResponse"),
        new("BLK020", FileSystem, "System.IO.Pipes", ApiRuleScope.Namespace),
        new("BLK020", FileSystem, "System.IO.MemoryMappedFiles", ApiRuleScope.Namespace),
        new("BLK020", FileSystem, "System.IO.IsolatedStorage", ApiRuleScope.Namespace),
        new("BLK020", FileSystem, "System.IO.Enumeration", ApiRuleScope.Namespace),
        new("BLK020", FileSystem, "System.Xml", ApiRuleScope.Namespace) { Where = TakesFileOrUri },
        Members("BLK020", FileSystem, "System.IO.Path", "GetTempPath", "GetTempFileName"),
        Members("BLK020", FileSystem, "System.IO.StreamReader", ".ctor") with { Where = FirstParameterIsString },
        Members("BLK020", FileSystem, "System.IO.StreamWriter", ".ctor") with { Where = FirstParameterIsString },
        Members("BLK020", FileSystem, "System.Resources.ResourceManager", "CreateFileBasedResourceManager"),

        // BLK021: registry and Win32
        new("BLK021", "Plugins cannot access the registry or other Win32 services.", "Microsoft.Win32", ApiRuleScope.Namespace),

        // BLK022: machine and runtime environment
        new("BLK022", "Plugins cannot read machine or process environment data.", "System.Environment", ApiRuleScope.Members) { Except = ["NewLine"] },
        .. Types("BLK022", "Plugins cannot control or inspect the runtime.", null, "System.AppDomain", "System.GC", "System.AppContext"),

        // BLK023: threads
        Members("BLK023", "Plugins cannot create or block threads; use async/await and ITickListener.", "System.Threading.Thread",
            ".ctor", "Start", "UnsafeStart", "Sleep", "Join", "Interrupt", "Abort", "SpinWait", "Yield"),
        .. Types("BLK023", "Plugins cannot use the thread pool directly; use async/await and ITickListener.", null, "System.Threading.ThreadPool"),

        // BLK024: blocking waits
        Members("BLK024", Blocking, "System.Threading.Tasks.Task", "Wait", "WaitAll", "WaitAny", "Result"),
        Members("BLK024", Blocking, "System.Threading.Tasks.ValueTask", "Result"),
        Members("BLK024", Blocking, "System.Runtime.CompilerServices.TaskAwaiter", "GetResult"),
        Members("BLK024", Blocking, "System.Runtime.CompilerServices.ValueTaskAwaiter", "GetResult"),
        Members("BLK024", Blocking, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable.ConfiguredTaskAwaiter", "GetResult"),
        Members("BLK024", Blocking, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable.ConfiguredValueTaskAwaiter", "GetResult"),
        Members("BLK024", Blocking, "System.Threading.SemaphoreSlim", "Wait"),
        Members("BLK024", Blocking, "System.Threading.ManualResetEventSlim", "Wait"),
        Members("BLK024", Blocking, "System.Threading.CountdownEvent", "Wait"),
        Members("BLK024", Blocking, "System.Threading.WaitHandle", "WaitOne", "WaitAll", "WaitAny"),

        // BLK030: raw networking, never allowed
        new("BLK030", "Raw sockets and network inspection are never allowed.", "System.Net.Sockets", ApiRuleScope.Namespace),
        new("BLK030", "Raw sockets and network inspection are never allowed.", "System.Net.NetworkInformation", ApiRuleScope.Namespace),
        new("BLK030", "Raw sockets and network inspection are never allowed.", "System.Net.Quic", ApiRuleScope.Namespace),

        // BLK031: high-level networking, needs the 'network' capability
        new("BLK031", NeedsNetwork, "System.Net.Http", ApiRuleScope.Namespace) { UnlockedBy = PluginCapability.Network },
        new("BLK031", NeedsNetwork, "System.Net.WebSockets", ApiRuleScope.Namespace) { UnlockedBy = PluginCapability.Network },
        new("BLK031", NeedsNetwork, "System.Net.Mail", ApiRuleScope.Namespace) { UnlockedBy = PluginCapability.Network },
        .. Types("BLK031", NeedsNetwork, PluginCapability.Network,
            "System.Net.WebClient",
            "System.Net.WebRequest",
            "System.Net.HttpWebRequest",
            "System.Net.HttpWebResponse",
            "System.Net.WebResponse",
            "System.Net.FtpWebRequest",
            "System.Net.Dns",
            "System.Net.ServicePoint",
            "System.Net.ServicePointManager"),

        // BLK040: security APIs except hashing
        new("BLK040", "Only hashing and random numbers are allowed from System.Security.", "System.Security", ApiRuleScope.Namespace) { Except = HashingTypes },

        // BLK041: console
        .. Types("BLK041", "Plugins cannot use the console; use context.Logger.", null, "System.Console"),
    ];

    private static ApiRule Members(string id, string reason, string type, params string[] members) =>
        new(id, reason, type, ApiRuleScope.Members) { Members = members };

    private static IEnumerable<ApiRule> Types(string id, string reason, PluginCapability? unlockedBy, params string[] types) =>
        types.Select(type => new ApiRule(id, reason, type, ApiRuleScope.Type) { UnlockedBy = unlockedBy });

    private static bool FirstParameterIsString(ISymbol symbol) =>
        symbol is IMethodSymbol { Parameters: [{ Type.SpecialType: SpecialType.System_String }, ..] };

    /// <summary>XML loaders and writers that take a file name or URI (<c>XDocument.Load(uri)</c>, <c>XmlReader.Create(uri)</c>, ...).</summary>
    private static bool TakesFileOrUri(ISymbol symbol) =>
        symbol is IMethodSymbol { Name: "Load" or "Save" or "Create" or ".ctor" } method
        && method.Parameters.Any(p => p.Type.SpecialType == SpecialType.System_String
            && p.Name is "uri" or "inputUri" or "url" or "filename" or "fileName" or "outputFileName" or "stylesheetUri" or "schemaUri");
}
