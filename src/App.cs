using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// 正在录入快捷键的窗口 (设置窗口 / 模式编辑框)
interface IKeyCapture
{
    bool IsCapturing { get; }
    void OnCaptured(int mods, int vk);
}

class App : ApplicationContext
{
    public const string Version = "1.1.0";
    public const string Repo = "czczccc/DisplayKit";
    public static string RepoUrl { get { return "https://github.com/" + Repo; } }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "DisplayKit";
    const string LegacyRunValue = "OledGuard"; // 改名前的自启项
    static readonly string ExePath = Application.ExecutablePath; // 自动更新会改名正在运行的 exe, 先记下原路径
    const int GraceMs = 800; // 黑屏后这段时间内忽略输入(比如松开快捷键)

    readonly Settings cfg = Settings.Load();
    readonly TrayIcon tray = new TrayIcon();
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Control invoker = new Control();
    readonly List<BlankForm> overlays = new List<BlankForm>();
    readonly AudioMeter audio = new AudioMeter();
    readonly GamepadWatcher gamepad = new GamepadWatcher();
    readonly Native.HookProc mouseProc, keyProc; // 保持引用, 防止被 GC
    readonly int selfPid = Process.GetCurrentProcess().Id;
    IntPtr mouseHook, keyHook;
    Thread hookThread;
    uint hookThreadId;
    Icon iconActive, iconPaused;
    bool taskbarLight;
    int lastTrayTry = Environment.TickCount;

    // 防烧屏状态. 键鼠钩子跑在单独的线程上 (见 StartHooks), 两边都访问的字段标为 volatile;
    // 钩子线程只读快照 hooks / blankRects, 不碰 cfg 里的列表
    volatile int lastActivity = Environment.TickCount; // 最后一次"有效"操作
    volatile int lastRawInput = Environment.TickCount; // 最后一次任何输入(含抖动)
    volatile int anchorX, anchorY;
    volatile bool overlayVisible, dismissQueued;
    volatile int overlayShownAt;
    volatile HookConfig hooks;
    volatile Rectangle[] blankRects; // 只黑部分显示器时被黑掉的区域, null = 全部
    int monitorOffAt;
    bool monitorOffSent;
    DateTime pausedUntil = DateTime.MinValue;
    readonly HashSet<int> swallowMouseUp = new HashSet<int>();
    readonly HashSet<int> swallowKeyUp = new HashSet<int>();
    string guardStatus = "", keepAwakeReason, lastTip;
    IntPtr fgHwnd; string fgProcess, fgScreen;
    bool locked, lockOffSent;
    int lockedAt;

    // 显示模式状态
    DisplayState display;
    int profileIndex = -1;
    bool switching, confirming;
    int lastAutoCheck = Environment.TickCount;
    DisplayProfile autoProfile, autoRestore; // 自动切换: 当前生效的模式, 程序退出后要恢复的模式
    string autoProcess;

    // 检查更新
    DateTime nextUpdateCheck = DateTime.Now.AddSeconds(15);
    string latestVersion, latestUrl, updateStatus;
    bool updateChecking, updating, autoInstallPending;
    string autoInstallFailed; // 自动安装失败过的版本, 不再自动重试
    Icon formIcon;

    SettingsForm settings;
    volatile IKeyCapture capture;
    int pendingModOnly; // 录入快捷键时: 只按了修饰键还没按别的键

    static int Since(int tick) { return unchecked(Environment.TickCount - tick); }

    public Settings Cfg { get { return cfg; } }
    public bool TrayOk { get { return tray.Added; } }
    public string GuardStatus { get { return guardStatus; } }
    public DisplayState Display { get { return display; } }
    public int ProfileIndex { get { return profileIndex; } }
    public Icon AppIcon { get { return formIcon ?? iconActive; } }
    public string LatestVersion { get { return latestVersion; } }
    public string UpdateStatus { get { return updateStatus; } }
    public bool UpdateChecking { get { return updateChecking; } }
    public bool Updating { get { return updating; } }
    public bool Paused { get { return DateTime.Now < pausedUntil; } }
    public DateTime PausedUntil { get { return pausedUntil; } }
    public IKeyCapture Capture { get { return capture; } set { capture = value; pendingModOnly = 0; } }

