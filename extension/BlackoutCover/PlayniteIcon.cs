// Window-icon watcher for Playnite.
//
// Usage: PlayniteIcon.exe <path-to-icon.ico>
//
// Windows takes a RUNNING program's taskbar / Alt-Tab icon from the window itself, not from any
// shortcut, so a custom icon has to be set on Playnite's windows while it runs. This tiny background
// process does that: every 1.5 s it makes sure each top-level window of Playnite.DesktopApp /
// Playnite.FullscreenApp carries the given icon (WM_SETICON), and exits once Playnite has been gone for
// a minute. One instance only. The icon handles belong to this process, so it must stay alive for the
// icon to stay - which is why it keeps running while Playnite does.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

static class PlayniteIcon
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint flags);

    const uint WM_SETICON = 0x0080, WM_GETICON = 0x007F;
    const int ICON_SMALL = 0, ICON_BIG = 1;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;

    static IntPtr Send(IntPtr h, uint msg, int w, IntPtr l)
    {
        IntPtr res;
        SendMessageTimeout(h, msg, (IntPtr)w, l, SMTO_ABORTIFHUNG, 500, out res);
        return res;
    }

    [STAThread]
    static int Main(string[] a)
    {
        if (a.Length < 1 || !File.Exists(a[0])) return 2;
        bool createdNew;
        Mutex mutex = new Mutex(true, "PlayniteIcon-watcher", out createdNew);
        if (!createdNew) return 0;

        IntPtr big = LoadImage(IntPtr.Zero, a[0], IMAGE_ICON, 64, 64, LR_LOADFROMFILE);
        IntPtr small = LoadImage(IntPtr.Zero, a[0], IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
        if (big == IntPtr.Zero || small == IntPtr.Zero) return 3;

        DateTime lastSeen = DateTime.Now;
        while (true)
        {
            List<uint> pids = new List<uint>();
            foreach (string n in new string[] { "Playnite.DesktopApp", "Playnite.FullscreenApp" })
                foreach (Process p in Process.GetProcessesByName(n)) pids.Add((uint)p.Id);

            if (pids.Count == 0)
            {
                if ((DateTime.Now - lastSeen).TotalSeconds > 60) break;
            }
            else
            {
                lastSeen = DateTime.Now;
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint wp; GetWindowThreadProcessId(h, out wp);
                    if (!pids.Contains(wp)) return true;
                    if (Send(h, WM_GETICON, ICON_BIG, IntPtr.Zero) != big)
                    {
                        Send(h, WM_SETICON, ICON_BIG, big);
                        Send(h, WM_SETICON, ICON_SMALL, small);
                    }
                    return true;
                }, IntPtr.Zero);
            }
            Thread.Sleep(1500);
        }
        GC.KeepAlive(mutex);
        return 0;
    }
}
