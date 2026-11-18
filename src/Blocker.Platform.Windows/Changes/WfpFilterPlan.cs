namespace Blocker.Platform.Windows.Changes;

/// <summary>ALE connect layer a filter attaches to.</summary>
internal enum WfpLayer
{
    /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V4.</summary>
    ConnectV4,

    /// <summary>FWPM_LAYER_ALE_AUTH_CONNECT_V6.</summary>
    ConnectV6,
}

/// <summary>Filter action.</summary>
internal enum WfpAction
{
    /// <summary>FWP_ACTION_PERMIT.</summary>
    Permit,

    /// <summary>FWP_ACTION_BLOCK.</summary>
    Block,
}

/// <summary>A match condition (beyond the application id every filter carries). Conditions on the same field are OR-ed by WFP.</summary>
internal abstract record WfpCondition;

/// <summary>IP protocol number (6 TCP, 17 UDP).</summary>
/// <param name="Protocol">Protocol number.</param>
internal sealed record WfpProtocolCondition(byte Protocol) : WfpCondition;

/// <summary>Remote port.</summary>
/// <param name="Port">Port number.</param>
internal sealed record WfpRemotePortCondition(ushort Port) : WfpCondition;

/// <summary>Remote address within a subnet.</summary>
/// <param name="Address">Network-order address bytes (4 or 16).</param>
/// <param name="PrefixLength">Prefix length in bits.</param>
internal sealed record WfpRemoteSubnetCondition(byte[] Address, int PrefixLength) : WfpCondition;

/// <summary>One filter to add to the Blocker sublayer.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Layer">Layer.</param>
/// <param name="Action">Action.</param>
/// <param name="Weight">Filter weight range 0..15 (FWP_UINT8); higher is evaluated first.</param>
/// <param name="AppPath">Full path of the browser executable the filter applies to.</param>
/// <param name="Conditions">Extra conditions (AND-ed across fields).</param>
internal sealed record WfpFilterSpec(string Name, WfpLayer Layer, WfpAction Action, byte Weight, string AppPath, IReadOnlyList<WfpCondition> Conditions);

/// <summary>
/// Describes the browser firewall without touching WFP: for each browser exe and for IPv4/IPv6, a loopback PERMIT (higher
/// weight, so the local PAC proxy stays reachable) and BLOCKs for TCP 80/443, UDP 443/853 (QUIC, DoQ) and TCP 853 (DoT).
/// </summary>
/// <param name="Filters">All filters, in creation order.</param>
internal sealed record WfpFilterPlan(IReadOnlyList<WfpFilterSpec> Filters)
{
    /// <summary>Weight of the loopback permit filters.</summary>
    internal const byte PermitWeight = 15;

    /// <summary>Weight of the block filters.</summary>
    internal const byte BlockWeight = 8;

    internal const byte Tcp = 6;
    internal const byte Udp = 17;

    private static readonly byte[] LoopbackV4 = [127, 0, 0, 0];
    private static readonly byte[] LoopbackV6 = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];

    /// <summary>Builds the plan for the given browser executables (duplicates by case-insensitive path are collapsed).</summary>
    /// <exception cref="ArgumentException">An executable is not a normalized drive-absolute <c>.exe</c> path.</exception>
    public static WfpFilterPlan Create(IEnumerable<string> executables)
    {
        List<WfpFilterSpec> filters = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string exe in executables)
        {
            string path = WindowsPath.Normalize(exe, nameof(executables));
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{exe}' is not an .exe.", nameof(executables));
            }

            if (!seen.Add(path))
            {
                continue;
            }

            string file = WindowsPath.GetFileName(path);
            foreach (WfpLayer layer in new[] { WfpLayer.ConnectV4, WfpLayer.ConnectV6 })
            {
                string family = layer == WfpLayer.ConnectV4 ? "IPv4" : "IPv6";
                WfpCondition loopback = layer == WfpLayer.ConnectV4
                    ? new WfpRemoteSubnetCondition(LoopbackV4, 8)
                    : new WfpRemoteSubnetCondition(LoopbackV6, 128);

                filters.Add(new($"WindowsDetox permit loopback {file} {family}", layer, WfpAction.Permit, PermitWeight, path, [loopback]));
                filters.Add(new($"WindowsDetox block TCP 80/443 {file} {family}", layer, WfpAction.Block, BlockWeight, path,
                    [new WfpProtocolCondition(Tcp), new WfpRemotePortCondition(80), new WfpRemotePortCondition(443)]));
                filters.Add(new($"WindowsDetox block UDP 443/853 {file} {family}", layer, WfpAction.Block, BlockWeight, path,
                    [new WfpProtocolCondition(Udp), new WfpRemotePortCondition(443), new WfpRemotePortCondition(853)]));
                filters.Add(new($"WindowsDetox block TCP 853 {file} {family}", layer, WfpAction.Block, BlockWeight, path,
                    [new WfpProtocolCondition(Tcp), new WfpRemotePortCondition(853)]));
            }
        }

        return new WfpFilterPlan(filters);
    }
}
