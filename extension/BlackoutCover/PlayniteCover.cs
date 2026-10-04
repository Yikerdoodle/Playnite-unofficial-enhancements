// Launch/exit helper for Playnite Fullscreen mode.
//
// Usage: PlayniteCover.exe start <stateFile> <maxWaitSeconds>
//   (the old "stop" mode is a no-op; exit is handled by this same process)
//
// One process for the whole game session:
//  1 WAIT     Pin Playnite's fullscreen window on top (hides Steam's "Starting game"
//             window and the desktop) until the game's window is up.
//  2 SWAP     Game window to the top, Playnite directly behind it, game focused.
//  3 WATCH    Playnite is minimized (invisible: it is behind the game), and a black
//             cover is kept in step with the game:
//               - game fills >=95% of the screen: the cover is a fully transparent,
//                 click-through, topmost "armed" layer, ready for the exit;
//               - game is windowed / smaller: an opaque black BACKDROP sits directly
//                 behind the game window, covering the rest of the screen, so it
//                 looks fullscreen. It disappears once the game really is fullscreen
//                 and comes back if the game goes windowed again.
//  4 RESTORE  When the game's window disappears (or Playnite reports it stopped), the
//             cover turns solid black at once and Playnite is restored. The moment
//             Playnite is back it is pinned #1 (topmost) with the black cover directly
//             BEHIND it, so Playnite paints itself normally and nothing flashes. Both stay
//             until Steam's windows are gone, then the cover is removed (invisible, behind
//             Playnite) and Playnite becomes a normal window again.
// If Playnite's window can't be found in phase 1, a plain black cover is used.
// Every phase has a fail-safe so nothing can stay on screen.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

class CoverForm : Form
{
    readonly bool clickThrough;
    public CoverForm(bool topmost, bool clickThrough)
    {
        this.clickThrough = clickThrough;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        TopMost = topmost;
        Bounds = Screen.PrimaryScreen.Bounds;
    }
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000;   // NOACTIVATE | TOOLWINDOW | LAYERED
            if (clickThrough) cp.ExStyle |= 0x00000020;            // TRANSPARENT (clicks pass through)
            return cp;
        }
    }
}

