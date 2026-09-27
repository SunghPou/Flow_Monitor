using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// <c>MIB_IF_ROW2</c> field order and packing per the Windows SDK.
/// Fixed buffers keep the struct blittable for direct reads from the GetIfTable2 buffer.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MIB_IF_ROW2
{
    public const int IF_MAX_STRING_SIZE = 256;
    public const int IF_MAX_PHYS_ADDRESS_LENGTH = 32;

    // NET_LUID
    public ulong InterfaceLuid;
    // NET_IFINDEX
    public uint InterfaceIndex;
    public Guid InterfaceGuid;
    public fixed char Alias[IF_MAX_STRING_SIZE + 1];
    public fixed char Description[IF_MAX_STRING_SIZE + 1];
    public uint PhysicalAddressLength;
    public fixed byte PhysicalAddress[IF_MAX_PHYS_ADDRESS_LENGTH];
    public fixed byte PermanentPhysicalAddress[IF_MAX_PHYS_ADDRESS_LENGTH];
    public uint Mtu;
    public int Type;                  // IFTYPE
    public int TunnelType;
    public int MediaType;
    public int PhysicalMediumType;
    public int AccessType;            // NET_IF_ACCESS_TYPE
    public int DirectionType;
    public uint InterfaceAndOperStatusFlags;
    public int OperStatus;            // IF_OPER_STATUS
    public int AdminStatus;
    public int MediaConnectState;
    public Guid NetworkGuid;
    public int ConnectionType;
    public ulong TransmitLinkSpeed;   // bits/s
    public ulong ReceiveLinkSpeed;    // bits/s
    public ulong InOctets;
    public ulong InUcastPkts;
    public ulong InNUcastPkts;
    public ulong InDiscards;
    public ulong InErrors;
    public ulong InUnknownProtos;
    public ulong InUcastOctets;
    public ulong InMulticastOctets;
    public ulong InBroadcastOctets;
    public ulong OutOctets;
    public ulong OutUcastPkts;
    public ulong OutNUcastPkts;
    public ulong OutDiscards;
    public ulong OutErrors;
    public ulong OutUcastOctets;
    public ulong OutMulticastOctets;
    public ulong OutBroadcastOctets;
    public ulong OutQLen;

    public const int IfOperStatusUp = 1;
    public const int IfTypeSoftwareLoopback = 24;
    public const int NetIfAccessLoopback = 1;
    public const int NdisMediumLoopback = 17;
    public const int NdisMediumWirelessLan = 1;

    public bool IsUp => OperStatus == IfOperStatusUp;
    public bool IsLoopback =>
        Type == IfTypeSoftwareLoopback || AccessType == NetIfAccessLoopback || MediaType == NdisMediumLoopback;

    public string AliasString { get { fixed (char* p = Alias) return ReadString(p); } }
    public string DescriptionString { get { fixed (char* p = Description) return ReadString(p); } }

    static unsafe string ReadString(char* buf)
    {
        int n = 0;
        while (n < IF_MAX_STRING_SIZE && buf[n] != '\0') n++;
        return new string(buf, 0, n);
    }
}

internal static class IpHlpApi
{
    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetIfEntry2(ref MIB_IF_ROW2 row);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint ConvertInterfaceLuidToAlias(ref ulong luid, [Out] byte[] alias, ushort length);
}
