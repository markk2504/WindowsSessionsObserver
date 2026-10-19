using System.Runtime.InteropServices;

namespace SessionObserver.Service.Interop;

/// <summary>P/Invoke declarations for the Remote Desktop Services (WTS) API in wtsapi32.dll.</summary>
internal static partial class WtsApi
{
    public static readonly IntPtr CurrentServerHandle = IntPtr.Zero;

    public const int AfInet = 2;
    public const int AfInet6 = 23;

    public const ushort ProtocolConsole = 0;
    public const ushort ProtocolRdp = 2;

    public const int SessionStateLock = 0;
    public const int SessionStateUnlock = 1;

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSEnumerateSessions(
        IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQuerySessionInformation(
        IntPtr server, int sessionId, WtsInfoClass infoClass, out IntPtr buffer, out int bytesReturned);

    [LibraryImport("wtsapi32.dll")]
    public static partial void WTSFreeMemory(IntPtr memory);

    public enum WtsInfoClass
    {
        UserName = 5,
        WinStationName = 6,
        DomainName = 7,
        ConnectState = 8,
        ClientName = 10,
        ClientAddress = 14,
        ClientProtocolType = 16,
        SessionInfoEx = 25,
    }

    /// <summary>WTS_SESSION_INFOW</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    /// <summary>WTSINFOEXW (level 1 only).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WtsInfoEx
    {
        public int Level;
        public WtsInfoExLevel1 Data;
    }

    /// <summary>WTSINFOEX_LEVEL1_W. Times are FILETIME (UTC); zero means "not set".</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WtsInfoExLevel1
    {
        public const int WinStationNameLength = 33; // WINSTATIONNAME_LENGTH + 1
        public const int UserNameLength = 21;       // USERNAME_LENGTH + 1
        public const int DomainNameLength = 18;     // DOMAIN_LENGTH + 1

        public int SessionId;
        public int SessionState;
        public int SessionFlags;
        public fixed char WinStationName[WinStationNameLength];
        public fixed char UserName[UserNameLength];
        public fixed char DomainName[DomainNameLength];
        public long LogonTime;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long CurrentTime;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
    }

    /// <summary>WTS_CLIENT_ADDRESS</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct WtsClientAddress
    {
        public int AddressFamily;
        public fixed byte Address[20];
    }
}
