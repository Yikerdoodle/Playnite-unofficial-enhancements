// Window icon / title watcher for Playnite.
//
// Usage: PlayniteIcon.exe <path-to-icon.ico | -> [window title] [--resident] [--hide-splash]
//                         [--splash-class=A,B] [--names=A,B] [--match=Text]
//
// Windows takes a RUNNING program's taskbar / Alt-Tab icon AND its taskbar label from the window itself
// (its icon and its title), not from any shortcut, so both have to be set on Playnite's windows. This tiny
// background process does that for every top-level window of Playnite.DesktopApp / Playnite.FullscreenApp:
//   * it carries the given icon (WM_SETICON), if an icon file is given, and
//   * a window titled exactly "Playnite" is renamed to the given title, if a title is given
//     (dialogs and other windows keep their own titles).
//
// To avoid ever showing Playnite's own icon/title, this runs event-driven: a WinEvent hook fires the
// instant any window is created or shown, and a window of Playnite gets its icon/title set right then,
// followed by a short fast-polling burst to win any race with Playnite's own startup code. A slow 1.5 s
// poll remains as a safety net. Run it BEFORE Playnite starts (e.g. at logon with --resident) so even the
// first window is fixed before it appears.
//
//   --hide-splash  also hide Playnite's startup splash window the instant it appears (covers every way
//                  Playnite can start, including the relaunch when switching Desktop <-> Fullscreen mode,
//                  where the --hidesplashscreen command-line switch cannot be passed)
//   --splash-class=A,B  window class name(s) that identify the splash (default SplashScreen); every new
//                  Playnite top-level window's class is logged so this can be checked/adjusted
//   --resident     keep running forever (otherwise it exits once Playnite has been gone for a minute)
//   --names=A,B    process names to watch (default Playnite.DesktopApp,Playnite.FullscreenApp)
//   --match=Text   the title text to replace (default "Playnite")
//
// One instance only. The icon handles belong to this process, so it must stay alive for the icon to stay.
// A log with timestamps is written to %TEMP%\playnite-icon.log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class PlayniteIcon
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

    const uint WM_SETTEXT = 0x000C, WM_SETICON = 0x0080, WM_GETICON = 0x007F;
    const int ICON_SMALL = 0, ICON_BIG = 1;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
    const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_NAMECHANGE = 0x800C;
    const uint WINEVENT_OUTOFCONTEXT = 0;

    static bool haveIcon, haveTitle, resident, hideSplash;
    static bool exitCover = true;                                   // cover Fullscreen mode's "Exiting Playnite..." screen
    static string coverExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PlayniteCover.exe");
    static DateTime nextExitCover = DateTime.MinValue;
    static HashSet<string> splashClasses = new HashSet<string>(new string[] { "splashscreen" });
    static HashSet<IntPtr> seenWindows = new HashSet<IntPtr>();
    static IntPtr big = IntPtr.Zero, small = IntPtr.Zero;
    static string title = "", match = "Playnite";
    static HashSet<string> names = new HashSet<string>(new string[] { "playnite.desktopapp", "playnite.fullscreenapp" });
    static Dictionary<uint, KeyValuePair<bool, DateTime>> pidCache = new Dictionary<uint, KeyValuePair<bool, DateTime>>();
    static DateTime burstUntil = DateTime.MinValue, lastSeen = DateTime.Now, startedAt = DateTime.Now;
    static System.Windows.Forms.Timer timer;
    static string logPath = Path.Combine(Path.GetTempPath(), "playnite-icon.log");
    static int logLines = 0;
    static WinEventProc hookProc;   // kept referenced so the delegate is not collected

    static void Log(string msg)
    {
        if (logLines++ > 400) return;
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n"); } catch { }
    }

    static IntPtr Send(IntPtr h, uint msg, int w, IntPtr l)
    {
        IntPtr res;
        SendMessageTimeout(h, msg, (IntPtr)w, l, SMTO_ABORTIFHUNG, 500, out res);
        return res;
    }

    static bool IsTarget(uint pid)
    {
        KeyValuePair<bool, DateTime> c;
        if (pidCache.TryGetValue(pid, out c) && (DateTime.Now - c.Value).TotalSeconds < 15) return c.Key;
        bool ok = false;
        try { ok = names.Contains(Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant()); } catch { }
        pidCache[pid] = new KeyValuePair<bool, DateTime>(ok, DateTime.Now);
        return ok;
    }

    // set the icon / title on one window if they are not already right; true if anything was changed
    static bool Apply(IntPtr h, string why)
    {
        bool changed = false;
        string before = "";

        // Diagnostics: log each new Playnite top-level window once (class, size, visibility, title) so the
        // splash window's class can be confirmed from the log.
        // The splash (if requested) is hidden right here, before anything else.
        if (hideSplash || !seenWindows.Contains(h))
        {
            StringBuilder cn = new StringBuilder(128);
            GetClassName(h, cn, 128);
            string cls = cn.ToString();
            if (hideSplash && splashClasses.Contains(cls.ToLowerInvariant()) && IsWindowVisible(h))
            {
                ShowWindow(h, 0);   // SW_HIDE
                Log("HID splash window " + h + " (class '" + cls + "', " + why + ")");
                return true;
            }
            if (seenWindows.Add(h))
            {
                RECT rc; GetWindowRect(h, out rc);
                StringBuilder tt = new StringBuilder(256); GetWindowText(h, tt, 256);
                Log("new window " + h + ": class='" + cls + "' size=" + (rc.R - rc.L) + "x" + (rc.B - rc.T) +
                    " visible=" + IsWindowVisible(h) + " title='" + tt + "' (" + why + ")");
            }
        }
        {
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            before = sb.ToString();
        }
        // Fullscreen mode's own "Exiting Playnite..." screen cannot be switched off, so cover it in black until
        // Playnite has really gone (PlayniteCover.exe, "exit" mode). Only for the Fullscreen app's window.
        if (exitCover && before.StartsWith("Exiting Playnite", StringComparison.Ordinal) && DateTime.Now >= nextExitCover)
        {
            uint wp; GetWindowThreadProcessId(h, out wp);
            string pn = "";
            try { pn = Process.GetProcessById((int)wp).ProcessName.ToLowerInvariant(); } catch { }
            if (pn == "playnite.fullscreenapp" && File.Exists(coverExe))
            {
                nextExitCover = DateTime.Now.AddSeconds(5);
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(coverExe, "exit \"x\" 40");
                    psi.UseShellExecute = false; psi.CreateNoWindow = true;
                    Process.Start(psi);
                    Log("'Exiting Playnite' screen seen on window " + h + " -> started the black exit cover");
                }
                catch (Exception ex) { Log("could not start the exit cover: " + ex.Message); }
            }
        }
        if (haveIcon && Send(h, WM_GETICON, ICON_BIG, IntPtr.Zero) != big)
        {
            Send(h, WM_SETICON, ICON_BIG, big);
            Send(h, WM_SETICON, ICON_SMALL, small);
            changed = true;
        }
        if (haveTitle && before == match)
        {
            IntPtr res;
            SendMessageTimeout(h, WM_SETTEXT, IntPtr.Zero, title, SMTO_ABORTIFHUNG, 500, out res);
            changed = true;
        }
        if (changed) Log("set on window " + h + " (" + why + "), title was '" + before + "'");
        return changed;
    }

    static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;   // OBJID_WINDOW only
            if (GetParent(hwnd) != IntPtr.Zero) return;                          // top-level windows only
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (!IsTarget(pid)) return;
            if (ev != EVENT_OBJECT_NAMECHANGE)
            {
                burstUntil = DateTime.Now.AddSeconds(4);
                if (timer.Interval != 25) timer.Interval = 25;
            }
            Apply(hwnd, ev == EVENT_OBJECT_CREATE ? "window created" : ev == EVENT_OBJECT_SHOW ? "window shown" : "title changed");
        }
        catch { }
    }

    static void ApplyToAll()
    {
        List<uint> pids = new List<uint>();
        foreach (string n in names)
            foreach (Process p in Process.GetProcessesByName(n)) pids.Add((uint)p.Id);
        if (pids.Count == 0) return;
        lastSeen = DateTime.Now;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (pids.Contains(wp)) Apply(h, "poll");
            return true;
        }, IntPtr.Zero);
    }

    [STAThread]
    static int Main(string[] a)
    {
        string iconPath = "";
        List<string> pos = new List<string>();
        foreach (string s in a)
        {
            if (s == "--resident") resident = true;
            else if (s == "--hide-splash") hideSplash = true;
            else if (s == "--no-exit-cover") exitCover = false;
            else if (s.StartsWith("--splash-class=")) splashClasses = new HashSet<string>(s.Substring(15).ToLowerInvariant().Split(','));
            else if (s.StartsWith("--names=")) { names = new HashSet<string>(s.Substring(8).ToLowerInvariant().Split(',')); }
            else if (s.StartsWith("--match=")) match = s.Substring(8);
            else pos.Add(s);
        }
        if (pos.Count >= 1) iconPath = pos[0];
        if (pos.Count >= 2) title = pos[1];
        haveIcon = iconPath.Length > 0 && File.Exists(iconPath);
        haveTitle = title.Length > 0;
        // the same opt-out as the extension: { "CoverExit": false } in settings.json next to this program
        try
        {
            string sj = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (File.Exists(sj) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(sj), "\"CoverExit\"\\s*:\\s*false")) exitCover = false;
        }
        catch { }
        if (!haveIcon && !haveTitle && !hideSplash) return 2;

        bool createdNew;
        Mutex mutex = new Mutex(true, "PlayniteIcon-watcher", out createdNew);
        if (!createdNew) return 0;
        try { File.WriteAllText(logPath, ""); } catch { }
        Log("watcher started (resident=" + resident + ", icon=" + haveIcon + ", title='" + title + "', hide-splash=" + hideSplash + ")");

        if (haveIcon)
        {
            big = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 64, 64, LR_LOADFROMFILE);
            small = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            if (big == IntPtr.Zero || small == IntPtr.Zero) { haveIcon = false; if (!haveTitle) return 3; }
        }

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 1500;
        timer.Tick += delegate
        {
            try
            {
                if (DateTime.Now < burstUntil) { ApplyToAll(); return; }          // fast burst after a new window
                if (timer.Interval != 1500) timer.Interval = 1500;
                ApplyToAll();                                                      // slow safety net
                if (!resident && (DateTime.Now - lastSeen).TotalSeconds > 60 && (DateTime.Now - startedAt).TotalSeconds > 60)
                { Log("exit: Playnite gone for a minute"); Application.ExitThread(); }
            }
            catch (Exception ex) { Log("ERROR in tick: " + ex.Message); }
        };
        timer.Start();

        hookProc = OnWinEvent;
        // window created / shown, and (separately, to avoid the very chatty location/state events in between)
        // title changed - so a title Playnite sets after creating the window is fixed at once too
        IntPtr hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        IntPtr hook2 = (haveTitle || exitCover) ? SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT) : IntPtr.Zero;
        Log(hook == IntPtr.Zero ? "WARNING: could not install the window event hook (polling only)" : "window event hooks installed");
        ApplyToAll();
        Application.Run();
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        if (hook2 != IntPtr.Zero) UnhookWinEvent(hook2);
        GC.KeepAlive(mutex);
        return 0;
    }
}
