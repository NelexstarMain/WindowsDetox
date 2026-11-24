using Blocker.Core.Ports;
using Blocker.Sdk.Extensions;

namespace Blocker.Core.Policy;

/// <summary>Outcome of a decision.</summary>
public enum VerdictKind
{
    /// <summary>Leave the process alone.</summary>
    Allow,

    /// <summary>Terminate it and show the block screen.</summary>
    Block,
}

/// <summary>Rule that produced a verdict.</summary>
public enum VerdictReason
{
    /// <summary>Services session; never touched.</summary>
    Session0,

    /// <summary>Hardcoded critical Windows process.</summary>
    Critical,

    /// <summary>One of Blocker's own executables.</summary>
    BlockerOwn,

    /// <summary>Bypass tool (always blocked during a session).</summary>
    BypassTool,

    /// <summary>Allowlisted by SHA-256.</summary>
    AllowlistHash,

    /// <summary>Allowlisted by path (entry without hash).</summary>
    AllowlistPath,

    /// <summary>Allowlisted by Store package family name.</summary>
    AllowlistPackage,

    /// <summary>Child of an allowlisted (or inheriting) parent.</summary>
    InheritedFromAllowed,

    /// <summary>Microsoft-signed system binary.</summary>
    SystemBaseline,

    /// <summary>Emergency pass active.</summary>
    EmergencyPass,

    /// <summary>No session running.</summary>
    NoSession,

    /// <summary>Not covered by any allow rule.</summary>
    NotAllowlisted,

    /// <summary>A plugin interceptor tightened an allow into a block.</summary>
    PluginInterceptor,

    /// <summary>Outside a session: the app is inside a scheduled block of its per-app rule.</summary>
    AppScheduledBlock,

    /// <summary>Outside a session: the app used up the time pool of its per-app rule.</summary>
    AppBudgetExhausted,
}

/// <summary>Enforcement state at decision time.</summary>
public enum EnforcementMode
{
    /// <summary>A session is active.</summary>
    Active,

    /// <summary>No session is running.</summary>
    NoSession,

    /// <summary>The daily emergency pass is in effect.</summary>
    EmergencyPass,
}

/// <summary>Decision for one process.</summary>
/// <param name="Kind">Allow or block.</param>
/// <param name="Reason">Deciding rule.</param>
/// <param name="Detail">Plugin-supplied user-visible reason for <see cref="VerdictReason.PluginInterceptor"/> blocks.</param>
public readonly record struct ProcessVerdict(VerdictKind Kind, VerdictReason Reason, string? Detail = null)
{
    /// <summary>True for <see cref="VerdictKind.Allow"/>.</summary>
    public bool IsAllow => Kind == VerdictKind.Allow;

    /// <summary>
    /// True when children may inherit the allow: only user allowlist rules and inheritance qualify, never Critical, SystemBaseline or session-less verdicts.
    /// </summary>
    public bool AllowsInheritance =>
        Kind == VerdictKind.Allow
        && Reason is VerdictReason.AllowlistHash or VerdictReason.AllowlistPath or VerdictReason.AllowlistPackage or VerdictReason.InheritedFromAllowed;
}

/// <summary>Everything the policy needs to decide.</summary>
/// <param name="Process">Process facts from the monitor.</param>
/// <param name="Sha256">Hex SHA-256 of the image, or <see langword="null"/> when unreadable.</param>
/// <param name="IsMicrosoftSigned">Image is validly signed by Microsoft.</param>
/// <param name="PackageFamilyName">Store package family name, or <see langword="null"/>.</param>
/// <param name="ParentAllowsInheritance"><see cref="ProcessTree.ParentAllowsInheritance"/> for this process.</param>
/// <param name="Mode">Session state.</param>
public sealed record ProcessFacts(
    ProcessInfo Process,
    string? Sha256 = null,
    bool IsMicrosoftSigned = false,
    string? PackageFamilyName = null,
    bool ParentAllowsInheritance = false,
    EnforcementMode Mode = EnforcementMode.Active);

/// <summary>
/// Pure decision logic, in the order of ARCHITECTURE.md: Session 0, critical, (no session or emergency pass), bypass tool, allowlist,
/// inheritance, system baseline, block.
/// </summary>
public sealed class EnforcementPolicy
{
    private readonly CriticalProcesses _critical;
    private readonly BypassTools _bypass;
    private readonly Allowlist _allowlist;
    private readonly string[] _baselineRoots;
    private readonly string[] _writableSubtrees;

