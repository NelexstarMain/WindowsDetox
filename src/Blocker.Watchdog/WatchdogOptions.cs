using System.Diagnostics.CodeAnalysis;

namespace Blocker.Watchdog;

/// <summary>
/// Command line of the watchdog: <c>[--console] [--partner &lt;service&gt;] [--data &lt;dir&gt;]</c>. <c>--console</c> runs interactively
/// instead of as a Windows service.
/// </summary>
/// <param name="Console">Whether to run in the console instead of the service control manager.</param>
/// <param name="PartnerServiceName">Service to keep alive.</param>
/// <param name="DataDirectory">Directory holding the sealed status file and the HMAC key (ProgramData, protected by its ACL).</param>
internal sealed record WatchdogOptions(bool Console, string PartnerServiceName, string DataDirectory)
{
    public const string DefaultServiceName = "WindowsDetoxWatchdog";
    public const string DefaultPartnerName = "WindowsDetoxService";

    public string ServiceName => DefaultServiceName;

    public string StatusPath => Path.Combine(DataDirectory, "supervision.status");

    public string KeyPath => Path.Combine(DataDirectory, "hmac.key");

    public static bool TryParse(IReadOnlyList<string> args, [NotNullWhen(true)] out WatchdogOptions? options, [NotNullWhen(false)] out string? error)
    {
        bool console = false;
        string partner = DefaultPartnerName;
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsDetox");
        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--console":
                    console = true;
                    break;
                case "--partner" or "--data" when i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]):
                    if (args[i] == "--partner")
                    {
                        partner = args[++i];
                    }
                    else
                    {
                        data = args[++i];
                    }

                    break;
                default:
                    options = null;
                    error = $"Unknown or incomplete argument '{args[i]}'.";
                    return false;
            }
        }

        options = new WatchdogOptions(console, partner, data);
        error = null;
        return true;
    }
}