    public App(bool autostart, bool updated, EventWaitHandle showSignal)
    {
        var h = invoker.Handle; // 强制创建句柄, 供 BeginInvoke 使用
        var p = Cursor.Position; anchorX = p.X; anchorY = p.Y;
        Log.Write("启动 v" + Version + (autostart ? " (开机自启)" : "") + (updated ? " (自动更新后)" : ""));

        Theme.Load();
        ToolStripManager.Renderer = new ModernRenderer();
        MakeIcons();
        try { formIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        tray.Icon = iconActive;
        tray.Menu = Ui.Menu();
        tray.Menu.Opening += (s, e) => { FillTrayMenu(); e.Cancel = false; };
        tray.LeftClick += OpenSettings;
        tray.BalloonClick += tag => { if (tag == "update") InstallUpdate(); };
        tray.TryAdd();

        // 再次运行 exe 时, 新进程会发信号让这里打开设置窗口
        ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { invoker.BeginInvoke((Action)OpenSettings); }, null, -1, false);

        mouseProc = MouseHook;
        keyProc = KeyHook;
        RebuildHooks();
        StartHooks();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        MigrateAutoStart();
        if (IsAutoStart()) SetAutoStart(true); // exe 换过位置时更新自启路径
        RefreshDisplay();

        // 旧版 OledGuard 还在运行的话, 两边的键盘钩子会抢同一组快捷键
        Mutex legacy;
        if (Mutex.TryOpenExisting(@"Local\OledGuard_SingleInstance", out legacy))
        {
            legacy.Dispose();
            Log.Write("检测到旧版 OledGuard 正在运行");
            tray.ShowBalloon("旧版 OledGuard 还在运行", "DisplayKit 已包含它的全部功能，请右键 OledGuard 的托盘图标退出它", TrayIcon.InfoWarning);
        }

        timer.Interval = 500;
        timer.Tick += delegate { Tick(); };
        timer.Start();
        Tick();

        if (updated)
        {
            tray.ShowBalloon("DisplayKit 已更新到 v" + Version, "设置和开机自启都保持不变", TrayIcon.InfoInfo);
            DeleteOldExe();
        }
        if (!autostart) OpenSettings();
    }

    // ---------- 输入钩子 ----------

    // 钩子线程用的设置快照: 设置窗口改 cfg 时钩子线程可能正在遍历, 所以每次保存后整体换一份
    class HookConfig
    {
        public int WakeMode, WakeVk, WakeMods, MoveThreshold;
        public int[] Vks, Mods;
        public Action[] Actions;
    }

    void RebuildHooks()
    {
        var vks = new List<int>(); var mods = new List<int>(); var actions = new List<Action>();
        vks.Add(cfg.BlankVk); mods.Add(cfg.BlankMods); actions.Add(ShowOverlay);
        if (cfg.Profiles.Count > 0) { vks.Add(cfg.ToggleVk); mods.Add(cfg.ToggleMods); actions.Add(ToggleProfile); }
        foreach (var p in cfg.Profiles)
        {
            var profile = p;
            vks.Add(p.HotkeyVk); mods.Add(p.HotkeyMods); actions.Add(delegate { SwitchProfile(profile, null); });
        }
        hooks = new HookConfig
        {
            WakeMode = cfg.WakeMode, WakeVk = cfg.WakeVk, WakeMods = cfg.WakeMods, MoveThreshold = cfg.MoveThreshold,
            Vks = vks.ToArray(), Mods = mods.ToArray(), Actions = actions.ToArray(),
        };
    }

