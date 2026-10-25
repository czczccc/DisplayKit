using System;
using System.Collections.Generic;
using System.Device.Location;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    public const string Version = "1.6.3";
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
    readonly List<WarnForm> warnings = new List<WarnForm>();
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
    volatile int lastMouseInput = Environment.TickCount; // 最后一次有效的鼠标操作 (闲置隐藏指针用)
    volatile bool cursorRestoreQueued;
    bool powerOffNow; // 按了"立即关闭显示器": 黑屏后不等设置的时间, 松开按键就关
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
    bool? hdrState; // 要切换的显示器的 HDR 状态, null = 不支持

    // 显示器硬件设置 (DDC/CI), 作用在"要切换的显示器"上
    const int BrightnessStep = 10;
    readonly DdcWriter ddc;
    string ddcDevice;                        // 下面这些值读自哪台显示器
    bool inputsTried;                        // 这台显示器的输入源列表读过了 (能力字符串要一两秒, 只读一次)
    int lastDdcRead = unchecked(Environment.TickCount - 600000);
    int ddcRetryAt; bool ddcRetry;           // 显示器断电重开、睡眠唤醒等之后过几秒重读
    Flyout flyout;
    int flyoutClosedAt = unchecked(Environment.TickCount - 600000);
    int brightness = -1, contrast = -1;      // 百分比, -1 = 读不到 (不支持 DDC/CI 或还在读)
    int input = -1;
    List<int> inputs;                        // 显示器声明支持的输入源
    bool ddcReading;
    OsdForm osd;
    volatile bool dimmed, undimQueued;       // 闲置时降了亮度
    int dimmedFrom;
    int profileIndex = -1;
    bool switching, confirming;
    int lastAutoCheck = Environment.TickCount;
    DisplayProfile autoProfile, autoRestore; // 自动切换: 当前生效的模式, 程序退出后要恢复的模式
    string autoProcess;

    // 检查更新
    DateTime nextUpdateCheck = DateTime.Now.AddSeconds(15);
    const int AutoInstallDelayHours = 24; // 新版本发布满这么久才自动安装, 有问题的版本来得及撤下
    string latestVersion, latestUrl, latestHashUrl, updateStatus;
    DateTime latestPublished = DateTime.MinValue; // UTC
    bool updateChecking, updating, autoInstallPending;
    string autoInstallFailed; // 自动安装失败过的版本, 不再自动重试
    Icon formIcon;

    // 跟随日出日落切换深浅色
    int lastSunCheck = unchecked(Environment.TickCount - 60000);
    bool? sunLight; // 上次按日出日落定下的主题, null = 还没定 (下次检查时立即应用)
    GeoCoordinateWatcher geo;
    int geoStartedAt;
    DateTime nextLocate = DateTime.Now.AddSeconds(5);
    string locationStatus;
    bool? sunBright;                            // 上次按日出日落定下的亮度 (true = 白天), null = 还没定
    int rampTarget = -1, lastRamp;              // 日出日落时亮度逐步过渡到的目标, -1 = 没有在过渡
    bool night;                                 // 现在是日落到日出之间
    double tintKelvin = NightTint.Neutral;      // 当前已应用的色温, 渐变用
    int lastTintApply;

    SettingsForm settings;
    volatile IKeyCapture capture;
    int pendingModOnly; // 录入快捷键时: 只按了修饰键还没按别的键

    static int Since(int tick) { return unchecked(Environment.TickCount - tick); }

    public Settings Cfg { get { return cfg; } }
    public bool TrayOk { get { return tray.Added; } }
    public string GuardStatus { get { return guardStatus; } }
    public DisplayState Display { get { return display; } }
    public bool? HdrState { get { return hdrState; } }
    public int Brightness { get { return dimmed ? dimmedFrom : brightness; } } // 降亮度期间显示原来的亮度
    public int Contrast { get { return contrast; } }
    public int Input { get { return input; } }
    public List<int> Inputs { get { return inputs; } }
    public bool DdcReading { get { return ddcReading; } }
    public int ProfileIndex { get { return profileIndex; } }
    public Icon AppIcon { get { return formIcon ?? iconActive; } }
    public string LatestVersion { get { return latestVersion; } }
    public string UpdateStatus { get { return updateStatus; } }
    public bool UpdateChecking { get { return updateChecking; } }
    public bool Updating { get { return updating; } }
    public string LocationStatus { get { return locationStatus; } }
    public bool Locating { get { return geo != null; } }
    public bool Paused { get { return DateTime.Now < pausedUntil; } }
    public DateTime PausedUntil { get { return pausedUntil; } }
    public IKeyCapture Capture { get { return capture; } set { capture = value; pendingModOnly = 0; } }

    public App(bool autostart, bool updated, EventWaitHandle showSignal)
    {
        var h = invoker.Handle; // 强制创建句柄, 供 BeginInvoke 使用
        ddc = new DdcWriter(delegate { invoker.BeginInvoke((Action)delegate { ScheduleDdcRefresh(3000); }); }); // 写入失败多半是显示器断开过
        var p = Cursor.Position; anchorX = p.X; anchorY = p.Y;
        Log.Write("启动 v" + Version + (autostart ? " (开机自启)" : "") + (updated ? " (自动更新后)" : ""));

        Theme.Load();
        ToolStripManager.Renderer = new ModernRenderer();
        MakeIcons();
        try { formIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        tray.Icon = iconActive;
        tray.Menu = Ui.Menu();
        tray.Menu.Opening += (s, e) => { FillTrayMenu(); e.Cancel = false; };
        tray.LeftClick += OpenFlyout; // 左键: 快捷面板; 右键: 菜单 (含打开设置)
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
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        MigrateAutoStart();
        if (IsAutoStart()) SetAutoStart(true); // exe 换过位置时更新自启路径
        RefreshDisplay();
        CursorHider.Restore();
        ApplyWallpaper();

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
        vks.Add(cfg.PowerOffVk); mods.Add(cfg.PowerOffMods); actions.Add(PowerOffNow);
        vks.Add(cfg.IconsVk); mods.Add(cfg.IconsMods); actions.Add(ToggleDesktopIcons);
        vks.Add(cfg.HdrVk); mods.Add(cfg.HdrMods); actions.Add(ToggleHdr);
        vks.Add(cfg.BrightUpVk); mods.Add(cfg.BrightUpMods); actions.Add(delegate { StepBrightness(BrightnessStep); });
        vks.Add(cfg.BrightDownVk); mods.Add(cfg.BrightDownMods); actions.Add(delegate { StepBrightness(-BrightnessStep); });
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

        if (meaningful)
        {
            lastMouseInput = Environment.TickCount;
            if (CursorHider.Hidden && !cursorRestoreQueued)
            {
                cursorRestoreQueued = true;
                invoker.BeginInvoke((Action)delegate { cursorRestoreQueued = false; CursorHider.Restore(); });
            }
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
            QueueUndim();
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
            QueueUndim(); // 排在快捷键动作前面, 调亮度快捷键会基于恢复后的亮度
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
        if (exceptKey != "poweroff" && KeyCombo.Same(mods, vk, cfg.PowerOffMods, cfg.PowerOffVk)) return "立即关闭显示器";
        if (exceptKey != "icons" && KeyCombo.Same(mods, vk, cfg.IconsMods, cfg.IconsVk)) return "显示/隐藏桌面图标";
        if (exceptKey != "hdr" && KeyCombo.Same(mods, vk, cfg.HdrMods, cfg.HdrVk)) return "开关 HDR";
        if (exceptKey != "brightup" && KeyCombo.Same(mods, vk, cfg.BrightUpMods, cfg.BrightUpVk)) return "调亮";
        if (exceptKey != "brightdown" && KeyCombo.Same(mods, vk, cfg.BrightDownMods, cfg.BrightDownVk)) return "调暗";
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
        HideWarning();
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
        powerOffNow = false;
        dismissQueued = false;
        overlayVisible = true;
    }

    // 黑屏前的提示: 要黑的屏幕逐渐变暗并倒计时, 鼠标键盘穿透, 动一下 lastActivity 就会刷新, 提示随之消失
    void ShowWarning(double progress, int secondsLeft)
    {
        if (warnings.Count == 0)
            foreach (var s in BlankTargets())
            {
                var f = new WarnForm(s.Bounds);
                f.Show();
                warnings.Add(f);
            }
        foreach (var f in warnings) f.SetProgress(progress, secondsLeft);
    }

    void HideWarning()
    {
        if (warnings.Count == 0) return;
        foreach (var f in warnings) { f.Close(); f.Dispose(); }
        warnings.Clear();
    }

    // 时段规则: 当前生效的黑屏等待秒数, 0 = 这个时段不自动黑屏
    public int EffectiveTimeoutSec()
    {
        return InSchedule(DateTime.Now) ? cfg.ScheduleTimeoutSec : cfg.TimeoutSec;
    }

    public bool InSchedule(DateTime now)
    {
        int s = cfg.ScheduleStart, e = cfg.ScheduleEnd, h = now.Hour;
        if (!cfg.ScheduleOn || s == e) return false;
        return s < e ? h >= s && h < e : h >= s || h < e; // 比如 23 点到 7 点跨午夜
    }

    // 先黑屏再关闭显示器: 显示器被抖动唤醒时还是黑屏, 静止后会再关一次
    public void PowerOffNow()
    {
        ShowOverlay();
        if (!overlayVisible) return; // 锁屏时
        powerOffNow = true;
        monitorOffSent = false;
    }

    public void ToggleDesktopIcons()
    {
        if (!DesktopIcons.Toggle()) tray.ShowBalloon("找不到桌面窗口", "资源管理器可能没有在运行", TrayIcon.InfoWarning);
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
            // 黑屏一段时间后 (或按了"立即关闭显示器") 真正关闭显示器; 若被抖动唤醒(仍是黑屏), 静止 10 秒后再关一次
            if ((powerOffNow || (cfg.PowerOffMin > 0 && Since(overlayShownAt) >= cfg.PowerOffMin * 60000)) && overlays.Count > 0)
            {
                bool wokenSince = monitorOffSent && unchecked(lastRawInput - monitorOffAt) > 0;
                int quiet = powerOffNow && !monitorOffSent ? 1000 : 10000; // 快捷键刚按下: 只等松开按键 (松键也会唤醒显示器)
                if ((!monitorOffSent || wokenSince) && Since(lastRawInput) >= quiet)
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
        int timeoutSec = EffectiveTimeoutSec();
        bool warned = false;
        if (overlayVisible || locked || paused) { }
        else
        {
            keepAwakeReason = KeepAwakeReason(fullscreen) ?? (timeoutSec == 0 ? "时段规则" : null);
            if (keepAwakeReason != null)
                lastActivity = Environment.TickCount;
            else
            {
                int idle = Since(lastActivity), timeout = timeoutSec * 1000;
                int warn = Math.Min(cfg.WarnSec * 1000, timeout / 2);
                // 闲置时先降亮度 (要早于黑屏才有意义)
                if (cfg.DimAfterSec > 0 && !dimmed && brightness > cfg.DimLevel && cfg.DimAfterSec * 1000 < timeout && idle >= cfg.DimAfterSec * 1000) Dim();
                if (idle >= timeout) ShowOverlay();
                else if (warn > 0 && idle >= timeout - warn)
                {
                    ShowWarning((double)(idle - (timeout - warn)) / warn, (timeout - idle + 999) / 1000);
                    warned = true;
                }
            }
        }
        if (!warned) HideWarning();
        // 兜底恢复亮度: 手柄、"不黑屏"的情况、黑屏唤醒、解锁等都会刷新 lastActivity (键鼠在钩子里已经即时恢复)
        if (dimmed && !overlayVisible && !locked && Since(lastActivity) < Math.Max(1, cfg.DimAfterSec) * 1000) Undim();
        // 日出日落时亮度逐步过渡, 每 2 秒调 5%
        if (rampTarget >= 0 && !dimmed && brightness >= 0 && Since(lastRamp) >= 2000)
        {
            int next = rampTarget > brightness ? Math.Min(rampTarget, brightness + 5) : Math.Max(rampTarget, brightness - 5);
            SetBrightness(next, false);
            lastRamp = Environment.TickCount;
            if (next == rampTarget) rampTarget = -1;
        }

        CheckDdc();
        if (Since(lastAutoCheck) >= 2000) CheckAutoSwitch();
        if (Since(lastSunCheck) >= 30000) CheckSunTheme();
        UpdateNightTint();
        if (cfg.CursorHideSec > 0 && !CursorHider.Hidden && !overlayVisible && !locked && Since(lastMouseInput) >= cfg.CursorHideSec * 1000) CursorHider.Hide();
        else if (cfg.CursorHideSec == 0 && CursorHider.Hidden) CursorHider.Restore();
        if (cfg.NeedsLocation && cfg.UseSystemLocation && geo == null && DateTime.Now >= nextLocate) Locate();
        if (geo != null && Since(geoStartedAt) >= 60000) StopLocate("定位超时，" + (cfg.HasLocation ? "沿用上次的位置" : "请稍后重试或手动输入坐标"));
        if (cfg.CheckUpdates && DateTime.Now >= nextUpdateCheck)
        {
            nextUpdateCheck = DateTime.Now.AddHours(24);
            CheckForUpdate(false);
        }
        // 自动更新: 等键鼠停一会儿 (但要赶在黑屏之前) 再装, 别在打字时突然重启
        if (autoInstallPending && ReleaseAged && !updating && SafeToRestart() && Since(lastRawInput) >= Math.Min(30, cfg.TimeoutSec / 2) * 1000)
            InstallUpdate();

        if (overlayVisible) guardStatus = "黑屏中";
        else if (locked) guardStatus = "已锁屏";
        else if (paused) guardStatus = pausedUntil == DateTime.MaxValue ? "已暂停" : "暂停至 " + pausedUntil.ToString("HH:mm");
        else if (keepAwakeReason != null) guardStatus = "不黑屏（" + keepAwakeReason + "）";
        else
        {
            int left = Math.Max(0, timeoutSec - Since(lastActivity) / 1000);
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
        try { hdrState = display == null ? null : DisplayController.GetHdr(display.Device); }
        catch { hdrState = null; }
        if (display != null && display.Device != ddcDevice) RefreshDdc(); // 换了要控制的显示器
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
            if (p.Hdr != DisplayProfile.HdrKeep && hdrState != null) before.Hdr = hdrState.Value ? DisplayProfile.HdrOn : DisplayProfile.HdrOff; // 恢复时连 HDR 一起恢复
            if (p.Brightness >= 0 && Brightness >= 0) before.Brightness = Brightness;
            if (p.Brightness >= 0) { Undim(); rampTarget = -1; }
            DisplayController.Apply(device, p);
            if (p.Brightness >= 0 && brightness >= 0 && device == ddcDevice) brightness = p.Brightness;

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
            if (before.Brightness >= 0 && brightness >= 0) brightness = before.Brightness;
            tray.ShowBalloon(message, before.Describe(), TrayIcon.InfoInfo);
        }
        catch (Exception ex)
        {
            Log.Write("恢复显示模式失败: " + ex.Message);
            tray.ShowBalloon("恢复显示模式失败", ex.Message, TrayIcon.InfoError);
        }
        RefreshDisplay();
    }

    public void ToggleHdr()
    {
        RefreshDisplay();
        try
        {
            if (hdrState == null) throw new InvalidOperationException("这台显示器不支持 HDR（或显卡驱动没有开放）。");
            bool on = !hdrState.Value;
            DisplayController.SetHdr(DisplayController.ResolveDevice(cfg.DisplayDevice), on);
            tray.ShowBalloon("HDR 已" + (on ? "开启" : "关闭"), display != null ? display.Describe() : "", TrayIcon.InfoInfo);
        }
        catch (Exception ex)
        {
            Log.Write("切换 HDR 失败: " + ex.Message);
            tray.ShowBalloon("切换 HDR 失败", ex.Message, TrayIcon.InfoError);
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
                CursorHider.Restore();
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
                ScheduleDdcRefresh(3000); // 锁屏期间显示器可能关过
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
            string tag = null, url = null, hashUrl = null, error = null;
            DateTime published = DateTime.MinValue;
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
                    var h = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/DisplayKit\\.exe\\.sha256)\"", RegexOptions.IgnoreCase);
                    if (h.Success) hashUrl = h.Groups[1].Value;
                    var p = Regex.Match(json, "\"published_at\"\\s*:\\s*\"([^\"]+)\"");
                    if (p.Success) DateTime.TryParse(p.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out published);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            invoker.BeginInvoke((Action)delegate { OnUpdateChecked(tag, url, hashUrl, published, error, manual); });
        });
    }

    static WebClient NewWebClient()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
        var wc = new WebClient { Encoding = Encoding.UTF8 };
        wc.Headers.Add("User-Agent", "DisplayKit/" + Version);
        return wc;
    }

    bool ReleaseAged { get { return DateTime.UtcNow - latestPublished >= TimeSpan.FromHours(AutoInstallDelayHours); } }

    void OnUpdateChecked(string tag, string url, string hashUrl, DateTime published, string error, bool manual)
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
            latestHashUrl = hashUrl;
            latestPublished = published;
            updateStatus = "发现新版本 v" + tag;
            bool canInstall = url != null && hashUrl != null;
            if (cfg.AutoInstallUpdates && canInstall && autoInstallFailed != tag)
            {
                autoInstallPending = true; // 等到合适的时机 (见 Tick) 再装
                Log.Write("发现新版本 v" + tag + (ReleaseAged ? ", 准备自动更新" : ", 发布满 " + AutoInstallDelayHours + " 小时后自动更新"));
                if (first && !ReleaseAged)
                    tray.ShowBalloon("DisplayKit 有新版本 v" + tag, "发布满一天后会自动安装，点击这里现在就更新", TrayIcon.InfoInfo, "update");
            }
            else if (first)
                tray.ShowBalloon("DisplayKit 有新版本 v" + tag, canInstall ? "点击这里立即更新" : "到 GitHub 下载新版本", TrayIcon.InfoInfo, "update");
        }
        else
        {
            updateStatus = "已是最新版本";
        }
        if (settings != null) settings.RefreshGeneral();
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
        // 没有校验文件的发布不自动替换, 打开发布页让用户自己下载
        if (latestUrl == null || latestHashUrl == null) { OpenUrl(RepoUrl + "/releases/latest"); return; }
        updating = true;
        updateStatus = "正在下载 v" + latestVersion + "…";
        if (settings != null) settings.RefreshGeneral();
        string tag = latestVersion, url = latestUrl, hashUrl = latestHashUrl, newPath = ExePath + ".new";
        bool auto = !(settings != null && settings.Visible);
        ThreadPool.QueueUserWorkItem(delegate
        {
            string error = null;
            try
            {
                File.Delete(newPath);
                string expected;
                using (var wc = NewWebClient())
                {
                    // 校验文件格式同 sha256sum: "<哈希>  DisplayKit.exe"
                    expected = wc.DownloadString(hashUrl).Trim().Split(' ', '\t')[0].ToLowerInvariant();
                    wc.DownloadFile(url, newPath);
                }
                if (Sha256(newPath) != expected) throw new InvalidDataException("下载的文件校验不通过，可能下载不完整");
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
            if (settings != null) settings.RefreshGeneral();
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

    static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var f = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
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

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) invoker.BeginInvoke((Action)delegate { ScheduleDdcRefresh(5000); }); // 睡眠唤醒后显示器要几秒才响应 DDC/CI
    }

    void OnDisplaySettingsChanged(object sender, EventArgs e)
    {
        invoker.BeginInvoke((Action)delegate
        {
            if (overlayVisible) { HideOverlay(); ShowOverlay(); }
            if (!switching) RefreshDisplay();
            lastTintApply = unchecked(Environment.TickCount - 10000); // 改分辨率会重置调色曲线, 马上重新设
            ScheduleDdcRefresh(3000); // 显示器断电重开也会走到这里; 等几秒它的 DDC/CI 准备好再读
            lastSunCheck = unchecked(Environment.TickCount - 60000); // 睡眠唤醒也会触发, 顺便检查日出日落
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
        if (wasDark != Theme.Dark) ApplyWallpaper();
        if (wasDark == Theme.Dark && wasLight == Theme.TaskbarLight()) return;
        ToolStripManager.Renderer = new ModernRenderer();
        MakeIcons();
        tray.Icon = Paused ? iconPaused : iconActive;
        if (settings != null) settings.ApplyTheme();
        if (flyout != null) flyout.Rebuild();
    }

    // ---------- 日出日落主题 ----------

    // 只在日出 / 日落那一刻 (以及刚打开此功能、程序启动、改了位置时) 切换主题,
    // 中途手动改过主题就保持到下一次日出或日落, 和 iPhone 的"自动"外观一样
    void CheckSunTheme()
    {
        lastSunCheck = Environment.TickCount;
        bool light = cfg.HasLocation && Sun.IsDay(DateTime.Now, cfg.Latitude, cfg.Longitude);
        night = cfg.HasLocation && !light;
        // 亮度和主题一样只在日出 / 日落那一刻调, 中途手动调过的亮度保持到下一次
        if (!cfg.AutoBrightness || !cfg.HasLocation) sunBright = null;
        else if (sunBright != light && brightness >= 0)
        {
            sunBright = light;
            int target = light ? cfg.DayBrightness : cfg.NightBrightness;
            if (dimmed) dimmedFrom = target; // 正在闲置降亮度: 恢复时直接回到新的亮度
            else { rampTarget = target; lastRamp = 0; }
            Log.Write("日" + (light ? "出" : "落") + ", 亮度调到 " + target + "%");
        }
        if (!cfg.AutoTheme || !cfg.HasLocation) { sunLight = null; return; }
        if (sunLight == light) return;
        sunLight = light;
        if (SystemTheme.IsLight() == light) return;
        try
        {
            SystemTheme.Set(light);
            Log.Write("日" + (light ? "出" : "落") + ", 切换到" + (light ? "浅色" : "深色") + "主题");
        }
        catch (Exception ex) { Log.Write("切换主题失败: " + ex.Message); }
    }

    // 设置改动后立即按当前时间应用一次
    public void ApplySunTheme()
    {
        sunLight = null;
        CheckSunTheme();
    }

    public void ApplySunBrightness()
    {
        sunBright = null;
        rampTarget = -1;
        CheckSunTheme();
    }

    // ---------- 显示器亮度 / 输入源 (DDC/CI) ----------

    // 后台读取当前亮度、对比度、输入源; 输入源列表要读能力字符串, 一两秒, 同一台显示器只读一次
    public void RefreshDdc()
    {
        if (ddcReading || display == null) return;
        ddcReading = true;
        string device = display.Device;
        bool readInputs = device != ddcDevice || !inputsTried;
        var knownInputs = readInputs ? null : inputs;
        ThreadPool.QueueUserWorkItem(delegate
        {
            // 每次都重新打开显示器句柄 (见 Ddc.Open), 显示器断电重开后不会卡在失效的旧句柄上
            int b = Ddc.GetPercent(device, Ddc.Brightness), c = b < 0 ? -1 : Ddc.GetPercent(device, Ddc.Contrast);
            int cur, max, inp = b >= 0 && Ddc.Get(device, Ddc.Input, out cur, out max) ? cur & 0xFF : -1;
            var list = b < 0 ? null : readInputs ? Ddc.Inputs(device) : knownInputs;
            invoker.BeginInvoke((Action)delegate
            {
                bool wasMissing = brightness < 0;
                ddcReading = false;
                lastDdcRead = Environment.TickCount;
                if (device != ddcDevice) inputsTried = false;
                ddcDevice = device;
                if (!dimmed) brightness = b;
                contrast = c;
                input = inp;
                inputs = list;
                if (b >= 0) inputsTried = true;
                if (wasMissing && b >= 0) Log.Write("已通过 DDC/CI 连上显示器, 亮度 " + b + "%");
                if (flyout != null) flyout.OnDdcRead();
                // 上次降了亮度没来得及恢复 (异常退出), 现在恢复
                if (cfg.DimmedFrom >= 0 && b >= 0 && !dimmed)
                {
                    Log.Write("恢复上次闲置前的亮度 " + cfg.DimmedFrom + "%");
                    SetBrightness(cfg.DimmedFrom, false);
                    cfg.DimmedFrom = -1;
                    cfg.Save();
                }
                lastSunCheck = unchecked(Environment.TickCount - 60000); // 读到亮度后再按日出日落检查一次
                if (settings != null) settings.OnDdcRead();
            });
        });
    }

    void ScheduleDdcRefresh(int delayMs)
    {
        ddcRetry = true;
        ddcRetryAt = unchecked(Environment.TickCount + delayMs);
    }

    // 读不到时每 30 秒重试 (显示器断电、DDC/CI 刚打开等), 读得到时每 2 分钟校准一次 (显示器上按键调过亮度)
    void CheckDdc()
    {
        if (display == null || ddcReading) return;
        bool due = ddcRetry && Since(ddcRetryAt) >= 0;
        if (!due && Since(lastDdcRead) < (brightness < 0 ? 30000 : 120000)) return;
        if (dimmed || rampTarget >= 0) return; // 正在降亮度 / 过渡中, 读回来的值没意义
        ddcRetry = false;
        RefreshDdc();
    }

    // 用户在滑块上调的亮度: 取消闲置降亮度和自动过渡
    public void SetBrightnessFromUser(int percent)
    {
        Undim();
        rampTarget = -1;
        SetBrightness(percent, false);
    }

    public void ToggleTheme()
    {
        try { SystemTheme.Set(!SystemTheme.IsLight()); }
        catch (Exception ex) { Log.Write("切换主题失败: " + ex.Message); }
    }

    void OpenFlyout()
    {
        if (flyout != null) { flyout.FadeClose(); return; }
        if (Since(flyoutClosedAt) < 300) return; // 点托盘图标让面板失去焦点关掉的那一下, 不要马上又打开
        if (settings == null || !settings.Visible) Ui.SetDpi(Ui.DpiAt(Cursor.Position));
        if (brightness < 0 || Since(lastDdcRead) > 10000) RefreshDdc(); // 打开时顺便重读, 显示器断电重开过也能马上找回来
        flyout = new Flyout(this);
        flyout.FormClosed += delegate { flyout = null; flyoutClosedAt = Environment.TickCount; };
        flyout.ShowAt(Cursor.Position);
    }

    public void SetBrightness(int percent, bool showOsd)
    {
        if (brightness < 0 || ddcDevice == null)
        {
            if (showOsd) ShowOsd("读不到显示器亮度（需要 DDC/CI）", -1);
            return;
        }
        brightness = Math.Max(0, Math.Min(100, percent));
        ddc.SetPercent(ddcDevice, Ddc.Brightness, brightness);
        if (showOsd) ShowOsd("亮度", brightness);
    }

    public void SetContrast(int percent)
    {
        if (contrast < 0 || ddcDevice == null) return;
        contrast = Math.Max(0, Math.Min(100, percent));
        ddc.SetPercent(ddcDevice, Ddc.Contrast, contrast);
    }

    void StepBrightness(int delta)
    {
        rampTarget = -1; // 手动调过就不再自动过渡
        SetBrightness(brightness + delta, true);
    }

    void ShowOsd(string caption, int percent)
    {
        if (osd == null) osd = new OsdForm();
        Screen screen = null;
        foreach (var s in Screen.AllScreens) if (ddcDevice != null && s.DeviceName == ddcDevice) screen = s;
        osd.Popup(screen ?? Screen.PrimaryScreen, caption, percent);
    }

    // 闲置时降低亮度; 有操作 (钩子里即时, 或 Tick 兜底) 立即恢复
    void Dim()
    {
        dimmedFrom = brightness;
        cfg.DimmedFrom = dimmedFrom; // 万一程序异常退出, 下次启动能恢复
        cfg.Save();
        SetBrightness(cfg.DimLevel, false);
        dimmed = true;
    }

    void QueueUndim()
    {
        if (!dimmed || undimQueued) return;
        undimQueued = true;
        invoker.BeginInvoke((Action)Undim);
    }

    void Undim()
    {
        undimQueued = false;
        if (!dimmed) return;
        dimmed = false;
        SetBrightness(dimmedFrom, false);
        cfg.DimmedFrom = -1;
        cfg.Save();
    }

    // 切换显示器的输入源: 切走后这台电脑的画面就看不到了, 由调用方先确认
    public void SwitchInput(int value)
    {
        if (ddcDevice == null) return;
        string device = ddcDevice;
        Log.Write("切换输入源到 " + Ddc.InputName(value));
        ThreadPool.QueueUserWorkItem(delegate
        {
            if (!Ddc.Set(device, Ddc.Input, value)) Log.Write("切换输入源失败");
        });
        input = value;
    }

    public bool ConfirmSwitchInput(IWin32Window owner, int value)
    {
        return MessageBox.Show(owner, "切换到「" + Ddc.InputName(value) + "」后，这台电脑的画面会从显示器上消失，需要用显示器的按键（或另一台设备）切回来。\n\n继续？",
            "切换输入源", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    // 夜间暖色: 日落后 / 日出后在 10 秒左右渐变到目标色温
    void UpdateNightTint()
    {
        double target = night && cfg.NightKelvin > 0 ? cfg.NightKelvin : NightTint.Neutral;
        if (tintKelvin == target)
        {
            // 切换分辨率、全屏游戏、UAC 等会把调色曲线重置掉, 定时重新设一次
            if (NightTint.Active && Since(lastTintApply) >= 10000)
            {
                NightTint.Apply(tintKelvin);
                lastTintApply = Environment.TickCount;
            }
            return;
        }
        tintKelvin = target > tintKelvin ? Math.Min(target, tintKelvin + 100) : Math.Max(target, tintKelvin - 100);
        if (tintKelvin >= NightTint.Neutral) NightTint.Restore();
        else NightTint.Apply(tintKelvin);
        lastTintApply = Environment.TickCount;
    }

    public bool NightTintFailed { get { return NightTint.Failed; } }

    // 壁纸跟随当前的深浅色 (不管是自动切换还是手动改的主题)
    public void ApplyWallpaper()
    {
        string want = SystemTheme.IsLight() ? cfg.LightWallpaper : cfg.DarkWallpaper;
        if (want.Length == 0 || !File.Exists(want) || string.Equals(Wallpaper.Current(), want, StringComparison.OrdinalIgnoreCase)) return;
        Wallpaper.Set(want);
        Log.Write("更换壁纸: " + want);
    }

    // 用 Windows 定位服务取一次位置 (城市级精度就够), 之后每 6 小时更新
    public void Locate()
    {
        nextLocate = DateTime.Now.AddHours(6);
        if (geo != null) return;
        try
        {
            geo = new GeoCoordinateWatcher(GeoPositionAccuracy.Default);
            geo.PositionChanged += (s, e) => invoker.BeginInvoke((Action)delegate { OnLocated(e.Position.Location); });
            geo.StatusChanged += (s, e) =>
            {
                if (e.Status == GeoPositionStatus.Disabled)
                    invoker.BeginInvoke((Action)delegate { StopLocate("Windows 定位已关闭或不允许桌面应用访问位置"); });
            };
            geoStartedAt = Environment.TickCount;
            locationStatus = "正在定位…";
            geo.Start(true);
            if (geo != null && geo.Permission == GeoPositionPermission.Denied) StopLocate("Windows 不允许本程序访问位置");
        }
        catch (Exception ex) { StopLocate("定位失败：" + ex.Message); }
        if (settings != null) settings.RefreshGeneral();
    }

    void OnLocated(GeoCoordinate c)
    {
        if (geo == null || c.IsUnknown) return;
        // 只存两位小数 (约 1 公里), 日出日落用不着更精确的位置
        cfg.Latitude = Math.Round(c.Latitude, 2);
        cfg.Longitude = Math.Round(c.Longitude, 2);
        cfg.Save();
        StopLocate(null);
        ApplySunTheme();
    }

    void StopLocate(string error)
    {
        if (geo != null)
        {
            geo.Stop();
            geo.Dispose();
            geo = null;
        }
        locationStatus = error;
        if (error != null) Log.Write("定位: " + error);
        if (settings != null) settings.RefreshGeneral();
    }

    // ---------- 设置 / 菜单 ----------

    public void Save()
    {
        cfg.Save();
        RebuildHooks();
        lastActivity = Environment.TickCount;
        lastSunCheck = unchecked(Environment.TickCount - 60000); // 外观设置下一次计时立即生效
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

        // 右键: 打开主界面 + 完整菜单; 亮度等快捷调节在左键面板里
        var open = Ui.Item(m.Items, "打开 DisplayKit 设置", false, delegate { OpenSettings(); });
        open.Font = Theme.Bold;
        m.Items.Add(Ui.Separator());
        m.Items.Add(Ui.Label("防烧屏：" + guardStatus));
        if (display != null) m.Items.Add(Ui.Label("显示：" + display.Describe()));
        if (Brightness >= 0) m.Items.Add(Ui.Label("亮度：" + Brightness + "%（左键托盘图标调节）"));
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
        }
        if (hdrState != null)
        {
            var hdr = Ui.Item(m.Items, "HDR", hdrState.Value, delegate { ToggleHdr(); });
            if (cfg.HdrVk != 0) hdr.ShortcutKeyDisplayString = KeyCombo.Name(cfg.HdrMods, cfg.HdrVk);
        }
        if (inputs != null)
        {
            var sub = Ui.Sub(m.Items, "切换输入源");
            foreach (int v in inputs)
            {
                int value = v;
                Ui.Item(sub.DropDownItems, Ddc.InputName(value), value == input, delegate
                {
                    if (value != input && ConfirmSwitchInput(null, value)) SwitchInput(value);
                });
            }
        }
        // 手动切换: 开着日出日落自动切换时, 保持到下一次日出或日落
        bool light = SystemTheme.IsLight();
        Ui.Item(m.Items, light ? "切换到深色模式" : "切换到浅色模式", false, delegate { ToggleTheme(); });
        m.Items.Add(Ui.Separator());

        var blank = Ui.Item(m.Items, "立即黑屏", false, delegate { ShowOverlay(); });
        blank.ShortcutKeyDisplayString = KeyCombo.Name(cfg.BlankMods, cfg.BlankVk);
        var off = Ui.Item(m.Items, "立即关闭显示器", false, delegate { PowerOffNow(); });
        if (cfg.PowerOffVk != 0) off.ShortcutKeyDisplayString = KeyCombo.Name(cfg.PowerOffMods, cfg.PowerOffVk);
        var icons = Ui.Item(m.Items, DesktopIcons.Visible ? "隐藏桌面图标" : "显示桌面图标", false, delegate { ToggleDesktopIcons(); });
        if (cfg.IconsVk != 0) icons.ShortcutKeyDisplayString = KeyCombo.Name(cfg.IconsMods, cfg.IconsVk);
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (flyout != null) flyout.Close();
        StopHooks();
        StopLocate(null);
        CursorHider.Restore();
        NightTint.Restore();
        HideWarning();
        if (dimmed && ddcDevice != null)
        {
            // 后台写入线程随进程一起结束, 这里同步恢复
            Ddc.SetPercent(ddcDevice, Ddc.Brightness, dimmedFrom);
            cfg.DimmedFrom = -1;
            cfg.Save();
        }
        if (osd != null) osd.Dispose();
        if (overlayVisible) HideOverlay();
        if (settings != null) settings.Close();
        tray.Remove();
        Log.Write("退出");
        base.ExitThreadCore();
    }
}
