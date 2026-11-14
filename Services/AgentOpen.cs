using System;
using System.Linq;

namespace LumenLite;

/// <summary>
/// Opens an agent session where it lives, the same way from the Island and the
/// panel: a session on this machine in the Claude or Codex app; one on another
/// machine on that machine's RustDesk screen, else over Remote Control, else
/// RustDesk itself.
/// </summary>
internal static class AgentOpen
{
    /// <summary>What a click does, for the row's tooltip.</summary>
    public static string Describe(AgentSession session)
    {
        var app = AgentMonitor.Name(session.Provider);
        if (session.Machine.Length == 0)
            return L.F($"Mở trong app {app}");
        if (Peer(session) is { } peer)
            return L.F($"Mở màn hình máy {session.Machine} trong RustDesk ({(peer.Host.Length > 0 ? peer.Host : peer.Id)}), bấm lần nữa để thu nhỏ");
        return session.OpenUrl.Length > 0
            ? L.F($"Mở qua Remote Control (phiên chạy trên máy {session.Machine})")
            : L.F($"Chưa biết máy {session.Machine} trong RustDesk: mở RustDesk");
    }

    /// <summary>Said when another machine's session has no RustDesk machine and no Remote Control to open it.</summary>
    public static event Action<string>? Unknown;

    /// <summary>Opens the session; false, with the reason logged, when that failed.</summary>
    public static bool Open(AgentSession session)
    {
        try
        {
            if (session.Machine.Length > 0 && Peer(session) is { } peer)
                RustDesk.Toggle(peer.Id);
            else if (session.OpenUrl.Length > 0)
                CommandBar.Launch(session.OpenUrl);
            else
            {
                // Better a word than RustDesk's home page: nothing here says which machine it is.
                Unknown?.Invoke(L.F($"Chưa biết máy {session.Machine} trong RustDesk"));
                RustDesk.OpenApp();
            }
            return true;
        }
        catch (Exception ex)
        {
            Store.Log($"open {AgentMonitor.Name(session.Provider)} session failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The saved RustDesk machine a session runs on: by the computer name the session-fleet plugin records
    /// (RustDesk shows it lower-cased, with ".local" on a Mac), else by the fleet's machine name in the RustDesk name.
    /// </summary>
    public static RemotePeer? Peer(AgentSession session)
    {
        if (session.Machine.Length == 0 || !RustDesk.Available)
            return null;
        static string Bare(string name) => name.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? name[..^6] : name;
        var peers = RustDesk.List();
        if (Store.S.RustDeskMachines.TryGetValue(session.Machine, out var id) && peers.FirstOrDefault(p => p.Id == id) is { } chosen)
            return chosen;
        return (session.Host.Length > 0 ? peers.FirstOrDefault(p => string.Equals(Bare(p.Host), Bare(session.Host), StringComparison.OrdinalIgnoreCase)) : null)
            ?? peers.FirstOrDefault(p => p.Host.Contains(session.Machine, StringComparison.OrdinalIgnoreCase));
    }
}
