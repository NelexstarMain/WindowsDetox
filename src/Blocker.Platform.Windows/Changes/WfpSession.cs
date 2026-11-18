using System.Buffers.Binary;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace Blocker.Platform.Windows.Changes;

/// <summary>
/// A WFP dynamic session holding the filters of one <see cref="WfpFilterPlan"/>. Everything is added in one transaction to
/// a private high-weight sublayer; disposing closes the engine handle, which removes all filters and the sublayer.
/// </summary>
internal sealed unsafe class WfpSession : IDisposable
{
    private const uint RpcCAuthnDefault = 0xFFFFFFFF;
    private const int MaxConditions = 6;

    private readonly FwpmEngineClose0SafeHandle _engine;

    private WfpSession(FwpmEngineClose0SafeHandle engine) => _engine = engine;

    /// <summary>Opens a dynamic session and installs the plan; filters for executables that do not exist are skipped.</summary>
    public static IDisposable Install(WfpFilterPlan plan)
    {
        FwpmEngineClose0SafeHandle engine = OpenDynamicEngine();
        try
        {
            Populate(engine, plan);
        }
        catch
        {
            engine.Dispose();
            throw;
        }

        return new WfpSession(engine);
    }

    /// <inheritdoc />
    public void Dispose() => _engine.Dispose();

    private static FwpmEngineClose0SafeHandle OpenDynamicEngine()
    {
        fixed (char* name = "WindowsDetox browser firewall")
        {
            FWPM_SESSION0 session = default;
            session.flags = PInvoke.FWPM_SESSION_FLAG_DYNAMIC;
            session.displayData.name = new PWSTR(name);
            uint error = PInvoke.FwpmEngineOpen0(RpcCAuthnDefault, null, session, out FwpmEngineClose0SafeHandle engine);
            if (error != 0 || engine.IsInvalid)
            {
                engine.Dispose();
                throw ChangeGuard.Error("FwpmEngineOpen0", error);
            }

            return engine;
        }
    }

    private static void Populate(FwpmEngineClose0SafeHandle engine, WfpFilterPlan plan)
    {
        // Scratch memory reused for every filter (stackalloc outside any loop).
        FWPM_FILTER_CONDITION0* conditions = stackalloc FWPM_FILTER_CONDITION0[MaxConditions];
        FWP_V4_ADDR_AND_MASK* v4 = stackalloc FWP_V4_ADDR_AND_MASK[1];
        FWP_V6_ADDR_AND_MASK* v6 = stackalloc FWP_V6_ADDR_AND_MASK[1];
        Dictionary<string, nint> appIds = new(StringComparer.OrdinalIgnoreCase);

        uint error = PInvoke.FwpmTransactionBegin0(engine, 0);
        if (error != 0)
        {
            throw ChangeGuard.Error("FwpmTransactionBegin0", error);
        }

        bool committed = false;
        try
        {
            Guid subLayer = Guid.NewGuid();
            fixed (char* subLayerName = "WindowsDetox browser sublayer")
            {
                FWPM_SUBLAYER0 sublayer = default;
                sublayer.subLayerKey = subLayer;
                sublayer.displayData.name = new PWSTR(subLayerName);
                sublayer.weight = 0xFFFF;
                error = PInvoke.FwpmSubLayerAdd0(engine, sublayer, default);
                if (error != 0)
                {
                    throw ChangeGuard.Error("FwpmSubLayerAdd0", error);
                }
            }

            foreach (WfpFilterSpec spec in plan.Filters)
            {
                if (!appIds.TryGetValue(spec.AppPath, out nint appId))
                {
                    appId = File.Exists(spec.AppPath) ? GetAppId(spec.AppPath) : 0;
                    appIds[spec.AppPath] = appId;
                }

                if (appId != 0)
                {
                    AddFilter(engine, subLayer, spec, (FWP_BYTE_BLOB*)appId, conditions, v4, v6);
                }
            }

            error = PInvoke.FwpmTransactionCommit0(engine);
            if (error != 0)
            {
                throw ChangeGuard.Error("FwpmTransactionCommit0", error);
            }

            committed = true;
        }
        finally
        {
            if (!committed)
            {
                _ = PInvoke.FwpmTransactionAbort0(engine);
            }

            foreach (nint appId in appIds.Values)
            {
                if (appId != 0)
                {
                    void* blob = (void*)appId;
                    PInvoke.FwpmFreeMemory0(ref blob);
                }
            }
        }
    }

