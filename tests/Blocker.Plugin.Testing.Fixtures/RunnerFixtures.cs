using System.Collections;
using Xunit;

// Sample classes that PluginTestRunner is pointed at by Blocker.Plugin.Testing.Tests. They live in a separate,
// non-test assembly on purpose: xUnit only discovers tests in the assembly it runs, so the intentionally failing,
// skipped, unsupported and hanging samples below never execute under xUnit itself.
namespace Blocker.Plugin.Testing.Fixtures;

public sealed class AllPassing
{
    [Fact]
    public void Sync() => Assert.True(true);

    [Fact]
    public async Task AsyncTask() => await Task.Yield();

    [Fact]
    public async ValueTask AsyncValueTask() => await Task.Yield();
}

public sealed class InlineTheory
{
    [Theory]
    [InlineData(2, "b")]
    [InlineData(1, "a")]
    public void Rows(int number, string text) => Assert.Equal(number == 1 ? "a" : "b", text);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SecondRowFails(int number) => Assert.Equal(1, number);
}

public sealed class UnsupportedData
{
    public static IEnumerable<object[]> Rows => [[1]];

    [Theory]
    [MemberData(nameof(Rows))]
    public void FromMember(int number) => Assert.True(number > 0);

    [Theory]
    [ClassData(typeof(RowSource))]
    public void FromClass(int number) => Assert.True(number > 0);
}

public sealed class RowSource : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return [1];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class TheoryWithoutData
{
    [Theory]
    public void NoRows(int number) => Assert.True(number > 0);
}

public sealed class SkippedTests
{
    [Fact(Skip = "not today")]
    public void SkippedFact() => Assert.True(true);

    [Theory(Skip = "not today")]
    [InlineData(1)]
    public void SkippedTheory(int number) => Assert.True(number > 0);
}

public sealed class ExplicitTests
{
    [Fact(Explicit = true)]
    public void OnlyOnRequest() => Assert.True(true);
}

public sealed class AsyncVoidTests
{
    [Fact]
    public async void Forbidden() => await Task.Yield();
}

public sealed class NoDefaultConstructor(int unused)
{
    [Fact]
    public void Never() => Assert.Equal(0, unused);
}

public sealed class FactWithParameter
{
    [Fact]
    public void Takes(int number) => Assert.True(number > 0);
}

public sealed class UnsupportedReturn
{
    [Fact]
    public int Returns() => 1;
}

public sealed class Failing
{
    [Fact]
    public void Throws() => throw new InvalidOperationException("boom\u0007\r\nsecond line");

    [Fact]
    public void FailedAssertion() => Assert.Equal(1, 2);

    [Fact]
    public void DeepStack() => Recurse(60);

    private static void Recurse(int depth)
    {
        if (depth == 0)
        {
            throw new InvalidOperationException("deep");
        }

        Recurse(depth - 1);
    }
}

public sealed class Lifecycle : IAsyncLifetime
{
    private static readonly List<string> Log = [];
    private int _calls;

    public static IReadOnlyList<string> Events
    {
        get
        {
            lock (Log)
            {
                return [.. Log];
            }
        }
    }

    public static void Reset()
    {
        lock (Log)
        {
            Log.Clear();
        }
    }

    public async ValueTask InitializeAsync()
    {
        await Task.Yield();
        Add("init");
    }

    public ValueTask DisposeAsync()
    {
        Add("disposeAsync");
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void A()
    {
        _calls++;
        Add("A");
        Assert.Equal(1, _calls);
    }

    [Fact]
    public void B()
    {
        _calls++;
        Add("B");
        Assert.Equal(1, _calls);
    }

    private static void Add(string entry)
    {
        lock (Log)
        {
            Log.Add(entry);
        }
    }
}

public sealed class SyncDisposable : IDisposable
{
    public static int Disposed;

    [Fact]
    public void Runs() => Assert.True(true);

    public void Dispose() => Interlocked.Increment(ref Disposed);
}

public sealed class DisposeThrows : IDisposable
{
    [Fact]
    public void Passes() => Assert.True(true);

    public void Dispose() => throw new InvalidOperationException("dispose failed");
}

public sealed class InitializeThrows : IAsyncLifetime
{
    public ValueTask InitializeAsync() => throw new InvalidOperationException("init failed");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void Never() => Assert.True(true);
}

public sealed class Hanging
{
    [Fact]
    public Task Hangs() => new TaskCompletionSource().Task;

    [Fact]
    public void ZPasses() => Assert.True(true);
}

public sealed class DisplayNames
{
    [Theory]
    [InlineData("a\nb", null, 'x', 1.5)]
    [InlineData("tab\there", "quote\"", '\u0001', -2)]
    public void Formats(string text, string? other, char letter, double number) => Assert.NotNull(text);

    [Theory]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    public void Long(string text) => Assert.NotEmpty(text);
}

public sealed class ZLast
{
    [Fact]
    public void Run() => Assert.True(true);
}

public sealed class AFirst
{
    [Fact]
    public void Beta() => Assert.True(true);

    [Fact]
    public void Alpha() => Assert.True(true);
}

public abstract class AbstractSkipped
{
    [Fact]
    public void Ignored() => Assert.Fail("abstract classes must not be run");
}

public sealed class NotATest
{
    public void Plain() => Assert.Fail("not a test");
}