    /// <summary>
    /// Subtrees below the Windows directory that standard users can write to and run programs from (the classic AppLocker-bypass folders),
    /// relative to it. A Microsoft-signed program copied there must not ride on the system baseline.
    /// </summary>
    internal static readonly string[] WritableSubtrees =
    [
        @"tasks", @"temp", @"tracing", @"registration\crmlog", @"debug\wia",
        @"system32\tasks", @"system32\tasks_migrated", @"system32\spool\drivers\color", @"system32\spool\printers", @"system32\spool\servers",
        @"system32\fxstmp", @"system32\com\dmp", @"system32\microsoft\crypto\rsa\machinekeys",
        @"syswow64\tasks", @"syswow64\spool\drivers\color", @"syswow64\fxstmp", @"syswow64\com\dmp",
    ];

    /// <summary>Creates the policy.</summary>
    /// <param name="critical">Critical process classifier.</param>
    /// <param name="bypass">Bypass tool matcher.</param>
    /// <param name="allowlist">Session allowlist.</param>
    /// <param name="windowsDirectory"><c>%SystemRoot%</c>; baseline = <c>System32</c>, <c>SysWOW64</c> and <c>SystemApps</c> below it.</param>
    public EnforcementPolicy(CriticalProcesses critical, BypassTools bypass, Allowlist allowlist, string windowsDirectory)
    {
        ArgumentNullException.ThrowIfNull(critical);
        ArgumentNullException.ThrowIfNull(bypass);
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowsDirectory);
        (_critical, _bypass, _allowlist) = (critical, bypass, allowlist);
        string root = WinPath.Normalize(windowsDirectory);
        _baselineRoots = [root + @"\system32", root + @"\syswow64", root + @"\systemapps"];
        _writableSubtrees = [.. WritableSubtrees.Select(relative => root + @"\" + relative)];
    }

    /// <summary>Decides what to do with a process.</summary>
    public ProcessVerdict Decide(ProcessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ProcessInfo process = facts.Process;
        if (process.SessionId == 0)
        {
            return Allow(VerdictReason.Session0);
        }

        switch (_critical.Classify(process.ImagePath))
        {
            case CriticalKind.System:
                return Allow(VerdictReason.Critical);
            case CriticalKind.BlockerOwn:
                return Allow(VerdictReason.BlockerOwn);
        }

        if (facts.Mode == EnforcementMode.EmergencyPass)
        {
            return Allow(VerdictReason.EmergencyPass);
        }

        if (facts.Mode == EnforcementMode.NoSession)
        {
            return Allow(VerdictReason.NoSession);
        }

        if (_bypass.IsBypassTool(process.ImagePath, facts.Sha256))
        {
            return new ProcessVerdict(VerdictKind.Block, VerdictReason.BypassTool);
        }

        if (_allowlist.Match(process.ImagePath, facts.Sha256, facts.PackageFamilyName) is { } rule)
        {
            return Allow(rule);
        }

        if (facts.ParentAllowsInheritance)
        {
            return Allow(VerdictReason.InheritedFromAllowed);
        }

        if (facts.IsMicrosoftSigned && IsSystemBaselinePath(process.ImagePath))
        {
            return Allow(VerdictReason.SystemBaseline);
        }

        return new ProcessVerdict(VerdictKind.Block, VerdictReason.NotAllowlisted);
    }

    /// <summary>
    /// Applies plugin interceptor decisions. Plugins can only turn an Allow into a Block; Session 0, critical and Blocker's own processes are never affected.
    /// </summary>
    public static ProcessVerdict CombineWithPlugins(ProcessVerdict core, IEnumerable<InterceptDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (!core.IsAllow || core.Reason is VerdictReason.Session0 or VerdictReason.Critical or VerdictReason.BlockerOwn)
        {
            return core;
        }

        foreach (InterceptDecision decision in decisions)
        {
            if (decision.Verdict == InterceptVerdict.Block)
            {
                return new ProcessVerdict(VerdictKind.Block, VerdictReason.PluginInterceptor, decision.Reason);
            }
        }

        return core;
    }

    private static ProcessVerdict Allow(VerdictReason reason) => new(VerdictKind.Allow, reason);

    private bool IsSystemBaselinePath(string imagePath)
    {
        string path = WinPath.Normalize(imagePath);
        foreach (string root in _baselineRoots)
        {
            if (WinPath.IsUnder(path, root))
            {
                return !_writableSubtrees.Any(subtree => WinPath.IsUnder(path, subtree));
            }
        }

        return false;
    }
}
