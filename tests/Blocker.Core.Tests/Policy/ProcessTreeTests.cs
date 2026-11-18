using Blocker.Core.Policy;
using Blocker.Core.Ports;

namespace Blocker.Core.Tests.Policy;

public sealed class ProcessTreeTests
{
    private static ProcessInfo P(int pid, int parent, long ticks) => new(pid, parent, 1, @"C:\x\a.exe", ticks);

    [Fact]
    public void Resolves_parent_and_inheritance_flag()
    {
        var tree = new ProcessTree();
        tree.Add(P(10, 4, 100), allowsInheritance: true);
        ProcessInfo child = P(11, 10, 200);

        Assert.True(tree.TryResolveParent(child, out ProcessKey parent));
        Assert.Equal(new ProcessKey(10, 100), parent);
        Assert.True(tree.ParentAllowsInheritance(child));
        tree.Add(child, allowsInheritance: false);
        Assert.True(tree.TryGetNode(new ProcessKey(11, 200), out ProcessNode node));
        Assert.Equal(new ProcessNode(new ProcessKey(10, 100), false, false), node);
        Assert.False(tree.ParentAllowsInheritance(P(12, 11, 300)));
    }

    [Fact]
    public void Unknown_or_missing_parent_does_not_resolve()
    {
        var tree = new ProcessTree();

        Assert.False(tree.TryResolveParent(P(11, 10, 200), out _));
        Assert.False(tree.TryResolveParent(P(11, 0, 200), out _));
        Assert.False(tree.ParentAllowsInheritance(P(11, 10, 200)));
    }

    [Fact]
    public void Parent_started_after_the_child_is_a_reused_pid_and_is_ignored()
    {
        var tree = new ProcessTree();
        tree.Add(P(10, 4, 500), allowsInheritance: true);

        Assert.False(tree.TryResolveParent(P(11, 10, 400), out _));
        Assert.False(tree.ParentAllowsInheritance(P(11, 10, 400)));
        Assert.True(tree.TryResolveParent(P(11, 10, 500), out _));
    }

    [Fact]
    public void Reusing_a_pid_retires_the_previous_holder()
    {
        var tree = new ProcessTree();
        tree.Add(P(10, 4, 100), allowsInheritance: true);
        tree.Add(P(10, 4, 900), allowsInheritance: false);

        Assert.True(tree.TryGetNode(new ProcessKey(10, 100), out ProcessNode old));
        Assert.True(old.Exited);
        Assert.False(tree.ParentAllowsInheritance(P(11, 10, 1000)));
    }

    [Fact]
    public void Exited_nodes_stay_available_for_late_child_events()
    {
        var tree = new ProcessTree();
        tree.Add(P(10, 4, 100), allowsInheritance: true);
        tree.MarkExited(10, 100);

        Assert.True(tree.TryGetNode(new ProcessKey(10, 100), out ProcessNode node));
        Assert.True(node.Exited);
        Assert.True(tree.ParentAllowsInheritance(P(11, 10, 200)));
    }

    [Fact]
    public void Eviction_removes_exited_nodes_first_in_exit_order()
    {
        var tree = new ProcessTree(maxNodes: 3);
        tree.Add(P(1, 0, 10), true);
        tree.Add(P(2, 0, 20), true);
        tree.Add(P(3, 0, 30), true);
        tree.MarkExited(2, 20);
        tree.MarkExited(1, 10);

        tree.Add(P(4, 0, 40), true);

        Assert.Equal(3, tree.Count);
        Assert.False(tree.TryGetNode(new ProcessKey(2, 20), out _));
        Assert.True(tree.TryGetNode(new ProcessKey(1, 10), out _));
        Assert.True(tree.TryGetNode(new ProcessKey(3, 30), out _));
        Assert.True(tree.TryGetNode(new ProcessKey(4, 40), out _));

        tree.Add(P(5, 0, 50), true);

        Assert.False(tree.TryGetNode(new ProcessKey(1, 10), out _));
        Assert.True(tree.TryGetNode(new ProcessKey(3, 30), out _));
        Assert.Equal(3, tree.Count);
    }

    [Fact]
    public void Size_stays_bounded_even_when_nothing_has_exited()
    {
        var tree = new ProcessTree(maxNodes: 4);

        for (int i = 1; i <= 50; i++)
        {
            tree.Add(P(i, 0, i), true);
        }

        Assert.Equal(4, tree.Count);
    }

    [Fact]
    public void Evicted_parent_no_longer_resolves()
    {
        var tree = new ProcessTree(maxNodes: 1);
        tree.Add(P(10, 4, 100), true);
        tree.MarkExited(10, 100);
        tree.Add(P(20, 4, 200), false);

        Assert.False(tree.TryGetNode(new ProcessKey(10, 100), out _));
        Assert.False(tree.ParentAllowsInheritance(P(11, 10, 300)));
    }

    [Fact]
    public void Rejects_non_positive_bound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessTree(0));
    }
}