    // 低级钩子放在单独的高优先级线程上: 回调超时 (UI 线程在切换分辨率等操作时会卡一两秒) 的钩子会被系统悄悄移除
    void StartHooks()
    {
        using (var ready = new ManualResetEvent(false))
        {
            hookThread = new Thread(delegate ()
            {
                hookThreadId = Native.GetCurrentThreadId();
                Native.MSG msg;
                Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0); // 建立消息队列, 之后才能收 WM_QUIT
                IntPtr mod = Native.GetModuleHandle(null);
                mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
                keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
                if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero) Log.Write("安装键鼠钩子失败, 错误码 " + Marshal.GetLastWin32Error());
                ready.Set();
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
                if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
                if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            });
            hookThread.Name = "InputHooks";
            hookThread.IsBackground = true;
            hookThread.Priority = ThreadPriority.Highest;
            hookThread.Start();
            ready.WaitOne();
        }
    }

    void StopHooks()
    {
        if (hookThread == null) return;
        Native.PostThreadMessage(hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        hookThread.Join(1000);
        hookThread = null;
    }

    bool WakeByMouse { get { return hooks.WakeMode == Settings.WakeAny; } }

    // 只黑部分显示器时, 落在没黑的屏幕上的输入照常放行
    bool OnBlankedScreen(int x, int y)
    {
        var rects = blankRects;
        if (rects == null) return true;
        foreach (var r in rects)
            if (r.Contains(x, y)) return true;
        return false;
    }

    bool ForegroundOnBlankedScreen()
    {
        if (blankRects == null) return true;
        Native.RECT r;
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out r)) return true;
        return OnBlankedScreen((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    // 钩子线程上的异常没人接, 会直接让进程退出
    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && HandleMouse(wParam.ToInt32(), lParam)) return new IntPtr(1);
        }
        catch (Exception ex) { Log.Write("鼠标钩子异常: " + ex); }
        return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && HandleKey(wParam.ToInt32(), lParam)) return new IntPtr(1);
        }
        catch (Exception ex) { Log.Write("键盘钩子异常: " + ex); }
        return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
    }

    // 返回 true 表示吞掉这次输入
    bool HandleMouse(int msg, IntPtr lParam)
    {
        int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
        lastRawInput = Environment.TickCount;

        bool meaningful = false, isPress = false;
        if (msg == Native.WM_MOUSEMOVE)
        {
            long dx = x - anchorX, dy = y - anchorY, t = hooks.MoveThreshold;
            meaningful = dx * dx + dy * dy > t * t;
        }
        else if (msg == 0x201 || msg == 0x204 || msg == 0x207 || msg == 0x20B)
        {
            meaningful = isPress = true;
        }
        else if (msg == Native.WM_MOUSEWHEEL || msg == Native.WM_MOUSEHWHEEL)
        {
            meaningful = true;
        }
        else if (swallowMouseUp.Remove(msg))
        {
            return true; // 吞掉被拦截点击对应的抬起
        }

        if (overlayVisible)
        {
            if (meaningful) { anchorX = x; anchorY = y; }
            if (msg == Native.WM_MOUSEMOVE)
            {
                if (meaningful && WakeByMouse && Since(overlayShownAt) >= GraceMs) QueueDismiss();
            }
            else if (meaningful)
            {
                if (WakeByMouse && Since(overlayShownAt) >= GraceMs) QueueDismiss();
                // 黑掉的屏幕上的点击/滚轮不传给下面的程序, 防止误操作
                if (OnBlankedScreen(x, y))
                {
                    if (isPress) swallowMouseUp.Add(msg + 1);
                    return true;
                }
            }
        }
        else if (meaningful)
        {
            anchorX = x; anchorY = y;
            lastActivity = Environment.TickCount;
        }
        return false;
    }

    bool HandleKey(int msg, IntPtr lParam)
    {
        int vk = Marshal.ReadInt32(lParam, 0);
        lastRawInput = Environment.TickCount;
        bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
        bool isMod = KeyCombo.IsModifier(vk);

        var target = capture; // UI 线程随时可能改 capture, 只读一次
        if (target != null && target.IsCapturing && !overlayVisible)
        {
            // 录入快捷键: 按下普通键时连同已按住的修饰键一起记录; 单独按下并松开修饰键则记录该修饰键
            if (down && !isMod)
            {
                int mods = KeyCombo.CurrentMods(vk), v = KeyCombo.Normalize(vk);
                pendingModOnly = 0;
                invoker.BeginInvoke((Action)delegate { if (capture == target && target.IsCapturing) target.OnCaptured(mods, v); });
                swallowKeyUp.Add(vk);
                return true;
            }
            if (down && isMod) pendingModOnly = KeyCombo.Normalize(vk);
            else if (!down && isMod && pendingModOnly == KeyCombo.Normalize(vk))
            {
                int mods = KeyCombo.CurrentMods(vk), v = pendingModOnly;
                pendingModOnly = 0;
                invoker.BeginInvoke((Action)delegate { if (capture == target && target.IsCapturing) target.OnCaptured(mods, v); });
            }
            if (!down && swallowKeyUp.Remove(vk)) return true;
        }
        else if (!down)
        {
            if (swallowKeyUp.Remove(vk)) return true;
        }
        else if (overlayVisible)
        {
            var h = hooks;
            if (Since(overlayShownAt) >= GraceMs &&
                (h.WakeMode != Settings.WakeKey || KeyCombo.Matches(vk, KeyCombo.CurrentMods(vk), h.WakeVk, h.WakeMods)))
                QueueDismiss();
            // 黑屏期间的按键不传给前台程序, 避免盲打误输入; 修饰键放行以免卡键/弹出开始菜单.
            // 只黑部分显示器且前台窗口在没黑的屏上时照常放行
            if (!isMod && ForegroundOnBlankedScreen()) { swallowKeyUp.Add(vk); return true; }
        }
        else
        {
            lastActivity = Environment.TickCount;
            Action hotkey = isMod ? null : FindHotkey(vk, KeyCombo.CurrentMods(vk));
            if (hotkey != null)
            {
                invoker.BeginInvoke(hotkey);
                swallowKeyUp.Add(vk);
                return true;
            }
        }
        return false;
    }

    // 所有全局快捷键都在钩子里处理, 不会被其他程序抢占
    // 快捷键已被谁占用, 没有冲突时返回 null. exceptKey / exceptProfile 是正在设置的那一项
    public string HotkeyOwner(int mods, int vk, string exceptKey, DisplayProfile exceptProfile)
    {
        if (exceptKey != "blank" && KeyCombo.Same(mods, vk, cfg.BlankMods, cfg.BlankVk)) return "立即黑屏";
        if (exceptKey != "toggle" && KeyCombo.Same(mods, vk, cfg.ToggleMods, cfg.ToggleVk)) return "循环切换模式";
        foreach (var p in cfg.Profiles)
            if (p != exceptProfile && KeyCombo.Same(mods, vk, p.HotkeyMods, p.HotkeyVk)) return "模式「" + p.Name + "」";
        return null;
    }

    Action FindHotkey(int vk, int mods)
    {
        var h = hooks;
        for (int i = 0; i < h.Vks.Length; i++)
            if (KeyCombo.Matches(vk, mods, h.Vks[i], h.Mods[i])) return h.Actions[i];
        return null;
    }

    void QueueDismiss()
    {
        if (dismissQueued) return;
        dismissQueued = true;
        invoker.BeginInvoke((Action)HideOverlay);
    }

    // ---------- 黑屏 ----------

    public void BlankNow() { ShowOverlay(); }

    // 要黑屏的显示器; 选中的显示器都不在了就退回全部
    List<Screen> BlankTargets()
    {
        var all = new List<Screen>(Screen.AllScreens);
        if (cfg.BlankDevices.Count == 0) return all;
        var list = all.FindAll(s => cfg.BlankDevices.Exists(d => string.Equals(d, s.DeviceName, StringComparison.OrdinalIgnoreCase)));
        return list.Count > 0 ? list : all;
    }

    void ShowOverlay()
    {
        if (overlayVisible || locked) return;
        var targets = BlankTargets();
        var rects = new List<Rectangle>();
        foreach (var s in targets)
        {
            var f = new BlankForm(s.Bounds);
            f.Show();
            f.KeepOnTop();
            overlays.Add(f);
            rects.Add(s.Bounds);
        }
        blankRects = targets.Count < Screen.AllScreens.Length ? rects.ToArray() : null;
        var p = Cursor.Position; anchorX = p.X; anchorY = p.Y;
        overlayShownAt = Environment.TickCount;
        monitorOffSent = false;
        dismissQueued = false;
        overlayVisible = true;
    }

    void HideOverlay()
    {
        if (!overlayVisible) return;
        overlayVisible = false;
        dismissQueued = false;
        foreach (var f in overlays) f.Hide(); // 先全部隐藏, 让画面立即恢复
        foreach (var f in overlays) { f.Close(); f.Dispose(); }
        overlays.Clear();
        lastActivity = Environment.TickCount;
    }

    public void Pause(int minutes)
    {
        pausedUntil = minutes <= 0 ? DateTime.MaxValue : DateTime.Now.AddMinutes(minutes);
        Tick();
    }

    public void Resume()
    {
        pausedUntil = DateTime.MinValue;
        lastActivity = Environment.TickCount;
        Tick();
    }

    // 记录当前前台程序 (排除本程序和桌面/任务栏), 返回该窗口是否全屏
    bool UpdateForeground()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        var sb = new System.Text.StringBuilder(64);
        Native.GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "NotifyIconOverflowWindow") return false;

        uint pid;
        Native.GetWindowThreadProcessId(hwnd, out pid);
        if (pid == selfPid) return false;
        if (hwnd != fgHwnd)
        {
            fgHwnd = hwnd;
            try { fgProcess = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
            catch { fgProcess = null; }
        }

        Native.RECT r;
        if (!Native.GetWindowRect(hwnd, out r)) return false;
        var screen = Screen.FromHandle(hwnd);
        var b = screen.Bounds;
        fgScreen = screen.DeviceName;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    // 返回不该黑屏的原因, null 表示可以黑屏
    string KeepAwakeReason(bool fullscreen)
    {
        if (confirming) return "等待确认显示模式"; // 别让黑屏盖住确认框
        if (fgProcess != null && Native.GetForegroundWindow() == fgHwnd && cfg.Whitelist.Contains(fgProcess)) return "白名单：" + fgProcess;
        // 全屏应用只在它所在的显示器会被黑屏时才算 (比如游戏在另一块非 OLED 屏上就不影响)
        if (cfg.SkipFullscreen && fullscreen && BlankTargets().Exists(s => s.DeviceName == fgScreen)) return "全屏应用";
        if (cfg.SkipAudio && audio.IsPlaying()) return "正在播放声音";
        return null;
    }

    void Tick()
    {
        bool paused = Paused;
        bool fullscreen = UpdateForeground();

        if (cfg.Gamepad && gamepad.HasActivity())
        {
            if (overlayVisible && cfg.WakeMode == Settings.WakeAny) HideOverlay();
            else if (!overlayVisible) lastActivity = Environment.TickCount;
        }

        keepAwakeReason = null;
        if (overlayVisible)
        {
            foreach (var f in overlays) f.KeepOnTop();
            // 可选: 黑屏一段时间后真正关闭显示器; 若被抖动唤醒(仍是黑屏), 静止 10 秒后再关一次
            if (cfg.PowerOffMin > 0 && Since(overlayShownAt) >= cfg.PowerOffMin * 60000 && overlays.Count > 0)
            {
                bool wokenSince = monitorOffSent && unchecked(lastRawInput - monitorOffAt) > 0;
                if ((!monitorOffSent || wokenSince) && Since(lastRawInput) >= 10000)
                {
                    tray.MonitorOff();
                    monitorOffSent = true;
                    monitorOffAt = Environment.TickCount;
                }
            }
        }
        else if (locked)
        {
            // 锁屏时键鼠钩子收不到输入, 不做黑屏判断; 锁屏界面也是静态画面, 按设置关闭显示器
            lastActivity = Environment.TickCount;
            if (cfg.LockScreenOffSec > 0 && !lockOffSent && Since(lockedAt) >= cfg.LockScreenOffSec * 1000)
            {
                tray.MonitorOff();
                lockOffSent = true;
            }
        }
        else if (!paused)
        {
            keepAwakeReason = KeepAwakeReason(fullscreen);
            if (keepAwakeReason != null)
                lastActivity = Environment.TickCount;
            else if (Since(lastActivity) >= cfg.TimeoutSec * 1000)
                ShowOverlay();
        }

        if (Since(lastAutoCheck) >= 2000) CheckAutoSwitch();
        if (cfg.CheckUpdates && DateTime.Now >= nextUpdateCheck)
        {
            nextUpdateCheck = DateTime.Now.AddHours(24);
            CheckForUpdate(false);
        }
        // 自动更新: 等键鼠停一会儿 (但要赶在黑屏之前) 再装, 别在打字时突然重启
        if (autoInstallPending && !updating && SafeToRestart() && Since(lastRawInput) >= Math.Min(30, cfg.TimeoutSec / 2) * 1000)
            InstallUpdate();

        if (overlayVisible) guardStatus = "黑屏中";
        else if (locked) guardStatus = "已锁屏";
        else if (paused) guardStatus = pausedUntil == DateTime.MaxValue ? "已暂停" : "暂停至 " + pausedUntil.ToString("HH:mm");
        else if (keepAwakeReason != null) guardStatus = "不黑屏（" + keepAwakeReason + "）";
        else
        {
            int left = Math.Max(0, cfg.TimeoutSec - Since(lastActivity) / 1000);
            guardStatus = string.Format("{0}:{1:00} 后黑屏", left / 60, left % 60);
        }

        tray.Icon = paused ? iconPaused : iconActive;
        string tip = "DisplayKit\n防烧屏：" + guardStatus;
        if (display != null) tip += "\n显示：" + (profileIndex >= 0 ? cfg.Profiles[profileIndex].Name + " · " : "") + display.Describe();
        if (tip != lastTip) { tray.Text = tip; lastTip = tip; }

        // 托盘图标没加上 (例如开机时任务栏还没就绪) 就每 3 秒重试
        if (!tray.Added && Since(lastTrayTry) > 3000)
        {
            lastTrayTry = Environment.TickCount;
            tray.TryAdd();
        }
        if (settings != null && settings.Visible) settings.UpdateLive();
    }

    // ---------- 显示模式 ----------

    public string Device
    {
        get
        {
            try { return DisplayController.ResolveDevice(cfg.DisplayDevice); }
            catch { return null; }
        }
    }

    public void RefreshDisplay()
    {
        try { display = DisplayController.GetState(DisplayController.ResolveDevice(cfg.DisplayDevice)); }
        catch { display = null; }
        profileIndex = display == null ? -1 : cfg.Profiles.FindIndex(p => DisplayController.Matches(display, p));
        lastTip = null;
        if (settings != null && settings.Visible) settings.UpdateLive();
    }

    public void SwitchProfile(DisplayProfile p, IWin32Window owner)
    {
        SwitchProfile(p, owner, null);
    }

    // 切换显示模式, 返回切换前的模式 (用于恢复), 失败返回 null.
    // autoReason 不为空表示由"程序启动时自动切换"触发, 这时不弹确认框
    DisplayProfile SwitchProfile(DisplayProfile p, IWin32Window owner, string autoReason)
    {
        if (switching || confirming) return null;
        switching = true;
        DisplayProfile before = null;
        bool needConfirm = false;
        try
        {
            string device = DisplayController.ResolveDevice(cfg.DisplayDevice);
            var s = DisplayController.GetState(device);
            before = new DisplayProfile { Name = "之前的模式", Width = s.Width, Height = s.Height, RefreshRate = s.RefreshRate, Scale = s.Scale };
            DisplayController.Apply(device, p);

            // 第一次切到一个改变分辨率/刷新率的模式时让用户确认, 万一显示器没信号 15 秒后自动恢复 (自动切换也一样)
            bool modeChanged = s.Width != p.Width || s.Height != p.Height || (p.RefreshRate > 0 && Math.Abs(s.RefreshRate - p.RefreshRate) > 1);
            if (!p.Verified && modeChanged) needConfirm = true;
            else if (autoReason != null) tray.ShowBalloon(autoReason + "，已切换到「" + p.Name + "」", p.Describe(), TrayIcon.InfoInfo);
            else if (owner == null) tray.ShowBalloon("已切换到「" + p.Name + "」", p.Describe(), TrayIcon.InfoInfo);
        }
        catch (Exception ex)
        {
            before = null;
            Log.Write("切换到 " + p.Name + " 失败: " + ex.Message);
            if (owner != null) MessageBox.Show(owner, ex.Message, "切换到「" + p.Name + "」失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else tray.ShowBalloon("切换到「" + p.Name + "」失败", ex.Message, TrayIcon.InfoError);
        }
        finally
        {
            switching = false;
            RefreshDisplay();
        }
        if (needConfirm)
        {
            var restore = before;
            invoker.BeginInvoke((Action)delegate { ConfirmProfile(p, restore); });
        }
        return before;
    }

    void ConfirmProfile(DisplayProfile p, DisplayProfile before)
    {
        confirming = true;
        bool keep;
        using (var f = new ConfirmModeForm(p.Name, AppIcon))
        {
            f.ShowDialog();
            keep = f.Keep;
        }
        confirming = false;
        if (keep)
        {
            p.Verified = true;
            cfg.Save();
            return;
        }
        if (autoProfile == p) autoRestore = null; // 已经恢复过了, 程序退出时不用再恢复
        RestoreMode(before, "已恢复为之前的显示模式");
    }

    void RestoreMode(DisplayProfile before, string message)
    {
        try
        {
            DisplayController.Apply(DisplayController.ResolveDevice(cfg.DisplayDevice), before);
            tray.ShowBalloon(message, before.Describe(), TrayIcon.InfoInfo);
        }
        catch (Exception ex)
        {
            Log.Write("恢复显示模式失败: " + ex.Message);
            tray.ShowBalloon("恢复显示模式失败", ex.Message, TrayIcon.InfoError);
        }
        RefreshDisplay();
    }

    void ToggleProfile()
    {
        if (cfg.Profiles.Count == 0) return;
        RefreshDisplay();
        SwitchProfile(cfg.Profiles[(profileIndex + 1) % cfg.Profiles.Count], null);
    }

    // 某个模式关联的程序启动时自动切过去, 程序退出后切回原来的模式
    void CheckAutoSwitch()
    {
        lastAutoCheck = Environment.TickCount;
        if (autoProfile == null && !cfg.Profiles.Exists(p => p.AutoApps.Count > 0)) return;
        if (switching || confirming) return;

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try { running.Add(proc.ProcessName); }
            catch { }
            finally { proc.Dispose(); }
        }

        if (autoProfile != null)
        {
            if (running.Contains(autoProcess)) return;
            var restore = autoRestore;
            string name = autoProcess;
            autoProfile = null;
            autoProcess = null;
            autoRestore = null;
            if (restore != null) RestoreMode(restore, name + ".exe 已退出，恢复显示模式");
            return;
        }

        foreach (var p in cfg.Profiles)
            foreach (var exe in p.AutoApps)
            {
                if (!running.Contains(exe)) continue;
                autoProfile = p;
                autoProcess = exe;
                // 已经是这个模式就不用切, 程序退出后也不切回
                autoRestore = profileIndex >= 0 && cfg.Profiles[profileIndex] == p ? null : SwitchProfile(p, null, "检测到 " + exe + ".exe");
                return;
            }
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        invoker.BeginInvoke((Action)delegate
        {
            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                // 锁屏界面在黑屏窗口之上, 黑屏留着只会挡掉下面"锁屏后关闭显示器"的逻辑
                HideOverlay();
                locked = true;
                lockedAt = Environment.TickCount;
                lockOffSent = false;
            }
            else if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                // 解锁时用户已经输过密码, 不需要再按唤醒键
                locked = false;
                HideOverlay();
                lastActivity = Environment.TickCount;
            }
        });
    }

    // ---------- 检查更新 ----------

    public void CheckForUpdate(bool manual)
    {
        if (updateChecking || updating) return;
        updateChecking = true;
        updateStatus = "正在检查…";
        ThreadPool.QueueUserWorkItem(delegate
        {
            string tag = null, url = null, error = null;
            try
            {
                using (var wc = NewWebClient())
                {
                    string json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
                    var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                    if (m.Success) tag = m.Groups[1].Value;
                    else error = "没有找到版本信息";
                    var a = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/DisplayKit\\.exe)\"", RegexOptions.IgnoreCase);
                    if (a.Success) url = a.Groups[1].Value;
                }
            }
            catch (Exception ex) { error = ex.Message; }
            invoker.BeginInvoke((Action)delegate { OnUpdateChecked(tag, url, error, manual); });
        });
    }

    static WebClient NewWebClient()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        var wc = new WebClient { Encoding = Encoding.UTF8 };
        wc.Headers.Add("User-Agent", "DisplayKit/" + Version);
        return wc;
    }

    void OnUpdateChecked(string tag, string url, string error, bool manual)
    {
        updateChecking = false;
        if (error != null)
        {
            updateStatus = "检查失败：" + error;
            Log.Write("检查更新失败: " + error);
        }
        else if (IsNewer(tag, Version))
        {
            bool first = latestVersion != tag;
            latestVersion = tag;
            latestUrl = url;
            updateStatus = "发现新版本 v" + tag;
            if (cfg.AutoInstallUpdates && url != null && autoInstallFailed != tag)
            {
                autoInstallPending = true; // 等到合适的时机 (见 Tick) 再装
                Log.Write("发现新版本 v" + tag + ", 准备自动更新");
            }
            else if (first)
                tray.ShowBalloon("DisplayKit 有新版本 v" + tag, url != null ? "点击这里立即更新" : "到 GitHub 下载新版本", TrayIcon.InfoInfo, "update");
        }
        else
        {
            updateStatus = "已是最新版本";
        }
        if (settings != null) settings.OnUpdateChecked();
    }

    // 自动安装要重启程序, 黑屏/锁屏/暂停/自动切换等状态会丢, 这些时候先不装
    bool SafeToRestart()
    {
        return !overlayVisible && !locked && !Paused && !switching && !confirming && autoProfile == null
            && (capture == null || !capture.IsCapturing);
    }

    // 下载新版本 exe, 校验版本号后替换正在运行的 exe 并重启
    public void InstallUpdate()
    {
        autoInstallPending = false;
        if (updating || latestVersion == null) return;
        if (latestUrl == null) { OpenUrl(RepoUrl + "/releases/latest"); return; }
        updating = true;
        updateStatus = "正在下载 v" + latestVersion + "…";
        if (settings != null) settings.OnUpdateChecked();
        string tag = latestVersion, url = latestUrl, newPath = ExePath + ".new";
        bool auto = !(settings != null && settings.Visible);
        ThreadPool.QueueUserWorkItem(delegate
        {
            string error = null;
            try
            {
                File.Delete(newPath);
                using (var wc = NewWebClient()) wc.DownloadFile(url, newPath);
                // 文件版本必须和发布标签一致, 防止下载不完整或拿错文件
                string v = FileVersionInfo.GetVersionInfo(newPath).ProductVersion;
                if (v != tag) throw new InvalidDataException("下载的文件版本不对（" + (v ?? "无版本信息") + "）");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { File.Delete(newPath); } catch { }
            }
            invoker.BeginInvoke((Action)delegate { OnUpdateDownloaded(tag, newPath, error, auto); });
        });
    }

    void OnUpdateDownloaded(string tag, string newPath, string error, bool background)
    {
        updating = false;
        if (error == null)
        {
            // 正在运行的 exe 不能覆盖但可以改名: 旧的改成 .old, 新的放到原位置, 启动新版本后退出
            string old = ExePath + ".old";
            try
            {
                File.Delete(old);
                File.Move(ExePath, old);
                try { File.Move(newPath, ExePath); }
                catch { File.Move(old, ExePath); throw; }
                try { Process.Start(ExePath, "--updated" + (background ? " --autostart" : "")); }
                catch { File.Move(ExePath, newPath); File.Move(old, ExePath); throw; }
            }
            catch (Exception ex)
            {
                error = ex is UnauthorizedAccessException ? "没有权限替换 " + ExePath + "，请手动下载" : ex.Message;
                try { File.Delete(newPath); } catch { }
            }
        }
        if (error != null)
        {
            autoInstallFailed = tag;
            updateStatus = "更新失败：" + error;
            Log.Write("更新到 v" + tag + " 失败: " + error);
            tray.ShowBalloon("DisplayKit 更新失败", error, TrayIcon.InfoError);
            if (settings != null) settings.OnUpdateChecked();
            return;
        }
        Log.Write("已更新到 v" + tag + ", 重启");
        ExitThread();
    }

    // 更新后删除旧版本 exe; 旧进程可能还没完全退出, 多试几次
    static void DeleteOldExe()
    {
        string old = ExePath + ".old";
        ThreadPool.QueueUserWorkItem(delegate
        {
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(1000); }
            }
        });
    }

    static bool IsNewer(string remote, string local)
    {
        System.Version r, l;
        return System.Version.TryParse(remote, out r) && System.Version.TryParse(local, out l) && r > l;
    }

    public static void OpenUrl(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("打开 " + target + " 失败: " + ex.Message); }
    }

    void OnDisplaySettingsChanged(object sender, EventArgs e)
    {
        invoker.BeginInvoke((Action)delegate
        {
            if (overlayVisible) { HideOverlay(); ShowOverlay(); }
            if (!switching) RefreshDisplay();
        });
    }

    void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color)
            invoker.BeginInvoke((Action)ApplyThemeChange);
    }

    void ApplyThemeChange()
    {
        bool wasDark = Theme.Dark, wasLight = taskbarLight;
        Theme.Load();
        if (wasDark == Theme.Dark && wasLight == Theme.TaskbarLight()) return;
        ToolStripManager.Renderer = new ModernRenderer();
        MakeIcons();
        tray.Icon = Paused ? iconPaused : iconActive;
        if (settings != null) settings.ApplyTheme();
    }

    // ---------- 设置 / 菜单 ----------

    public void Save()
    {
        cfg.Save();
        RebuildHooks();
        lastActivity = Environment.TickCount;
        RefreshDisplay();
    }

    public void OpenSettings()
    {
        if (settings == null)
        {
            Ui.SetDpi(Ui.DpiAt(Cursor.Position));
            settings = new SettingsForm(this);
            settings.FormClosed += delegate
            {
                if (capture == settings) Capture = null;
                settings = null;
            };
            settings.Show();
        }
        else if (settings.WindowState == FormWindowState.Minimized)
        {
            settings.WindowState = FormWindowState.Normal;
        }
        settings.Activate();
    }

    public static string PauseName(int minutes)
    {
        if (minutes <= 0) return "直到手动恢复";
        return minutes < 60 ? minutes + " 分钟" : minutes / 60 + " 小时";
    }

    void FillTrayMenu()
    {
        var m = tray.Menu;
        var old = new List<ToolStripItem>();
        foreach (ToolStripItem it in m.Items) old.Add(it);
        m.Items.Clear();
        foreach (var it in old) it.Dispose();
        // 菜单按任务栏所在屏幕的缩放显示 (设置窗口开着时沿用它的缩放)
        if (settings == null || !settings.Visible) Ui.SetDpi(Ui.DpiAt(Cursor.Position));
        m.Font = Theme.Font;
        m.Padding = new Padding(0, Ui.S(4), 0, Ui.S(4));

        m.Items.Add(Ui.Label("防烧屏：" + guardStatus));
        if (display != null) m.Items.Add(Ui.Label("显示：" + display.Describe()));
        m.Items.Add(Ui.Separator());

        if (cfg.Profiles.Count > 0)
        {
            for (int i = 0; i < cfg.Profiles.Count; i++)
            {
                var p = cfg.Profiles[i];
                var it = Ui.Item(m.Items, p.Name, i == profileIndex, delegate { SwitchProfile(p, null); });
                if (p.HotkeyVk != 0) it.ShortcutKeyDisplayString = KeyCombo.Name(p.HotkeyMods, p.HotkeyVk);
            }
            if (cfg.Profiles.Count > 1)
            {
                var t = Ui.Item(m.Items, "切换到下一个模式", false, delegate { ToggleProfile(); });
                if (cfg.ToggleVk != 0) t.ShortcutKeyDisplayString = KeyCombo.Name(cfg.ToggleMods, cfg.ToggleVk);
            }
            m.Items.Add(Ui.Separator());
        }

        var blank = Ui.Item(m.Items, "立即黑屏", false, delegate { ShowOverlay(); });
        blank.ShortcutKeyDisplayString = KeyCombo.Name(cfg.BlankMods, cfg.BlankVk);
        if (Paused) Ui.Item(m.Items, "恢复防烧屏", false, delegate { Resume(); });
        else
        {
            var pause = Ui.Sub(m.Items, "暂停防烧屏");
            foreach (int min in new[] { 30, 60, 120, 0 })
            {
                int v = min;
                Ui.Item(pause.DropDownItems, PauseName(v), false, delegate { Pause(v); });
            }
        }
        m.Items.Add(Ui.Separator());
        Ui.Item(m.Items, "设置…", false, delegate { OpenSettings(); });
        Ui.Item(m.Items, "退出", false, delegate { ExitThread(); });
    }

    // 以前设置过 OledGuard 开机自启的, 换成 DisplayKit
    static void MigrateAutoStart()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null || k.GetValue(LegacyRunValue) == null) return;
                k.DeleteValue(LegacyRunValue, false);
            }
            SetAutoStart(true);
            Log.Write("已把 OledGuard 的开机自启换成 DisplayKit");
        }
        catch (Exception ex) { Log.Write("迁移开机自启失败: " + ex.Message); }
    }

    public static bool IsAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(RunValue) != null;
    }

    public static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" --autostart");
            else k.DeleteValue(RunValue, false);
        }
    }

    // 托盘图标: 显示器轮廓 (跟随任务栏深浅色) + 状态圆点
    void MakeIcons()
    {
        taskbarLight = Theme.TaskbarLight();
        var oldActive = iconActive; var oldPaused = iconPaused;
        iconActive = MakeIcon(Color.FromArgb(16, 200, 120), taskbarLight);
        iconPaused = MakeIcon(Color.FromArgb(150, 150, 150), taskbarLight);
        tray.Icon = iconActive;
        if (oldActive != null) Native.DestroyIcon(oldActive.Handle);
        if (oldPaused != null) Native.DestroyIcon(oldPaused.Handle);
    }

    static Icon MakeIcon(Color dot, bool lightTaskbar)
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            Color stroke = lightTaskbar ? Color.FromArgb(32, 32, 32) : Color.White;
            using (var pen = new Pen(stroke, 2.6f))
            using (var screen = Theme.RoundRect(new RectangleF(2.5f, 4.5f, 27, 19), 3.5f))
            {
                g.FillPath(Brushes.Black, screen);
                g.DrawPath(pen, screen);
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLine(pen, 16, 24, 16, 27.5f);
                g.DrawLine(pen, 10, 28.5f, 22, 28.5f);
            }
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, 11, 9, 10, 10);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        StopHooks();
        if (overlayVisible) HideOverlay();
        if (settings != null) settings.Close();
        tray.Remove();
        Log.Write("退出");
        base.ExitThreadCore();
    }
}
