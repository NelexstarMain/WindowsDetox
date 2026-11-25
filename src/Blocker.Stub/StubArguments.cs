using System.Security.Cryptography;
using System.Text;

namespace Blocker.Stub;

/// <summary>
/// What the stub was started for. Two forms: <c>--blocked &lt;path&gt; --reason &lt;reason&gt; [--block-id &lt;id&gt;]</c> from the service, or
/// <c>--ifeo &lt;original command line…&gt;</c> from an Image File Execution Options debugger entry, where the first argument after the
/// debugger is the path of the executable that was about to start.
/// </summary>
/// <param name="BlockedPath">Path of the blocked executable, or <see langword="null"/> when unknown.</param>
/// <param name="Reason">Reason code or text from the service; empty for IFEO launches.</param>
/// <param name="BlockId">Identifier for <c>stub.getBlockInfo</c>.</param>
/// <param name="IsIfeo">Whether the stub was started directly by an IFEO debugger redirect.</param>
internal sealed record StubArguments(string? BlockedPath, string Reason, string BlockId, bool IsIfeo = false)
{
    /// <summary>Parses the process arguments; never throws. Unknown arguments are ignored.</summary>
    /// <param name="args">Process arguments.</param>
    /// <returns>The parsed arguments.</returns>
    public static StubArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count >= 2 && string.Equals(args[0], "--ifeo", StringComparison.OrdinalIgnoreCase))
        {
            return new StubArguments(args[1], string.Empty, args[1], IsIfeo: true);
        }

        string? path = null;
        string reason = string.Empty;
        string? blockId = null;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--blocked":
                    path = args[++i];
                    break;
                case "--reason":
                    reason = args[++i];
                    break;
                case "--block-id":
                    blockId = args[++i];
                    break;
            }
        }

        return new StubArguments(path, reason, blockId ?? path ?? string.Empty);
    }

    /// <summary>Gets the stable 16-hex-char hash of the normalized blocked executable path.</summary>
    public string ImageHash
    {
        get
        {
            string key = NormalizeImageKey(BlockedPath);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        }
    }

    /// <summary>Gets the name of the per-image mutex that keeps one stub window per blocked executable.</summary>
    public string MutexName => @"Local\WindowsDetox.Stub." + ImageHash;

    private static string NormalizeImageKey(string? path)
    {
        string trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                trimmed = Path.GetFullPath(trimmed).TrimEnd('\\', '/');
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Keep the raw trimmed key when an external command line passes malformed path characters.
        }

        return trimmed.ToUpperInvariant();
    }

    /// <summary>Words a reason code or text for the user.</summary>
    /// <returns>A sentence explaining why the app was blocked.</returns>
    public string DescribeReason() => Reason.ToLowerInvariant() switch
    {
        "" or "not-allowlisted" or "notallowlisted" or "ifeo" => "This app isn't on your allowlist for this session.",
        "bypass" or "bypass-tool" => "This tool could be used to get around the session, so it stays blocked.",
        _ => Reason,
    };
}
