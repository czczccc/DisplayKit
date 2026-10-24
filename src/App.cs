using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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
    public const string Version = "0.3.0";
    public const string Repo = "czczccc/OledGuard";
    public static string RepoUrl { get { return "https://github.com/" + Repo; } }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "OledGuard";
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
    Icon iconActive, iconPaused;
    bool taskbarLight;
    int lastTrayTry = Environment.TickCount;

    // 防烧屏状态 (只在 UI 线程, 含钩子回调, 访问)
    int lastActivity = Environment.TickCount; // 最后一次"有效"操作
    int lastRawInput = Environment.TickCount; // 最后一次任何输入(含抖动)
    int anchorX, anchorY;
    bool overlayVisible, dismissQueued;
    int overlayShownAt, monitorOffAt;
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
    string latestVersion, updateStatus;
    bool updateChecking;
    Icon formIcon;

    SettingsForm settings;
    IKeyCapture capture;
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
    public bool Paused { get { return DateTime.Now < pausedUntil; } }
    public DateTime PausedUntil { get { return pausedUntil; } }
    public IKeyCapture Capture { get { return capture; } set { capture = value; pendingModOnly = 0; } }

    public App(bool autostart, EventWaitHandle showSignal)
    {
        var h = invoker.Handle; // 强制创建句柄, 供 BeginInvoke 使用
        var p = Cursor.Position; anchorX = p.X; anchorY = p.Y;
        Log.Write("启动 v" + Version + (autostart ? " (开机自启)" : ""));

        Theme.Load();
        ToolStripManager.Renderer = new ModernRenderer();
        MakeIcons();
        try { formIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        tray.Icon = iconActive;
        tray.Menu = Ui.Menu();
        tray.Menu.Opening += (s, e) => { FillTrayMenu(); e.Cancel = false; };
        tray.LeftClick += OpenSettings;
        tray.BalloonClick += delegate { if (latestVersion != null) OpenUrl(RepoUrl + "/releases/latest"); };
        tray.TryAdd();

        // 再次运行 exe 时, 新进程会发信号让这里打开设置窗口
        ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { invoker.BeginInvoke((Action)OpenSettings); }, null, -1, false);

        mouseProc = MouseHook;
        keyProc = KeyHook;
        IntPtr mod = Native.GetModuleHandle(null);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
        keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);
        if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero) Log.Write("安装键鼠钩子失败, 错误码 " + Marshal.GetLastWin32Error());

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;

        if (IsAutoStart()) SetAutoStart(true); // exe 换过位置时更新自启路径
        RefreshDisplay();

        timer.Interval = 500;
        timer.Tick += delegate { Tick(); };
        timer.Start();
        Tick();

        if (!autostart) OpenSettings();
    }

    // ---------- 输入钩子 ----------

    bool WakeByMouse { get { return cfg.WakeMode == Settings.WakeAny; } }

    IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
            lastRawInput = Environment.TickCount;

            bool meaningful = false, isPress = false;
            if (msg == Native.WM_MOUSEMOVE)
            {
                long dx = x - anchorX, dy = y - anchorY, t = cfg.MoveThreshold;
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
                return new IntPtr(1); // 吞掉被拦截点击对应的抬起
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
                    // 黑屏期间的点击/滚轮一律不传给下面的程序, 防止误操作
                    if (WakeByMouse && Since(overlayShownAt) >= GraceMs) QueueDismiss();
                    if (isPress) swallowMouseUp.Add(msg + 1);
                    return new IntPtr(1);
                }
            }
            else if (meaningful)
            {
                anchorX = x; anchorY = y;
                lastActivity = Environment.TickCount;
            }
        }
        return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam, 0);
            lastRawInput = Environment.TickCount;
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool isMod = KeyCombo.IsModifier(vk);

            if (capture != null && capture.IsCapturing && !overlayVisible)
            {
                // 录入快捷键: 按下普通键时连同已按住的修饰键一起记录; 单独按下并松开修饰键则记录该修饰键
                var target = capture;
                if (down && !isMod)
                {
                    int mods = KeyCombo.CurrentMods(vk), v = KeyCombo.Normalize(vk);
                    pendingModOnly = 0;
                    invoker.BeginInvoke((Action)delegate { if (capture == target && target.IsCapturing) target.OnCaptured(mods, v); });
                    swallowKeyUp.Add(vk);
                    return new IntPtr(1);
                }
                if (down && isMod) pendingModOnly = KeyCombo.Normalize(vk);
                else if (!down && isMod && pendingModOnly == KeyCombo.Normalize(vk))
                {
                    int mods = KeyCombo.CurrentMods(vk), v = pendingModOnly;
                    pendingModOnly = 0;
                    invoker.BeginInvoke((Action)delegate { if (capture == target && target.IsCapturing) target.OnCaptured(mods, v); });
                }
                if (!down && swallowKeyUp.Remove(vk)) return new IntPtr(1);
            }
            else if (!down)
            {
                if (swallowKeyUp.Remove(vk)) return new IntPtr(1);
            }
            else if (overlayVisible)
            {
                if (Since(overlayShownAt) >= GraceMs &&
                    (cfg.WakeMode != Settings.WakeKey || KeyCombo.Matches(vk, KeyCombo.CurrentMods(vk), cfg.WakeVk, cfg.WakeMods)))
                    QueueDismiss();
                // 黑屏期间的按键不传给前台程序, 避免盲打误输入; 修饰键放行以免卡键/弹出开始菜单
                if (!isMod) { swallowKeyUp.Add(vk); return new IntPtr(1); }
            }
            else
            {
                Action hotkey = isMod ? null : FindHotkey(vk, KeyCombo.CurrentMods(vk));
                if (hotkey != null)
                {
                    invoker.BeginInvoke(hotkey);
                    swallowKeyUp.Add(vk);
                    return new IntPtr(1);
                }
                lastActivity = Environment.TickCount;
            }
        }
        return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
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
        if (KeyCombo.Matches(vk, mods, cfg.BlankVk, cfg.BlankMods)) return ShowOverlay;
        if (cfg.Profiles.Count > 0 && KeyCombo.Matches(vk, mods, cfg.ToggleVk, cfg.ToggleMods)) return ToggleProfile;
        foreach (var p in cfg.Profiles)
            if (KeyCombo.Matches(vk, mods, p.HotkeyVk, p.HotkeyMods))
            {
                var profile = p;
                return delegate { SwitchProfile(profile, null); };
            }
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
        if (overlayVisible) return;
        foreach (var s in BlankTargets())
        {
            var f = new BlankForm(s.Bounds);
            f.Show();
            f.KeepOnTop();
            overlays.Add(f);
        }
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
        string tip = "OledGuard\n防烧屏：" + guardStatus;
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

            // 第一次切到一个改变分辨率/刷新率的模式时让用户确认, 万一显示器没信号 15 秒后自动恢复
            bool modeChanged = s.Width != p.Width || s.Height != p.Height || (p.RefreshRate > 0 && Math.Abs(s.RefreshRate - p.RefreshRate) > 1);
            if (!p.Verified && modeChanged && autoReason == null) needConfirm = true;
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
        if (updateChecking) return;
        updateChecking = true;
        updateStatus = "正在检查…";
        ThreadPool.QueueUserWorkItem(delegate
        {
            string tag = null, error = null;
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers.Add("User-Agent", "OledGuard/" + Version);
                    string json = wc.DownloadString("https://api.github.com/repos/" + Repo + "/releases/latest");
                    var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                    if (m.Success) tag = m.Groups[1].Value;
                    else error = "没有找到版本信息";
                }
            }
            catch (Exception ex) { error = ex.Message; }
            invoker.BeginInvoke((Action)delegate { OnUpdateChecked(tag, error, manual); });
        });
    }

    void OnUpdateChecked(string tag, string error, bool manual)
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
            updateStatus = "发现新版本 v" + tag;
            if (first) tray.ShowBalloon("OledGuard 有新版本 v" + tag, "点击这里打开下载页面", TrayIcon.InfoInfo);
        }
        else
        {
            updateStatus = "已是最新版本";
        }
        if (settings != null) settings.OnUpdateChecked();
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
        if (overlayVisible) HideOverlay();
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
        if (settings != null) settings.Close();
        tray.Remove();
        Log.Write("退出");
        base.ExitThreadCore();
    }
}