    private static nint GetAppId(string path)
    {
        uint error = PInvoke.FwpmGetAppIdFromFileName0(path, out FWP_BYTE_BLOB* appId);
        return error == 0 ? (nint)appId : throw ChangeGuard.Error("FwpmGetAppIdFromFileName0", error);
    }

    private static void AddFilter(
        FwpmEngineClose0SafeHandle engine,
        Guid subLayer,
        WfpFilterSpec spec,
        FWP_BYTE_BLOB* appId,
        FWPM_FILTER_CONDITION0* conditions,
        FWP_V4_ADDR_AND_MASK* v4,
        FWP_V6_ADDR_AND_MASK* v6)
    {
        if (spec.Conditions.Count + 1 > MaxConditions)
        {
            throw new InvalidOperationException($"Filter '{spec.Name}' has too many conditions.");
        }

        int count = 0;
        FWPM_FILTER_CONDITION0 appCondition = default;
        appCondition.fieldKey = PInvoke.FWPM_CONDITION_ALE_APP_ID;
        appCondition.matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL;
        appCondition.conditionValue.type = FWP_DATA_TYPE.FWP_BYTE_BLOB_TYPE;
        appCondition.conditionValue.Anonymous.byteBlob = appId;
        conditions[count++] = appCondition;

        foreach (WfpCondition condition in spec.Conditions)
        {
            FWPM_FILTER_CONDITION0 translated = default;
            translated.matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL;
            switch (condition)
            {
                case WfpProtocolCondition protocol:
                    translated.fieldKey = PInvoke.FWPM_CONDITION_IP_PROTOCOL;
                    translated.conditionValue.type = FWP_DATA_TYPE.FWP_UINT8;
                    translated.conditionValue.Anonymous.uint8 = protocol.Protocol;
                    break;
                case WfpRemotePortCondition port:
                    translated.fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_PORT;
                    translated.conditionValue.type = FWP_DATA_TYPE.FWP_UINT16;
                    translated.conditionValue.Anonymous.uint16 = port.Port;
                    break;
                case WfpRemoteSubnetCondition { Address.Length: 4 } subnet:
                    v4->addr = BinaryPrimitives.ReadUInt32BigEndian(subnet.Address); // WFP wants host byte order
                    v4->mask = subnet.PrefixLength == 0 ? 0 : uint.MaxValue << (32 - subnet.PrefixLength);
                    translated.fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS;
                    translated.conditionValue.type = FWP_DATA_TYPE.FWP_V4_ADDR_MASK;
                    translated.conditionValue.Anonymous.v4AddrMask = v4;
                    break;
                case WfpRemoteSubnetCondition { Address.Length: 16 } subnet:
                    byte* address = (byte*)&v6->addr;
                    subnet.Address.CopyTo(new Span<byte>(address, 16));
                    v6->prefixLength = (byte)subnet.PrefixLength;
                    translated.fieldKey = PInvoke.FWPM_CONDITION_IP_REMOTE_ADDRESS;
                    translated.conditionValue.type = FWP_DATA_TYPE.FWP_V6_ADDR_MASK;
                    translated.conditionValue.Anonymous.v6AddrMask = v6;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported WFP condition in '{spec.Name}'.");
            }

            conditions[count++] = translated;
        }

        fixed (char* name = spec.Name)
        {
            FWPM_FILTER0 filter = default;
            filter.displayData.name = new PWSTR(name);
            filter.layerKey = spec.Layer == WfpLayer.ConnectV4 ? PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V4 : PInvoke.FWPM_LAYER_ALE_AUTH_CONNECT_V6;
            filter.subLayerKey = subLayer;
            filter.weight.type = FWP_DATA_TYPE.FWP_UINT8;
            filter.weight.Anonymous.uint8 = spec.Weight;
            filter.numFilterConditions = (uint)count;
            filter.filterCondition = conditions;
            filter.action.type = spec.Action == WfpAction.Permit ? FWP_ACTION_TYPE.FWP_ACTION_PERMIT : FWP_ACTION_TYPE.FWP_ACTION_BLOCK;

            uint error = PInvoke.FwpmFilterAdd0(engine, filter, default);
            if (error != 0)
            {
                throw ChangeGuard.Error($"FwpmFilterAdd0 ({spec.Name})", error);
            }
        }
    }
}
