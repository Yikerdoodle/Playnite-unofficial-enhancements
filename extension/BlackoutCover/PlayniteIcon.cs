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
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Black full-screen layer: topmost, click-through, never takes focus. Shown at alpha 0 (invisible) while Playnite's
// Fullscreen window is on screen, and turned to alpha 255 the instant that window goes away (see FlipExitCover).
class ExitCoverForm : Form
{
    public ExitCoverForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        TopMost = true;
        Bounds = Screen.PrimaryScreen.Bounds;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000 | 0x00000020;   // NOACTIVATE | TOOLWINDOW | LAYERED | TRANSPARENT
            return cp;
        }
    }
}

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
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
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
    const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C;
    static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    const uint SWP_FLAGS = 0x0001 | 0x0002 | 0x0010;   // NOSIZE | NOMOVE | NOACTIVATE
    const uint WINEVENT_OUTOFCONTEXT = 0;

    static bool haveIcon, haveTitle, resident, hideSplash;
    static bool exitCover = true;                                   // cover Fullscreen mode's "Exiting Playnite..." screen
    static string coverExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PlayniteCover.exe");
    static DateTime nextExitCover = DateTime.MinValue;
    // exit cover that is ALREADY in place (invisible) while Playnite's Fullscreen window is on screen
    // Fullscreen creates more than one big window while it starts (one of them goes away again), so ALL of them are
    // tracked and an exit is only assumed when the LAST one is gone.
    static HashSet<IntPtr> fsWins = new HashSet<IntPtr>();

    static bool AnyFsShowing()
    {
        foreach (IntPtr w in fsWins)
            if (IsWindow(w) && IsWindowVisible(w) && !IsIconic(w)) return true;
        return false;
    }
    static ExitCoverForm armed;                          // the invisible/black layer
    static bool flipped, helperStarted;                  // armed layer is black / the PlayniteCover "exit" helper was started
    static DateTime flippedAt = DateTime.MinValue, noPlayniteSince = DateTime.MinValue;
    static Dictionary<uint, KeyValuePair<string, DateTime>> pnCache = new Dictionary<uint, KeyValuePair<string, DateTime>>();
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
        // Fullscreen mode: (1) remember the main window, so an invisible cover can sit over it, ready for the exit;
        // (2) react at once to Playnite's own "Exiting Playnite..." screen, which cannot be switched off.
        if (exitCover)
        {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (ProcNameOf(wp) == "playnite.fullscreenapp")
            {
                if (before.StartsWith("Exiting Playnite", StringComparison.Ordinal))
                {
                    FlipExitCover("'Exiting Playnite' screen seen on window " + h);
                    StartExitHelper();
                }
                else if (!before.StartsWith("Opening ", StringComparison.Ordinal) && IsWindowVisible(h))
                {
                    RECT rc2; GetWindowRect(h, out rc2);
                    Rectangle pb = Screen.PrimaryScreen.Bounds;
                    if ((double)(rc2.R - rc2.L) * (rc2.B - rc2.T) >= 0.5 * pb.Width * pb.Height && fsWins.Add(h))
                        Log("tracking Fullscreen window " + h + " (" + fsWins.Count + " tracked)");
                }
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

    static string ProcNameOf(uint pid)
    {
        KeyValuePair<string, DateTime> c;
        if (pnCache.TryGetValue(pid, out c) && (DateTime.Now - c.Value).TotalSeconds < 15) return c.Key;
        string n = "";
        try { n = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { }
        pnCache[pid] = new KeyValuePair<string, DateTime>(n, DateTime.Now);
        return n;
    }

    static int CountPlaynite()
    {
        int n = 0;
        foreach (string name in new string[] { "Playnite.FullscreenApp", "Playnite.DesktopApp" }) n += Process.GetProcessesByName(name).Length;
        return n;
    }

    // ---- the exit cover that is already in place ----
    static void EnsureArmed()
    {
        if (armed != null) return;
        armed = new ExitCoverForm();
        armed.Show();
        SetLayeredWindowAttributes(armed.Handle, 0, 0, 2);                       // alpha 0: invisible, click-through
        SetWindowPos(armed.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_FLAGS);
        Log("exit cover armed (invisible) over the Fullscreen window");
    }

    static void CloseArmed()
    {
        if (armed != null) { armed.Close(); armed = null; }
        flipped = false; helperStarted = false;
    }

    // Playnite's Fullscreen window is going away (or its exit screen showed up): make the layer black NOW.
    static void FlipExitCover(string why)
    {
        if (!exitCover) return;
        if (armed == null) { armed = new ExitCoverForm(); armed.Show(); }
        SetLayeredWindowAttributes(armed.Handle, 0, 255, 2);
        SetWindowPos(armed.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_FLAGS);
        if (!flipped) { flipped = true; flippedAt = DateTime.Now; Log("exit cover BLACK now: " + why); }
    }

    // No Fullscreen window is showing any more. That is an EXIT unless a game is running: while a game runs, the
    // launch helper (also PlayniteCover.exe) is alive for the whole session and Playnite is minimized on purpose.
    // (Playnite hides/minimizes its windows before it starts its own exit screen, so "minimized" counts as gone.)
    static bool falseAlarmLatched;   // a false alarm was corrected: stay quiet until a Fullscreen window is showing again

    static void CheckExitNow(string why)
    {
        if (!exitCover || flipped || falseAlarmLatched || fsWins.Count == 0) return;
        if (AnyFsShowing()) return;
        if (Process.GetProcessesByName("PlayniteCover").Length > 0) return;
        FlipExitCover(why);
    }

    // The long-lived exit helper (PlayniteCover.exe exit): holds the cover until Playnite has really gone.
    static void StartExitHelper()
    {
        if (helperStarted || !File.Exists(coverExe)) return;
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(coverExe, "exit \"x\" 40");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            Process.Start(psi);
            helperStarted = true;
            Log("started the exit helper (PlayniteCover.exe exit)");
        }
        catch (Exception ex) { Log("could not start the exit helper: " + ex.Message); }
    }

    // Called from the timer: keep the invisible cover in place while the Fullscreen window is showing, and release the
    // black one when it is no longer needed (including false alarms, so the screen can never stay black).
    static void ManageArmed()
    {
        if (!exitCover) return;
        DateTime now = DateTime.Now;
        fsWins.RemoveWhere(delegate(IntPtr w) { return !IsWindow(w); });          // forget destroyed windows
        bool mainShowing = AnyFsShowing();

        if (flipped)
        {
            double sec = (now - flippedAt).TotalSeconds;
            int helpers = Process.GetProcessesByName("PlayniteCover").Length;
            int pl = CountPlaynite();
            if (pl == 0) { if (noPlayniteSince == DateTime.MinValue) noPlayniteSince = now; } else noPlayniteSince = DateTime.MinValue;

            if (helpers > 0 && sec >= 1.2) { CloseArmed(); fsWins.Clear(); Log("exit cover: the helper's cover has taken over -> this layer released"); return; }
            if (pl == 0 && (now - noPlayniteSince).TotalSeconds >= 2.0 && helpers == 0) { CloseArmed(); fsWins.Clear(); Log("exit cover: Playnite gone -> released"); return; }
            if (sec >= 45) { CloseArmed(); fsWins.Clear(); Log("exit cover: fail-safe (45 s) -> released"); return; }
            // false alarm: the window is back, or nothing followed within a few seconds while Playnite keeps running
            if (helpers == 0 && pl > 0 && (mainShowing || sec >= (helperStarted ? 6 : 1.5)))
            {
                flipped = false; falseAlarmLatched = true;
                if (armed != null) SetLayeredWindowAttributes(armed.Handle, 0, 0, 2);   // invisible again
                Log("exit cover: false alarm (Playnite is still running) -> invisible again");
            }
            return;
        }

        if (mainShowing) { falseAlarmLatched = false; EnsureArmed(); SetWindowPos(armed.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_FLAGS); }
        else
        {
            CheckExitNow("no Fullscreen window is showing and no game is running");   // fallback if no event was seen
            if (!flipped && armed != null) { CloseArmed(); Log("exit cover disarmed (Fullscreen window not showing, a game is running or none was tracked)"); }
        }
    }

    static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // The earliest signal of an exit: Playnite's Fullscreen main window is hidden or destroyed.
            bool goneEvent = ev == EVENT_OBJECT_DESTROY || ev == EVENT_OBJECT_HIDE || ev == EVENT_SYSTEM_MINIMIZESTART;
            if (exitCover && idObject == 0 && idChild == 0 && goneEvent && fsWins.Contains(hwnd))
            {
                Log("event " + (ev == EVENT_OBJECT_DESTROY ? "DESTROY" : ev == EVENT_OBJECT_HIDE ? "HIDE" : "MINIMIZESTART") + " on Fullscreen window " + hwnd);
                burstUntil = DateTime.Now.AddSeconds(2);                          // a minimize only completes a moment later: re-check fast
                if (timer.Interval != 25) timer.Interval = 25;
                CheckExitNow("Fullscreen window " + (ev == EVENT_OBJECT_DESTROY ? "destroyed" : ev == EVENT_OBJECT_HIDE ? "hidden" : "minimizing"));
                return;
            }
            if (goneEvent) return;
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
                if (DateTime.Now < burstUntil) { ApplyToAll(); ManageArmed(); return; }   // fast burst after a new window
                if (timer.Interval != 1500 && !flipped) timer.Interval = 1500;
                ApplyToAll();                                                      // slow safety net
                ManageArmed();
                if (flipped && timer.Interval != 250) timer.Interval = 250;       // keep a close eye on a black layer
                if (!resident && (DateTime.Now - lastSeen).TotalSeconds > 60 && (DateTime.Now - startedAt).TotalSeconds > 60)
                { Log("exit: Playnite gone for a minute"); Application.ExitThread(); }
            }
            catch (Exception ex) { Log("ERROR in tick: " + ex.Message); }
        };
        timer.Start();

        hookProc = OnWinEvent;
        // window created / shown, and (separately, to avoid the very chatty location/state events in between)
        // title changed - so a title Playnite sets after creating the window is fixed at once too
        IntPtr hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);   // create, destroy, show, hide
        IntPtr hook2 = (haveTitle || exitCover) ? SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT) : IntPtr.Zero;
        Log(hook == IntPtr.Zero ? "WARNING: could not install the window event hook (polling only)" : "window event hooks installed");
        // a window being minimized is a system event of its own (minimize start)
        IntPtr hook3 = exitCover ? SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT) : IntPtr.Zero;
        ApplyToAll();
        Application.Run();
        if (hook3 != IntPtr.Zero) UnhookWinEvent(hook3);
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        if (hook2 != IntPtr.Zero) UnhookWinEvent(hook2);
        GC.KeepAlive(mutex);
        return 0;
    }
}
