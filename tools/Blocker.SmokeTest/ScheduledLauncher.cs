using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Blocker.SmokeTest;

/// <summary>
/// Starts programs through the Windows Task Scheduler (late-bound COM, so no other process is spawned) as the signed-in user in their
/// interactive session. The service decides inheritance by the process that CREATED a child (taken from ETW), and this tool is
/// allowlisted so it survives the session: anything started directly from here would inherit the allow and could never be blocked.
/// A task is created by the Schedule service (a system process that never passes the allow on), so the child is judged on its own.
/// </summary>
internal sealed class ScheduledLauncher : IDisposable
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int ActionExec = 0;

    private readonly string _prefix = $"WindowsDetoxSmoke-{Guid.NewGuid():N}";
    private readonly Dictionary<string, object> _tasks = [];
    private object? _service;
    private object? _folder;

    /// <summary>Registers (but does not start) a task named after <paramref name="key"/>.</summary>
    public void Register(string key, string executable, string arguments)
    {
        if (_service is null)
        {
            object service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
            _ = Call(service, "Connect");
            _folder = Call(service, "GetFolder", "\\");
            _service = service;
        }

        object definition = Call(_service, "NewTask", 0)!;
        object settings = Get(definition, "Settings")!;
        Set(Get(definition, "RegistrationInfo")!, "Description", "WindowsDetox smoke test probe (removed by the test)");
        Set(settings, "Enabled", true);
        Set(settings, "AllowDemandStart", true);
        Set(settings, "DisallowStartIfOnBatteries", false);
        Set(settings, "StopIfGoingOnBatteries", false);
        Set(settings, "ExecutionTimeLimit", "PT0S");
        Set(Get(definition, "Principal")!, "LogonType", TaskLogonInteractiveToken);
        object action = Call(Get(definition, "Actions")!, "Create", ActionExec)!;
        Set(action, "Path", executable);
        Set(action, "Arguments", arguments);
        _tasks[key] = Call(
            _folder!,
            "RegisterTaskDefinition",
            TaskName(key),
            definition,
            TaskCreateOrUpdate,
            WindowsIdentity.GetCurrent().Name,
            null,
            TaskLogonInteractiveToken,
            null)!;
    }

    public void Start(string key) => _ = Call(_tasks[key], "Run", (object?)null);

    /// <summary>True when the scheduler recorded a run of the task at or after <paramref name="since"/> (local time).</summary>
    public bool HasRunSince(string key, DateTime since) => (DateTime)Get(Fresh(key), "LastRunTime")! >= since;

    /// <summary>The scheduler's own record of the task (state, last run, last result; 0x41303 means it has not run yet).</summary>
    public string Describe(string key)
    {
        object task = Fresh(key);
        DateTime lastRun = (DateTime)Get(task, "LastRunTime")!;
        int lastResult = (int)Get(task, "LastTaskResult")!;
        return $"scheduler task {key}: state={Get(task, "State")}, lastRun={lastRun:u}, lastResult=0x{lastResult:X}";
    }

    public void Dispose()
    {
        foreach (string key in _tasks.Keys)
        {
            try
            {
                _ = Call(_folder!, "DeleteTask", TaskName(key), 0);
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException)
            {
                Console.WriteLine($"     could not delete scheduled task '{TaskName(key)}': {ex.Message}");
            }
        }

        _tasks.Clear();
    }

    // A registered-task object may cache its properties, so the run history is always read from a freshly fetched one.
    private object Fresh(string key) => Call(_folder!, "GetTask", TaskName(key))!;

    private string TaskName(string key) => $"{_prefix}-{key}";

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    private static object? Call(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, arguments);
}
