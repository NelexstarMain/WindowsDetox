namespace Blocker.SmokeTest;

/// <summary>Prints one line per step (<c>PASS</c>, <c>FAIL name: detail</c>, <c>SKIP</c>) and counts the failures that decide the exit code.</summary>
internal sealed class StepReport
{
    private int _failures;

    public int Failures => _failures;

    public void Pass(string name, string? detail = null) =>
        Console.WriteLine(detail is null ? $"PASS {name}" : $"PASS {name}: {detail}");

    public void Fail(string name, string detail)
    {
        _failures++;
        Console.WriteLine($"FAIL {name}: {detail}");
    }

    public void Skip(string name, string reason) => Console.WriteLine($"SKIP {name}: {reason}");

    public void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            Pass(name, detail);
        }
        else
        {
            Fail(name, detail);
        }
    }

    /// <summary>Free-form evidence belonging to the step above (SDDL, lists); indented so it never looks like a result line.</summary>
    public void Info(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            Console.WriteLine($"     {line.TrimEnd('\r')}");
        }
    }

    /// <summary>Runs a step; an exception becomes a FAIL line with the full exception text. Once the global cap is hit nothing is caught, so the run stops.</summary>
    public async Task RunAsync(string name, Func<Task> step, CancellationToken cap)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (!cap.IsCancellationRequested)
        {
            Fail(name, ex.ToString());
        }
    }
}
