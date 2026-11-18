using Blocker.Core.Ports;

namespace Blocker.Core.Policy;

/// <summary>Process identity: pids are reused, so the creation time is part of the key.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="StartTimeTicks">Creation time as FILETIME ticks.</param>
public readonly record struct ProcessKey(int ProcessId, long StartTimeTicks)
{
    /// <summary>Key of a <see cref="ProcessInfo"/>.</summary>
    public static ProcessKey Of(ProcessInfo process) => new(process.ProcessId, process.StartTimeTicks);
}

/// <summary>A tracked process.</summary>
/// <param name="Parent">Resolved parent, or <see langword="null"/> when the parent was unknown or its pid had been reused.</param>
/// <param name="AllowsInheritance">
/// True when the process was allowed by an allowlist rule or by inheritance, so its children may inherit the allow.
/// Never true for critical, baseline, session-less or blocked processes.
/// </param>
/// <param name="Exited">The process has exited; the node is kept for late child events until evicted.</param>
public readonly record struct ProcessNode(ProcessKey? Parent, bool AllowsInheritance, bool Exited);

/// <summary>
/// Tracks the process tree keyed by (pid, start time) so inheritance can be decided safely. A parent resolves only if its start time is not
/// later than the child's (PID-reuse guard). Exited nodes are retained (late child events) and evicted first once <paramref name="maxNodes"/> is exceeded.
/// </summary>
public sealed class ProcessTree(int maxNodes = 16384)
{
    private readonly int _maxNodes = maxNodes > 0 ? maxNodes : throw new ArgumentOutOfRangeException(nameof(maxNodes));
    private readonly object _gate = new();
    private readonly Dictionary<ProcessKey, ProcessNode> _nodes = [];
    private readonly Dictionary<int, ProcessKey> _latestByPid = [];
    private readonly Queue<ProcessKey> _exitOrder = new();

    /// <summary>Number of tracked nodes.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _nodes.Count;
            }
        }
    }

    /// <summary>Records a process with its final verdict (<see cref="ProcessVerdict.AllowsInheritance"/>).</summary>
    public void Add(ProcessInfo process, bool allowsInheritance)
    {
        ArgumentNullException.ThrowIfNull(process);
        var key = ProcessKey.Of(process);
        lock (_gate)
        {
            ProcessKey? parent = ResolveParentLocked(process);
            if (_latestByPid.TryGetValue(key.ProcessId, out ProcessKey previous) && previous != key && _nodes.TryGetValue(previous, out ProcessNode old) && !old.Exited)
            {
                // Same pid again: the previous holder must be gone.
                _nodes[previous] = old with { Exited = true };
                _exitOrder.Enqueue(previous);
            }

            _nodes[key] = new ProcessNode(parent, allowsInheritance, Exited: false);
            _latestByPid[key.ProcessId] = key;
            Trim();
        }
    }

    /// <summary>Marks a process as exited; its node stays until evicted.</summary>
    public void MarkExited(int processId, long startTimeTicks)
    {
        var key = new ProcessKey(processId, startTimeTicks);
        lock (_gate)
        {
            if (_nodes.TryGetValue(key, out ProcessNode node) && !node.Exited)
            {
                _nodes[key] = node with { Exited = true };
                _exitOrder.Enqueue(key);
            }
        }
    }

    /// <summary>Looks up a node.</summary>
    public bool TryGetNode(ProcessKey key, out ProcessNode node)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(key, out node);
        }
    }

    /// <summary>Resolves the parent of <paramref name="child"/>; false when unknown or when the pid was reused after the child started.</summary>
    public bool TryResolveParent(ProcessInfo child, out ProcessKey parent)
    {
        ArgumentNullException.ThrowIfNull(child);
        lock (_gate)
        {
            ProcessKey? resolved = ResolveParentLocked(child);
            parent = resolved.GetValueOrDefault();
            return resolved.HasValue;
        }
    }

    /// <summary>True when the child's resolved parent allows inheritance. Feeds <see cref="ProcessFacts.ParentAllowsInheritance"/>.</summary>
    public bool ParentAllowsInheritance(ProcessInfo child)
    {
        ArgumentNullException.ThrowIfNull(child);
        lock (_gate)
        {
            return ResolveParentLocked(child) is { } parent && _nodes.TryGetValue(parent, out ProcessNode node) && node.AllowsInheritance;
        }
    }

    private ProcessKey? ResolveParentLocked(ProcessInfo child) =>
        child.ParentProcessId > 0
        && _latestByPid.TryGetValue(child.ParentProcessId, out ProcessKey parent)
        && parent.StartTimeTicks <= child.StartTimeTicks
        && parent != ProcessKey.Of(child)
            ? parent
            : null;

    private void Trim()
    {
        while (_nodes.Count > _maxNodes)
        {
            ProcessKey victim;
            if (_exitOrder.Count > 0)
            {
                victim = _exitOrder.Dequeue();
                if (!_nodes.TryGetValue(victim, out ProcessNode node) || !node.Exited)
                {
                    continue;
                }
            }
            else
            {
                // Last resort (more live processes than the bound): drop an arbitrary node; its children simply fail closed.
                victim = default;
                foreach (ProcessKey key in _nodes.Keys)
                {
                    victim = key;
                    break;
                }
            }

            _nodes.Remove(victim);
            if (_latestByPid.TryGetValue(victim.ProcessId, out ProcessKey latest) && latest == victim)
            {
                _latestByPid.Remove(victim.ProcessId);
            }
        }
    }
}
