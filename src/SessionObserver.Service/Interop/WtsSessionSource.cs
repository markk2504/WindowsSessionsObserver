using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using log4net;
using SessionObserver.Protocol.Models;

namespace SessionObserver.Service.Interop;

/// <summary>Reads sessions of the local machine through wtsapi32.dll.</summary>
internal sealed class WtsSessionSource : ISessionSource
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(WtsSessionSource));

    public IReadOnlyList<SessionInfo> EnumerateSessions()
    {
        if (!WtsApi.WTSEnumerateSessions(WtsApi.CurrentServerHandle, 0, 1, out IntPtr buffer, out int count))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "WTSEnumerateSessions failed.");
        }

        var ids = new List<int>(count);
        try
        {
            int size = Marshal.SizeOf<WtsApi.WtsSessionInfo>();
            for (int i = 0; i < count; i++)
            {
                var entry = Marshal.PtrToStructure<WtsApi.WtsSessionInfo>(buffer + (i * size));
                ids.Add(entry.SessionId);
            }
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }

        var sessions = new List<SessionInfo>(ids.Count);
        foreach (int id in ids)
        {
            // A session can end between enumeration and query; it is then simply skipped.
            if (QuerySession(id) is { } info)
            {
                sessions.Add(info);
            }
        }
        return sessions;
    }

    public SessionInfo? QuerySession(int sessionId)
    {
        if (!TryQueryInfoEx(sessionId, out WtsApi.WtsInfoExLevel1 ex))
        {
            return null;
        }

        string? winStation;
        unsafe
        {
            winStation = FromFixed(ex.WinStationName, WtsApi.WtsInfoExLevel1.WinStationNameLength);
        }

        ushort protocol = QueryUInt16(sessionId, WtsApi.WtsInfoClass.ClientProtocolType) ?? WtsApi.ProtocolConsole;

        return new SessionInfo
        {
            SessionId = sessionId,
            // Queried separately: the fixed-size fields in WTSINFOEX truncate long names.
            UserName = NullIfEmpty(QueryString(sessionId, WtsApi.WtsInfoClass.UserName)),
            Domain = NullIfEmpty(QueryString(sessionId, WtsApi.WtsInfoClass.DomainName)),
            State = Enum.IsDefined(typeof(SessionState), ex.SessionState) ? (SessionState)ex.SessionState : SessionState.Down,
            WinStationName = NullIfEmpty(winStation),
            ClientName = NullIfEmpty(QueryString(sessionId, WtsApi.WtsInfoClass.ClientName)),
            ClientAddress = QueryClientAddress(sessionId),
            IsRemote = protocol == WtsApi.ProtocolRdp,
            IsLocked = ex.SessionFlags switch
            {
                WtsApi.SessionStateLock => true,
                WtsApi.SessionStateUnlock => false,
                _ => null,
            },
            LogonTime = FromFileTime(ex.LogonTime),
            ConnectTime = FromFileTime(ex.ConnectTime),
            DisconnectTime = FromFileTime(ex.DisconnectTime),
            LastInputTime = FromFileTime(ex.LastInputTime),
        };
    }

    private static unsafe bool TryQueryInfoEx(int sessionId, out WtsApi.WtsInfoExLevel1 result)
    {
        result = default;
        if (!WtsApi.WTSQuerySessionInformation(
                WtsApi.CurrentServerHandle, sessionId, WtsApi.WtsInfoClass.SessionInfoEx, out IntPtr buffer, out int bytes))
        {
            int error = Marshal.GetLastPInvokeError();
            Log.Debug($"WTSQuerySessionInformation(SessionInfoEx) for session {sessionId} failed: " +
                      $"{new Win32Exception(error).Message} ({error}).");
            return false;
        }

        try
        {
            if (bytes < sizeof(WtsApi.WtsInfoEx))
            {
                Log.Warn($"WTSINFOEX for session {sessionId} is {bytes} bytes; expected at least {sizeof(WtsApi.WtsInfoEx)}.");
                return false;
            }
            WtsApi.WtsInfoEx info = *(WtsApi.WtsInfoEx*)buffer;
            if (info.Level != 1)
            {
                Log.Warn($"WTSINFOEX for session {sessionId} has unexpected level {info.Level}.");
                return false;
            }
            result = info.Data;
            return true;
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }

    private static string? QueryString(int sessionId, WtsApi.WtsInfoClass infoClass)
    {
        if (!WtsApi.WTSQuerySessionInformation(WtsApi.CurrentServerHandle, sessionId, infoClass, out IntPtr buffer, out _))
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }

    private static ushort? QueryUInt16(int sessionId, WtsApi.WtsInfoClass infoClass)
    {
        if (!WtsApi.WTSQuerySessionInformation(WtsApi.CurrentServerHandle, sessionId, infoClass, out IntPtr buffer, out int bytes))
        {
            return null;
        }
        try
        {
            return bytes >= sizeof(ushort) ? (ushort)Marshal.ReadInt16(buffer) : null;
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }

    private static unsafe string? QueryClientAddress(int sessionId)
    {
        if (!WtsApi.WTSQuerySessionInformation(
                WtsApi.CurrentServerHandle, sessionId, WtsApi.WtsInfoClass.ClientAddress, out IntPtr buffer, out int bytes))
        {
            return null;
        }
        try
        {
            if (bytes < sizeof(WtsApi.WtsClientAddress))
            {
                return null;
            }
            WtsApi.WtsClientAddress address = *(WtsApi.WtsClientAddress*)buffer;
            byte* raw = address.Address;

            // Both families store the address starting at offset 2 of the Address array.
            return address.AddressFamily switch
            {
                WtsApi.AfInet => new IPAddress(new ReadOnlySpan<byte>(raw + 2, 4)).ToString(),
                WtsApi.AfInet6 => new IPAddress(new ReadOnlySpan<byte>(raw + 2, 16)).ToString(),
                _ => null,
            };
        }
        finally
        {
            WtsApi.WTSFreeMemory(buffer);
        }
    }

    private static unsafe string FromFixed(char* chars, int maxLength)
    {
        int length = 0;
        while (length < maxLength && chars[length] != '\0')
        {
            length++;
        }
        return new string(chars, 0, length);
    }

    private static DateTimeOffset? FromFileTime(long fileTime) =>
        fileTime <= 0 ? null : new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime));

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