static class Program
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    static readonly IntPtr HWND_TOP = IntPtr.Zero;
    static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
    const uint FLAGS = 0x0001 | 0x0002 | 0x0010;   // NOSIZE | NOMOVE | NOACTIVATE
    const int SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;

    static readonly string[] Ignore = {
        "steam", "steamwebhelper", "steamservice", "playnite.fullscreenapp", "playnite.desktopapp",
        "playnitecover", "playniteicon", "explorer", "applicationframehost", "textinputhost", "searchhost",
        "startmenuexperiencehost", "shellexperiencehost", "dwm", "sunshine", "conhost"
    };

    static double Area(IntPtr h)
    {
        RECT r; if (!GetWindowRect(h, out r)) return 0;
        return (double)Math.Max(0, r.R - r.L) * Math.Max(0, r.B - r.T);
    }

    static IntPtr FindPlaynite()
    {
        System.Collections.Generic.List<uint> pids = new System.Collections.Generic.List<uint>();
        foreach (Process p in Process.GetProcessesByName("Playnite.FullscreenApp")) pids.Add((uint)p.Id);
        if (pids.Count == 0) return IntPtr.Zero;
        IntPtr best = IntPtr.Zero; double bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (!pids.Contains(wp)) return true;
            double a = Area(h);
            if (a > bestArea) { bestArea = a; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    static IntPtr FindGameWindow(int pid, Rectangle screen, DateTime since, uint ownPid)
    {
        double screenArea = (double)screen.Width * screen.Height;
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp == ownPid) return true;
            double frac = Area(h) / screenArea;
            if (frac <= 0) return true;
            if (pid > 0 && wp == (uint)pid && frac >= 0.30) { found = h; return false; }
            if (frac >= 0.50)
            {
                try
                {
                    Process p = Process.GetProcessById((int)wp);
                    string n = p.ProcessName.ToLowerInvariant();
                    if (Array.IndexOf(Ignore, n) < 0 && p.StartTime > since) { found = h; return false; }
                }
                catch { }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // Largest visible, non-minimized window (>=30% of the screen) of the given process.
    static IntPtr BigWindowOf(uint pid, Rectangle screen)
    {
        double screenArea = (double)screen.Width * screen.Height;
        IntPtr best = IntPtr.Zero; double bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp != pid) return true;
            double a = Area(h);
            if (a / screenArea >= 0.30 && a > bestArea) { bestArea = a; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    // ---- diagnostics: %TEMP%\playnite-cover.log (reset on each start) ----
    static string logPath = Path.Combine(Path.GetTempPath(), "playnite-cover.log");
    static void Log(string msg)
    {
        try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n"); } catch { }
    }

    // ---- the cover layer ----
    static CoverForm form;
    static byte coverAlpha;
    static void ArmCover(byte alpha, bool topmost, bool clickThrough)
    {
        coverAlpha = alpha;
        if (form == null)
        {
            form = new CoverForm(topmost, clickThrough);
            form.Show();
            // make it visible/painted right now, not on a later message-loop pass
            SetLayeredWindowAttributes(form.Handle, 0, alpha, 2);
            form.Refresh();
            Log("cover created alpha=" + alpha + " topmost=" + topmost + " clickThrough=" + clickThrough);
        }
        else if (form.IsHandleCreated) SetLayeredWindowAttributes(form.Handle, 0, alpha, 2);
    }
    static void CloseCover()
    {
        if (form != null) { form.Close(); form = null; }
    }
    static void Quit(string why)
    {
        Log("exit: " + why);
        Application.ExitThread();
    }

    // A window that needs the USER (e.g. Steam's controller warning, a cloud-save conflict question). These
    // are titled "Steam Dialog" (process steam / steamwebhelper). Steam's own "Launching..." progress window
    // is deliberately NOT in this list: that one is hidden, these must never be.
    static readonly string[] AttentionTitles = { "Steam Dialog" };

    static IntPtr FindAttentionWindow(Rectangle screen)
    {
        double screenArea = (double)screen.Width * screen.Height;
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (Area(h) / screenArea < 0.05) return true;
            StringBuilder t = new StringBuilder(128); GetWindowText(h, t, 128);
            if (Array.IndexOf(AttentionTitles, t.ToString()) < 0) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            try
            {
                string n = Process.GetProcessById((int)wp).ProcessName.ToLowerInvariant();
                if (n == "steam" || n == "steamwebhelper") { found = h; return false; }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // diagnostics: the visible Steam windows (title, size), at most every 2 s, so the real titles can be checked in the log
    static DateTime nextSteamLog = DateTime.MinValue;
    static void LogSteamWindows(DateTime now)
    {
        if (now < nextSteamLog) return;
        nextSteamLog = now.AddSeconds(2);
        System.Collections.Generic.List<string> parts = new System.Collections.Generic.List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            try
            {
                string n = Process.GetProcessById((int)wp).ProcessName.ToLowerInvariant();
                if (n != "steam" && n != "steamwebhelper") return true;
            }
            catch { return true; }
            StringBuilder t = new StringBuilder(128); GetWindowText(h, t, 128);
            RECT r; GetWindowRect(h, out r);
            parts.Add("'" + t + "' " + (r.R - r.L) + "x" + (r.B - r.T));
            return true;
        }, IntPtr.Zero);
        if (parts.Count > 0) Log("steam windows: " + string.Join("; ", parts.ToArray()));
    }

    // A big Steam window (e.g. the "Synchronizing cloud" screen shown after a game quits).
    static bool SteamWindowUp(Rectangle screen)
    {
        double screenArea = (double)screen.Width * screen.Height;
        bool found = false;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            if (Area(h) / screenArea < 0.25) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            try
            {
                string n = Process.GetProcessById((int)wp).ProcessName.ToLowerInvariant();
                if (n == "steam" || n == "steamwebhelper") { found = true; return false; }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---- "exit" mode: black cover while Playnite shuts down ----
    // Playnite shows its own "Exiting Playnite..." screen while it closes, and it cannot be switched off. This
    // covers the whole primary screen in black (above that screen, click-through) from the moment it is asked to
    // until Playnite has really gone: no Playnite process left, plus a short grace period. If a NEW Playnite
    // starts during that grace period (Playnite relaunches itself when you switch Desktop <-> Fullscreen mode)
    // the cover stays until the new window is on screen, so mode switches are smooth too. Fail-safe: maxSeconds.
    static string[] PlayniteProcs = { "Playnite.FullscreenApp", "Playnite.DesktopApp" };

    static int CountPlaynite()
    {
        int n = 0;
        foreach (string name in PlayniteProcs) n += Process.GetProcessesByName(name).Length;
        return n;
    }

    // a big, visible window of Playnite that is not one of its small progress dialogs
    static bool BigPlayniteWindow(Rectangle screen)
    {
        System.Collections.Generic.List<uint> pids = new System.Collections.Generic.List<uint>();
        foreach (string name in PlayniteProcs) foreach (Process p in Process.GetProcessesByName(name)) pids.Add((uint)p.Id);
        if (pids.Count == 0) return false;
        double screenArea = (double)screen.Width * screen.Height;
        bool found = false;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (!pids.Contains(wp) || Area(h) / screenArea < 0.5) return true;
            StringBuilder t = new StringBuilder(128); GetWindowText(h, t, 128);
            string ts = t.ToString();
            if (ts.StartsWith("Exiting Playnite") || ts.StartsWith("Opening ")) return true;
            found = true; return false;
        }, IntPtr.Zero);
        return found;
    }

    static int RunExitCover(double max)
    {
        bool createdNew;
        Mutex mx = new Mutex(true, "PlayniteCover-exit", out createdNew);
        if (!createdNew) return 0;
        Application.EnableVisualStyles();
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        DateTime start = DateTime.Now, goneSince = DateTime.MinValue, bigSince = DateTime.MinValue;
        bool relaunched = false;
        Log("exit cover: started (Playnite processes: " + CountPlaynite() + ")");
        ArmCover(255, true, true);

        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 100;
        t.Tick += delegate
        {
            try
            {
                DateTime now = DateTime.Now;
                if (form != null) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                if ((now - start).TotalSeconds >= max) { CloseCover(); Quit("exit cover fail-safe (" + max + " s)"); return; }

                int n = CountPlaynite();
                if (n > 0 && goneSince == DateTime.MinValue) return;                  // Playnite still shutting down
                if (n == 0)
                {
                    if (goneSince == DateTime.MinValue) { goneSince = now; Log("exit cover: Playnite has exited"); }
                    // grace period: a relaunch (mode switch) would start a new process very soon
                    if (!relaunched && (now - goneSince).TotalSeconds >= 1.5) { CloseCover(); Quit("Playnite gone, cover removed"); }
                    return;
                }
                // a new Playnite appeared after the old one exited: keep covering until its window is up
                if (!relaunched) { relaunched = true; Log("exit cover: Playnite is starting again (mode switch) -> holding the cover until its window is up"); }
                if (BigPlayniteWindow(bounds))
                {
                    if (bigSince == DateTime.MinValue) bigSince = now;
                    else if ((now - bigSince).TotalSeconds >= 0.6) { CloseCover(); Quit("new Playnite window is up, cover removed"); }
                }
                else bigSince = DateTime.MinValue;
            }
            catch (Exception ex) { Log("ERROR in exit cover: " + ex.Message); CloseCover(); Quit("error"); }
        };
        t.Start();
        Application.Run();
        GC.KeepAlive(mx);
        return 0;
    }

    enum Phase { Wait, MinPending, Watch, Restore }
    // cover kinds while watching: 0 none, 1 backdrop behind a windowed game, 2 armed (transparent, topmost), 3 exit cover
    static int coverKind = 0;

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length < 3) return 2;
        string mode = a[0], stateFile = a[1];
        double max;
        if (!double.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out max)) max = 30;

        // "exit": black cover while Playnite shuts down (see RunExitCover). Independent of the launch helper.
        if (mode == "exit") return RunExitCover(max);

        // "stop" is a no-op: the running "start" helper detects the exit itself.
        if (mode != "start") return 0;

        // A stale helper from an earlier game must never block this one.
        uint myId = (uint)Process.GetCurrentProcess().Id;
        foreach (Process p in Process.GetProcessesByName("PlayniteCover"))
            if ((uint)p.Id != myId) { try { p.Kill(); p.WaitForExit(2000); } catch { } }

        bool createdNew;
        Mutex mutex = new Mutex(true, "PlayniteCover-start", out createdNew);
        if (!createdNew) return 0;

        try { File.WriteAllText(logPath, ""); } catch { }
        Log("helper started");
        Application.EnableVisualStyles();
        DateTime start = DateTime.Now;
        DateTime minimizePinAt = DateTime.MinValue;   // delayed minimize of Playnite behind a windowed game's backdrop
        bool pinHiddenLogged = false;
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        double screenArea = (double)bounds.Width * bounds.Height;
        int pid = 0;
        IntPtr pin = IntPtr.Zero, game = IntPtr.Zero;
        uint gamePid = 0;
        DateTime okSince = DateTime.MinValue, minAt = DateTime.MinValue, goneSince = DateTime.MinValue, phaseStart = DateTime.Now, wantSince = DateTime.MinValue;
        int wantKind = -1;
        Phase phase = Phase.Wait;

        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 120;
        bool backLogged = false;
        bool tempOpaque = false;                                   // cover turned opaque because the game window vanished
        DateTime steamClearSince = DateTime.MinValue;
        bool attentionActive = false;                              // a Steam dialog that needs the user is on screen
        DateTime nextRaise = DateTime.MinValue, prevNow = DateTime.Now;
        t.Tick += delegate
        {
          try
          {
            DateTime now = DateTime.Now;
            double dt = (now - prevNow).TotalSeconds; prevNow = now;
            string s = "";
            try { s = File.ReadAllText(stateFile); } catch { }
            bool stopSignal = s.Contains("stop");

            // ---------------- 1 WAIT (+ 2 SWAP) ----------------
            if (phase == Phase.Wait)
            {
                double el = (now - start).TotalSeconds;
                Match m = Regex.Match(s, @"pid=(\d+)");
                if (m.Success) int.TryParse(m.Groups[1].Value, out pid);
                LogSteamWindows(now);

                // A Steam dialog that needs the user (controller warning, save-conflict question...) must
                // never be hidden: unpin Playnite, drop any cover, put the dialog on top, and pause the
                // fail-safe clock while it is up. Normal behaviour resumes once it is gone.
                IntPtr att = stopSignal ? IntPtr.Zero : FindAttentionWindow(bounds);
                if (att != IntPtr.Zero && el < max)
                {
                    if (!attentionActive) { attentionActive = true; Log("Steam dialog on screen -> letting it through (Playnite unpinned, cover removed)"); }
                    if (pin != IntPtr.Zero && IsWindow(pin)) SetWindowPos(pin, HWND_NOTOPMOST, 0, 0, 0, 0, FLAGS);
                    CloseCover();
                    SetWindowPos(att, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                    if (now >= nextRaise) { SetForegroundWindow(att); nextRaise = now.AddSeconds(5); }   // not every tick: never fight the user
                    start = start.AddSeconds(dt);
                    return;
                }
                if (attentionActive) { attentionActive = false; Log("Steam dialog gone -> back to normal"); }

                if (pin == IntPtr.Zero) { pin = FindPlaynite(); if (pin != IntPtr.Zero) Log("found Playnite window"); }
                if (pin != IntPtr.Zero && !IsWindow(pin))
                {
                    // Playnite itself was closed while we were waiting: nothing to protect, never leave a black cover up
                    CloseCover(); Quit("Playnite window closed while waiting for the game"); return;
                }
                bool pinOk = pin != IntPtr.Zero && IsWindow(pin) && IsWindowVisible(pin) && !IsIconic(pin);
                if (pinOk) SetWindowPos(pin, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                else if (pin != IntPtr.Zero)
                {
                    // Playnite hid or minimized itself: never let the desktop show - black cover at once
                    if (!pinHiddenLogged) { Log("Playnite window hidden/minimized during launch -> black cover"); pinHiddenLogged = true; }
                    ArmCover(255, true, true);
                }
                else if (el > 1.5) ArmCover(255, true, true);   // Playnite window not found: plain black fallback
                if (form != null) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);

                bool done = stopSignal || el >= max;
                IntPtr g = IntPtr.Zero;
                if (!done && el > 0.8)
                {
                    g = FindGameWindow(pid, bounds, start, myId);
                    if (g != IntPtr.Zero)
                    {
                        if (okSince == DateTime.MinValue) okSince = now;
                        else if ((now - okSince).TotalSeconds >= 1.2) done = true;   // let it paint a frame
                    }
                    else okSince = DateTime.MinValue;
                }
                if (!done) return;

                if (pin != IntPtr.Zero && g != IntPtr.Zero)
                {
                    // swap in one step: game on top, Playnite right behind it (not topmost), game focused
                    SetWindowPos(g, HWND_TOP, 0, 0, 0, 0, FLAGS);
                    SetWindowPos(pin, g, 0, 0, 0, 0, FLAGS);
                    SetForegroundWindow(g);
                    CloseCover();
                    game = g;
                    GetWindowThreadProcessId(game, out gamePid);
                    Log("swap: game window " + game + " covers " + Math.Round(100 * Area(game) / screenArea) + "% of screen, pid " + gamePid);
                    if (Area(game) / screenArea >= 0.95)
                    {
                        phase = Phase.MinPending; minAt = now.AddSeconds(0.8); phaseStart = now;
                    }
                    else
                    {
                        // windowed game: black backdrop directly behind it. Playnite is minimized only
                        // LATER (after the backdrop has certainly painted), never in the same instant.
                        ArmCover(255, false, false);
                        SetWindowPos(form.Handle, game, 0, 0, 0, 0, FLAGS);
                        coverKind = 1;
                        minimizePinAt = now.AddSeconds(0.7);
                        phase = Phase.Watch; phaseStart = now;
                    }
                    t.Interval = 60;
                    return;
                }
                // nothing to hand over to (timeout, launch failed, or no game window)
                if (pin != IntPtr.Zero) SetWindowPos(pin, HWND_NOTOPMOST, 0, 0, 0, 0, FLAGS);
                CloseCover();
                Quit("nothing to hand over to (stop signal: " + stopSignal + ", waited " + Math.Round(el) + " s, game window found: " + (g != IntPtr.Zero) + ", Playnite window found: " + (pin != IntPtr.Zero) + ")");
                return;
            }

            // ---------------- 3a fullscreen game: minimize Playnite after a short wait ----------------
            if (phase == Phase.MinPending)
            {
                if (stopSignal || !IsWindow(game) || !IsWindow(pin)) { Quit("early exit before minimize (stop signal: " + stopSignal + ", game window valid: " + IsWindow(game) + ")"); return; }
                if (now < minAt) return;
                bool stillFull = IsWindowVisible(game) && !IsIconic(game) && Area(game) / screenArea >= 0.95;
                if (!stillFull)
                {
                    // changed its mind (windowed after all): let the Watch phase sort out the backdrop
                    ShowWindow(pin, SW_SHOWMINNOACTIVE);
                    phase = Phase.Watch; phaseStart = now; coverKind = 0;
                    return;
                }
                ShowWindow(pin, SW_SHOWMINNOACTIVE);       // invisible: it is behind the fullscreen game
                ArmCover(0, true, true);                    // transparent, ready for the exit
                coverKind = 2;
                Log("fullscreen game confirmed -> Playnite minimized, exit cover armed");
                phase = Phase.Watch; phaseStart = now;
                return;
            }

            // ---------------- 3 WATCH ----------------
            if (phase == Phase.Watch)
            {
                if (!IsWindow(pin)) { CloseCover(); Quit("Playnite window closed"); return; }   // Playnite itself closed

                if (minimizePinAt != DateTime.MinValue && now >= minimizePinAt && coverKind == 1 && form != null)
                {
                    ShowWindow(pin, SW_SHOWMINNOACTIVE);   // behind the painted backdrop: invisible
                    minimizePinAt = DateTime.MinValue;
                    Log("Playnite minimized (windowed-game backdrop is up)");
                }

                bool gone = stopSignal;
                if (!gone)
                {
                    try { gone = Process.GetProcessById((int)gamePid).HasExited; } catch { gone = true; }
                }
                IntPtr bw = IntPtr.Zero;
                if (!gone)
                {
                    bw = BigWindowOf(gamePid, bounds);
                    if (bw != IntPtr.Zero)
                    {
                        game = bw; goneSince = DateTime.MinValue;
                        if (tempOpaque)
                        {
                            // it was only a window rebuild (e.g. a resolution change): back to normal
                            tempOpaque = false;
                            Log("game window is back -> cover reverted");
                            if (form != null)
                            {
                                if (coverKind == 2) SetLayeredWindowAttributes(form.Handle, 0, 0, 2);
                                else if (coverKind == 1) { SetWindowPos(form.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, FLAGS); SetWindowPos(form.Handle, game, 0, 0, 0, 0, FLAGS); }
                            }
                        }
                    }
                    else
                    {
                        // The game window vanished. It may be the exit, so cover the screen with black
                        // IMMEDIATELY (never wait to be sure); revert only if the window comes back.
                        if (goneSince == DateTime.MinValue) { goneSince = now; Log("game window vanished -> cover opaque at once"); }
                        if (!tempOpaque && form != null)
                        {
                            tempOpaque = true;
                            SetLayeredWindowAttributes(form.Handle, 0, 255, 2);
                            SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                        }
                        if ((now - goneSince).TotalSeconds >= 0.8) gone = true;
                    }
                }

                if (gone)
                {
                    // cover everything at once, bring Playnite back underneath
                    if (coverKind == 1 && form != null)
                    {
                        form.TopMost = true;
                        SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                    }
                    else ArmCover(255, true, true);
                    coverKind = 3;
                    Log("game gone (stop signal: " + stopSignal + ") -> exit cover, restoring Playnite");
                    ShowWindow(pin, SW_RESTORE);
                    phase = Phase.Restore; phaseStart = now; okSince = DateTime.MinValue;
                    return;
                }

                // keep the cover in step with the game: fullscreen -> armed layer, windowed -> backdrop
                if (bw == IntPtr.Zero) return;   // window vanished: the cover is already opaque (above); decide next tick
                int want = (Area(game) / screenArea >= 0.95 ? 2 : 1);
                if (want == coverKind) wantKind = -1;
                else if (wantKind != want) { wantKind = want; wantSince = now; }
                else if ((now - wantSince).TotalSeconds >= 0.4)
                {
                    CloseCover();
                    coverKind = want; wantKind = -1;
                    Log("cover kind -> " + want + " (0 none, 1 backdrop behind windowed game, 2 armed/transparent)");
                    if (want == 1) ArmCover(255, false, false);
                    else if (want == 2) ArmCover(0, true, true);
                }
                if (form != null)
                {
                    if (coverKind == 2) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                    else if (coverKind == 1) SetWindowPos(form.Handle, game, 0, 0, 0, 0, FLAGS);   // directly behind the game
                }
                return;
            }

            // ---------------- 4 RESTORE ----------------
            if (phase == Phase.Restore)
            {
                if (!IsWindow(pin)) { CloseCover(); Quit("Playnite window closed during restore"); return; }

                // Same rule as while waiting for the game: a Steam dialog that needs the user (e.g. a
                // cloud-save question after the game quits) is never hidden under Playnite or the cover.
                IntPtr att2 = FindAttentionWindow(bounds);
                if (att2 != IntPtr.Zero)
                {
                    if (!attentionActive) { attentionActive = true; Log("Steam dialog on screen during restore -> letting it through"); }
                    SetWindowPos(pin, HWND_NOTOPMOST, 0, 0, 0, 0, FLAGS);
                    CloseCover();
                    SetWindowPos(att2, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                    if (now >= nextRaise) { SetForegroundWindow(att2); nextRaise = now.AddSeconds(5); }
                    phaseStart = phaseStart.AddSeconds(dt);   // do not let the dialog use up the 15 s fail-safe
                    okSince = DateTime.MinValue; steamClearSince = DateTime.MinValue;
                    return;
                }
                if (attentionActive) { attentionActive = false; Log("Steam dialog gone during restore -> back to normal"); }

                if (IsIconic(pin)) ShowWindow(pin, SW_RESTORE);
                bool back = !IsIconic(pin) && IsWindowVisible(pin) && Area(pin) / screenArea >= 0.90;

                // Z-order: as soon as Playnite is back it is #1 (topmost), with the black cover directly
                // BEHIND it. Playnite can then paint itself normally (a fully covered window may not repaint),
                // and the cover only acts as a safety net behind it. Until Playnite is back, the cover is #1.
                if (back)
                {
                    SetWindowPos(pin, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);
                    if (form != null) SetWindowPos(form.Handle, pin, 0, 0, 0, 0, FLAGS);   // just below Playnite
                    if (!backLogged) { Log("Playnite restored -> pinned #1, cover directly behind it"); backLogged = true; }
                }
                else if (form != null) SetWindowPos(form.Handle, HWND_TOPMOST, 0, 0, 0, 0, FLAGS);

                // Steam shows a full-screen "Synchronizing cloud" window after a game quits (and Steam is
                // being shut down by the script). Playnite stays pinned above it until that window is gone,
                // and for at least 2.5 s in case it has not appeared yet.
                if (SteamWindowUp(bounds)) steamClearSince = DateTime.MinValue;
                else if (steamClearSince == DateTime.MinValue) steamClearSince = now;
                bool steamClear = steamClearSince != DateTime.MinValue && (now - steamClearSince).TotalSeconds >= 1.0;
                bool minHold = (now - phaseStart).TotalSeconds >= 2.5;

                if (back)
                {
                    if (okSince == DateTime.MinValue) okSince = now;
                    else if ((now - okSince).TotalSeconds >= 0.7 && steamClear && minHold)
                    {
                        SetForegroundWindow(pin);
                        CloseCover();                                           // it is behind Playnite: invisible change
                        SetWindowPos(pin, HWND_NOTOPMOST, 0, 0, 0, 0, FLAGS);   // back to a normal window
                        Quit("Playnite back on screen, Steam window clear, cover removed, helper done");
                        return;
                    }
                }
                else okSince = DateTime.MinValue;
                if ((now - phaseStart).TotalSeconds >= 15) { CloseCover(); Quit("restore fail-safe (15 s) triggered"); }   // fail-safe
            }
          }
          catch (Exception ex)
          {
              Log("ERROR in tick: " + ex);
          }
        };
        t.Start();
        Application.Run();
        GC.KeepAlive(mutex);
        return 0;
    }
}
