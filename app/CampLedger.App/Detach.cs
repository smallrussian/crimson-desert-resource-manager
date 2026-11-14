using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CampLedger.Reader;

namespace CampLedger.App;

/// <summary>
/// Moves the app out of the game's process tree. The launcher ASI starts <c>CampLedger.exe watch</c> as a child of
/// CrimsonDesert.exe, and Steam counts the game's child processes (and processes carrying its Steam* environment)
/// as the game still running. With the window open the app outlives the game, so Steam kept showing it as running.
///
/// <see cref="TryRelaunch"/> starts a fresh copy with Explorer as its parent and without the Steam* variables, and the
/// launcher-started copy exits at once. Nothing here touches the game process.
/// </summary>
static class Detach
{
    public const string Flag = "--detached";

    /// <summary>True when the detached copy started; the caller should then exit. False: keep running here (logged).</summary>
    public static bool TryRelaunch(string[] args)
    {
        IntPtr parent = IntPtr.Zero, attrs = IntPtr.Zero;
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) throw new InvalidOperationException("no shell window (Explorer not running)");
            GetWindowThreadProcessId(shell, out uint explorerPid);
            parent = OpenProcess(PROCESS_CREATE_PROCESS, false, explorerPid);
            if (parent == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess(explorer)");

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrs = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrs, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            IntPtr parentSlot = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(parentSlot, parent);
                if (!UpdateProcThreadAttribute(attrs, 0, PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, parentSlot, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

                var si = new STARTUPINFOEX { StartupInfo = { cb = Marshal.SizeOf<STARTUPINFOEX>() }, lpAttributeList = attrs };
                string cmd = $"\"{exe}\" " + string.Join(' ', args.Select(Quote).Append(Flag));
                if (!CreateProcessW(exe, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, EnvironmentWithoutSteam(),
                        AppContext.BaseDirectory, ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                ReaderHost.Log($"started detached from the game (pid {pi.dwProcessId}, parent Explorer {explorerPid})");
                return true;
            }
            finally
            {
                DeleteProcThreadAttributeList(attrs);
                Marshal.FreeHGlobal(parentSlot);
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            ReaderHost.Log("could not detach from the game, running as its child (Steam may show the game as running while the app is open): " + e.Message);
            return false;
        }
        finally
        {
            if (attrs != IntPtr.Zero) Marshal.FreeHGlobal(attrs);
            if (parent != IntPtr.Zero) CloseHandle(parent);
        }
    }

    /// <summary>Steam's per-game variables (SteamAppId, SteamGameId, SteamOverlayGameId, ...) tie a process to the game.</summary>
    public static bool IsSteamVariable(string name) => name.StartsWith("Steam", StringComparison.OrdinalIgnoreCase);

    static string EnvironmentWithoutSteam()
    {
        var sb = new StringBuilder();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string name = (string)e.Key;
            if (IsSteamVariable(name)) continue;
            sb.Append(name).Append('=').Append((string?)e.Value).Append('\0');
        }
        return sb.Append('\0').ToString();
    }

    static string Quote(string a) => a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    const uint PROCESS_CREATE_PROCESS = 0x0080;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x00020000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        string env, string cwd, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);
}
