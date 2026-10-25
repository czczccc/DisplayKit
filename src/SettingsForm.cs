using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 设置窗口: 左侧导航, 右侧卡片式设置, 修改即时生效
class SettingsForm : Form, IKeyCapture
{
    enum Page { Guard, Display, General }
    const int TargetWake = 1, TargetBlank = 2, TargetToggle = 3, TargetPowerOff = 4, TargetIcons = 5, TargetHdr = 6, TargetBrightUp = 7, TargetBrightDown = 8;
    static readonly int[] DimAfters = { 0, 30, 60, 120, 300 };
    static readonly int[] Percents = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
    static readonly int[] CursorHides = { 0, 3, 5, 10, 30 };
    static readonly int[] Warns = { 0, 3, 5, 10 };
    static readonly int[] NightKelvins = { 0, 5500, 5000, 4500, 4000 };
    static readonly int[] Timeouts = { 30, 60, 120, 180, 300, 600, 900, 1800 };
    static readonly int[] PowerOffs = { 0, 1, 5, 15, 30, 60 };
    static readonly int[] Thresholds = { 20, 60, 150, 300 };

    readonly App app;
    readonly Panel nav, content;
    readonly List<NavButton> navButtons = new List<NavButton>();
    Page page = Page.Guard;
    int captureTarget;
    string keyHint;
    bool keyHintIsError;
    Label liveStatus, liveDisplay;
    bool lastTrayOk, lastIconsVisible;
    int lastProfileIndex = -2;

    public bool IsCapturing { get { return captureTarget != 0; } }

    static int S(int v) { return Ui.S(v); }
    int X { get { return S(36); } }
    int W { get { return S(560); } }

    public SettingsForm(App app)
    {
        this.app = app;
        Text = "DisplayKit";
        Icon = app.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = Theme.Font;

        nav = new Panel { Dock = DockStyle.Left, Width = S(220) };
        content = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        content.HandleCreated += delegate { Theme.ThemeScrollbars(content); };
        Controls.Add(content);
        Controls.Add(nav);

        ApplySize(Screen.FromPoint(Cursor.Position));
        ApplyColors();
        BuildNav();
        Rebuild();
        Deactivate += delegate { if (captureTarget != 0) { captureTarget = 0; keyHint = null; Rebuild(); } };
    }

    void ApplySize(Screen screen)
    {
        Font = Theme.Font;
        nav.Width = S(220);
        int h = Math.Min(S(780), (int)(screen.WorkingArea.Height * 0.9));
        ClientSize = new Size(nav.Width + X * 2 + W + S(17), h); // 预留滚动条宽度
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindow(Handle);
    }

    // 显示器缩放改变或窗口拖到另一块缩放不同的屏幕: 按新缩放重新排版
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED)
        {
            var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
            Ui.SetDpi(m.WParam.ToInt32() & 0xFFFF);
            Location = new Point(r.Left, r.Top);
            ApplySize(Screen.FromPoint(new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)));
            BuildNav();
            Rebuild();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    public void RefreshGeneral()
    {
        if (page == Page.General && captureTarget == 0) Rebuild();
    }

    // 读到显示器的亮度 / 输入源后刷新显示模式页 (亮度相关的设置也在防烧屏页和外观里)
    public void OnDdcRead()
    {
        if (captureTarget == 0) Rebuild();
    }

    void ApplyColors()
    {
        BackColor = Theme.Bg;
        nav.BackColor = Theme.Bg;
        content.BackColor = Theme.Bg;
    }

    public void ApplyTheme()
    {
        ApplyColors();
        Theme.ApplyWindow(Handle);
        Theme.ThemeScrollbars(content);
        BuildNav();
        Rebuild();
    }

    // ---------- 导航 ----------

    void BuildNav()
    {
        var old = new List<Control>();
        foreach (Control c in nav.Controls) old.Add(c);
        nav.Controls.Clear();
        foreach (var c in old) c.Dispose();
        navButtons.Clear();

        var logo = Ui.Text("DisplayKit", Theme.Header, Theme.Text, Theme.Bg);
        logo.Bounds = new Rectangle(S(22), S(18), nav.Width - S(30), S(28));
        nav.Controls.Add(logo);
        var ver = Ui.Text("v" + App.Version + " · 防烧屏 + 显示模式", Theme.Small, Theme.SubText, Theme.Bg);
        ver.Bounds = new Rectangle(S(22), S(46), nav.Width - S(30), S(20));
        nav.Controls.Add(ver);

        int y = S(84);
        AddNav("\uE7F4", "防烧屏", Page.Guard, ref y);
        AddNav("\uE7F8", "显示模式", Page.Display, ref y);
        AddNav("\uE713", "通用", Page.General, ref y);

        float top = PillAt(navButtons[(int)page]);
        SetPill(top, top + S(16));
    }

    // 选中项左侧的强调色竖条 (Windows 11 设置的样式). 位置用导航面板坐标, 由各个按钮画出自己范围内的那一段
    float PillAt(NavButton b) { return b.Top + (b.Height - S(16)) / 2f; }

    void SetPill(float top, float bottom)
    {
        foreach (var nb in navButtons)
        {
            nb.PillTop = top - nb.Top;
            nb.PillBottom = bottom - nb.Top;
            nb.Invalidate();
        }
    }

    // 滑到新选项: 前端先到、后端跟上, 中途竖条被拉长再收回
    void MovePill(NavButton from, NavButton to)
    {
        float a = PillAt(from), z = PillAt(to), h = S(16);
        bool downward = z > a;
        Anim.Run(nav, "pill", 0, 1, 320, Anim.Linear, t =>
        {
            float lead = Anim.Decelerate(Math.Min(1, t * 1.6f)), trail = Anim.Decelerate(t);
            SetPill(a + (z - a) * (downward ? trail : lead), a + h + (z - a) * (downward ? lead : trail));
        });
    }

    void AddNav(string glyph, string text, Page p, ref int y)
    {
        var b = new NavButton(glyph, text) { Selected = page == p, Bounds = new Rectangle(S(8), y, nav.Width - S(16), S(40)) };
        b.Click += delegate
        {
            if (page == p) return;
            var from = navButtons[(int)page];
            page = p;
            captureTarget = 0;
            keyHint = null;
            foreach (var nb in navButtons) { nb.Selected = nb == b; nb.Invalidate(); }
            content.AutoScrollPosition = Point.Empty;
            Rebuild();
            MovePill(from, b); // 重建页面要占用 UI 线程一会儿, 之后再开始动画, 不会一开始就掉帧
        };
        nav.Controls.Add(b);
        navButtons.Add(b);
        y += S(44);
    }

    // ---------- 页面 ----------

    void Later() { BeginInvoke((Action)Rebuild); } // 在事件处理完后再重建, 避免销毁正在处理事件的控件

    public void Rebuild()
    {
        int scrollY = -content.AutoScrollPosition.Y;
        content.SuspendLayout();
        content.AutoScrollPosition = Point.Empty;
        var old = new List<Control>();
        foreach (Control c in content.Controls) old.Add(c);
        content.Controls.Clear();
        foreach (var c in old) c.Dispose();
        liveStatus = null;
        liveDisplay = null;

        int y = S(28);
        if (page == Page.Guard) y = BuildGuard(y);
        else if (page == Page.Display) y = BuildDisplay(y);
        else y = BuildGeneral(y);

        // 撑开滚动区域的底部留白
        content.Controls.Add(new Label { Bounds = new Rectangle(X, y + S(16), 1, 1), BackColor = Theme.Bg });
        content.ResumeLayout();
        content.AutoScrollPosition = new Point(0, scrollY);
        lastTrayOk = app.TrayOk;
        lastIconsVisible = DesktopIcons.Visible;
        lastProfileIndex = app.ProfileIndex;
        UpdateLive();
    }

    // 定时刷新的状态文字
    public void UpdateLive()
    {
        if (page == Page.Guard && (lastTrayOk != app.TrayOk || lastIconsVisible != DesktopIcons.Visible) && captureTarget == 0) { Rebuild(); return; }
        if (page == Page.Display && lastProfileIndex != app.ProfileIndex && captureTarget == 0) { Rebuild(); return; }
        if (liveStatus != null) SetText(liveStatus, "当前状态：" + app.GuardStatus);
        if (liveDisplay != null)
        {
            var d = app.Display;
            string text = d == null ? "无法读取当前显示模式" : "当前：" + d.Describe();
            if (d != null && app.ProfileIndex >= 0) text += "（" + app.Cfg.Profiles[app.ProfileIndex].Name + "）";
            SetText(liveDisplay, text);
        }
    }

    static void SetText(Label l, string text) { if (l.Text != text) l.Text = text; }

    Label AddText(string text, Font font, Color color, int y, int h)
    {
        var l = Ui.Text(text, font, color, Theme.Bg);
        l.Bounds = new Rectangle(X, y, W, h);
        content.Controls.Add(l);
        return l;
    }

    Label AddWrapped(string text, Font font, Color color, int y)
    {
        int h = TextRenderer.MeasureText(text, font, new Size(W, 0), TextFormatFlags.WordBreak).Height + S(4);
        var l = AddText(text, font, color, y, h);
        l.AutoEllipsis = false;
        l.TextAlign = ContentAlignment.TopLeft;
        return l;
    }

    int Section(string title, Card card, int y)
    {
        AddText(title, Theme.Bold, Theme.Text, y, S(24));
        y += S(30);
        card.Location = new Point(X, y);
        content.Controls.Add(card);
        return y + card.Height + S(28);
    }

    RoundButton Button(string text, bool primary, EventHandler click)
    {
        var b = new RoundButton(text) { Primary = primary };
        b.FitWidth(S(72));
        b.Click += click;
        return b;
    }

    ContextMenuStrip lastMenu;

    RoundButton Drop(string text, Func<ContextMenuStrip> factory, int minWidth)
    {
        var b = new RoundButton(text) { Chevron = true };
        b.FitWidth(minWidth);
        b.Click += delegate
        {
            // 上一个菜单到这时才释放: 菜单项里可能弹出对话框, 关闭后立刻释放会出问题
            if (lastMenu != null) lastMenu.Dispose();
            lastMenu = factory();
            lastMenu.Show(b, new Point(0, b.Height + S(4)));
        };
        return b;
    }

    Toggle Switch(bool value, Action<bool> set)
    {
        var t = new Toggle(value);
        t.Changed += delegate { set(t.Checked); app.Save(); };
        return t;
    }

    void Changed()
    {
        app.Save();
        Later();
    }

    RoundButton KeyButton(int target, int mods, int vk)
    {
        bool active = captureTarget == target;
        string text = active ? "请按下按键…" : vk == 0 ? "未设置" : KeyCombo.Name(mods, vk);
        var b = new RoundButton(text) { Highlight = active };
        b.FitWidth(S(150));
        b.Click += delegate
        {
            captureTarget = active ? 0 : target;
            keyHint = active ? null : Clearable(target) ? "按下新的快捷键，按 Backspace 清除。" : "按下新的按键或组合键。";
            keyHintIsError = false;
            app.Capture = this;
            Later();
        };
        return b;
    }

    // 可以清除 (不设快捷键) 的项
    static bool Clearable(int target) { return target != TargetWake && target != TargetBlank; }

    static string HotkeyKey(int target)
    {
        switch (target)
        {
            case TargetBlank: return "blank";
            case TargetPowerOff: return "poweroff";
            case TargetIcons: return "icons";
            case TargetHdr: return "hdr";
            case TargetBrightUp: return "brightup";
            case TargetBrightDown: return "brightdown";
        }
        return "toggle";
    }

    void SetHotkey(int target, int mods, int vk)
    {
        var cfg = app.Cfg;
        switch (target)
        {
            case TargetWake: cfg.WakeVk = vk; cfg.WakeMods = mods; cfg.WakeMode = Settings.WakeKey; break;
            case TargetBlank: cfg.BlankVk = vk; cfg.BlankMods = mods; break;
            case TargetPowerOff: cfg.PowerOffVk = vk; cfg.PowerOffMods = mods; break;
            case TargetIcons: cfg.IconsVk = vk; cfg.IconsMods = mods; break;
            case TargetHdr: cfg.HdrVk = vk; cfg.HdrMods = mods; break;
            case TargetBrightUp: cfg.BrightUpVk = vk; cfg.BrightUpMods = mods; break;
            case TargetBrightDown: cfg.BrightDownVk = vk; cfg.BrightDownMods = mods; break;
            default: cfg.ToggleVk = vk; cfg.ToggleMods = mods; break;
        }
    }

    public void OnCaptured(int mods, int vk)
    {
        int t = captureTarget;
        if (t == 0) return;
        if (Clearable(t) && mods == 0 && (vk == 0x08 || vk == 0x2E))
        {
            SetHotkey(t, 0, 0);
        }
        else
        {
            string err = null;
            if (t != TargetWake)
            {
                err = KeyCombo.Validate(mods, vk);
                string owner = err == null ? app.HotkeyOwner(mods, vk, HotkeyKey(t), null) : null;
                if (owner != null) err = "这个快捷键已被「" + owner + "」使用，请换一个。";
            }
            if (err != null)
            {
                keyHint = err;
                keyHintIsError = true;
                Rebuild();
                return;
            }
            SetHotkey(t, mods, vk);
        }
        captureTarget = 0;
        keyHint = null;
        app.Save();
        Rebuild();
    }

    void AddKeyHint(Card card, params int[] targets)
    {
        if (keyHint == null) return;
        if (captureTarget != 0 && Array.IndexOf(targets, captureTarget) < 0) return;
        card.AddNote(keyHint, keyHintIsError ? Theme.Warn : Theme.SubText);
    }

    // ----- 防烧屏 -----

    static string TimeName(int sec) { return sec < 60 ? sec + " 秒" : sec / 60 + " 分钟"; }
    static string PowerOffName(int min) { return min == 0 ? "不关闭" : "黑屏 " + min + " 分钟后"; }

    static string ThresholdName(int px)
    {
        switch (px)
        {
            case 20: return "低 · 20 像素";
            case 60: return "中 · 60 像素";
            case 150: return "高 · 150 像素";
            case 300: return "很高 · 300 像素";
        }
        return px + " 像素";
    }

    static string WakeName(int mode)
    {
        if (mode == Settings.WakeKey) return "仅指定按键";
        if (mode == Settings.WakeAnyKey) return "键盘任意键";
        return "任意操作";
    }

    int BuildGuard(int y)
    {
        var cfg = app.Cfg;

        var blank = Button("立即黑屏", true, delegate { app.BlankNow(); });
        blank.FitWidth(S(104));
        blank.Location = new Point(X + W - blank.Width, y + S(8));
        content.Controls.Add(blank);
        AddText("防烧屏", Theme.Title, Theme.Text, y, S(44)).Width = W - blank.Width - S(16);
        y += S(46);
        liveStatus = AddText("", Theme.Font, Theme.SubText, y, S(24));
        y += S(30);
        if (!app.TrayOk)
            y += AddWrapped("托盘图标未能显示（原因见「通用 → 打开配置文件夹」里的 log.txt）。程序仍在正常工作，再次运行 exe 可打开本窗口。", Theme.Small, Theme.Warn, y).Height + S(8);
        y += S(10);

        var c1 = new Card(W);
        c1.AddRow("无操作后黑屏", "期间没有有效的键盘、鼠标或手柄操作", Drop(TimeName(cfg.TimeoutSec), delegate
        {
            var m = Ui.Menu();
            foreach (int sec in Timeouts) { int v = sec; Ui.Item(m.Items, TimeName(v), cfg.TimeoutSec == v, delegate { cfg.TimeoutSec = v; Changed(); }); }
            return m;
        }, S(150)));
        c1.AddRow("黑屏后关闭显示器", "很多 OLED 显示器只在待机时执行像素刷新", Drop(PowerOffName(cfg.PowerOffMin), delegate
        {
            var m = Ui.Menu();
            foreach (int min in PowerOffs) { int v = min; Ui.Item(m.Items, PowerOffName(v), cfg.PowerOffMin == v, delegate { cfg.PowerOffMin = v; Changed(); }); }
            return m;
        }, S(150)));
        c1.AddRow("鼠标防抖", "鼠标移动超过这个距离才算在使用，过滤传感器抖动", Drop(ThresholdName(cfg.MoveThreshold), delegate
        {
            var m = Ui.Menu();
            foreach (int px in Thresholds) { int v = px; Ui.Item(m.Items, ThresholdName(v), cfg.MoveThreshold == v, delegate { cfg.MoveThreshold = v; Changed(); }); }
            return m;
        }, S(150)));
        c1.AddRow("黑屏前提示", "屏幕逐渐变暗并倒计时，动一下就取消", Drop(WarnName(cfg.WarnSec), delegate
        {
            var m = Ui.Menu();
            foreach (int sec in Warns) { int v = sec; Ui.Item(m.Items, WarnName(v), cfg.WarnSec == v, delegate { cfg.WarnSec = v; Changed(); }); }
            return m;
        }, S(150)));
        c1.AddRow("闲置时先降低亮度", app.Brightness < 0 ? "需要显示器支持 DDC/CI（见「显示模式」页）" : "黑屏之前先调暗显示器背光，有操作立即恢复",
            Drop(DimName(cfg.DimAfterSec), delegate
            {
                var m = Ui.Menu();
                foreach (int sec in DimAfters) { int v = sec; Ui.Item(m.Items, DimName(v), cfg.DimAfterSec == v, delegate { cfg.DimAfterSec = v; Changed(); }).Enabled = v == 0 || v < cfg.TimeoutSec; }
                return m;
            }, S(150)));
        if (cfg.DimAfterSec > 0)
            c1.AddRow("降到", cfg.DimAfterSec >= cfg.TimeoutSec ? "要早于黑屏时间才会生效" : null, PercentDrop(cfg.DimLevel, false, v => cfg.DimLevel = v));
        if (Screen.AllScreens.Length > 1)
            c1.AddRow("黑屏的显示器", "只黑 OLED 屏，其他屏幕照常显示；关闭显示器会关掉全部屏幕", Drop(BlankDevicesName(), BlankDevicesMenu, S(150)));
        c1.AddRow("锁屏后关闭显示器", "锁屏界面也是静态画面，本程序无法覆盖", Drop(LockOffName(cfg.LockScreenOffSec), delegate
        {
            var m = Ui.Menu();
            foreach (int sec in LockOffs) { int v = sec; Ui.Item(m.Items, LockOffName(v), cfg.LockScreenOffSec == v, delegate { cfg.LockScreenOffSec = v; Changed(); }); }
            return m;
        }, S(150)));
        y = Section("黑屏", c1, y);

        var c2 = new Card(W);
        c2.AddRow("唤醒方式", "黑屏期间的按键和点击不会传给后面的程序", Drop(WakeName(cfg.WakeMode), delegate
        {
            var m = Ui.Menu();
            foreach (int mode in new[] { Settings.WakeKey, Settings.WakeAnyKey, Settings.WakeAny })
            {
                int v = mode;
                Ui.Item(m.Items, WakeName(v) + (v == Settings.WakeAny ? "（按键 / 点击 / 大幅移动鼠标 / 手柄）" : ""), cfg.WakeMode == v, delegate { cfg.WakeMode = v; Changed(); });
            }
            return m;
        }, S(150)));
        c2.AddRow("唤醒按键", cfg.WakeMode == Settings.WakeKey ? "支持单键或组合键，比如空格、Ctrl+Shift+F12" : "唤醒方式为「仅指定按键」时使用",
            KeyButton(TargetWake, cfg.WakeMods, cfg.WakeVk));
        c2.AddRow("立即黑屏快捷键", "和唤醒按键设成同一个，就能一键切换黑屏/唤醒", KeyButton(TargetBlank, cfg.BlankMods, cfg.BlankVk));
        c2.AddRow("立即关闭显示器快捷键", "先黑屏再关闭显示器，离开前按一下；也在托盘菜单里", KeyButton(TargetPowerOff, cfg.PowerOffMods, cfg.PowerOffVk));
        AddKeyHint(c2, TargetWake, TargetBlank, TargetPowerOff);
        y = Section("唤醒与快捷键", c2, y);

        var c4 = new Card(W);
        c4.AddRow("鼠标指针闲置隐藏", "鼠标不动时隐藏指针，移动鼠标立即恢复", Drop(CursorHideName(cfg.CursorHideSec), delegate
        {
            var m = Ui.Menu();
            foreach (int sec in CursorHides) { int v = sec; Ui.Item(m.Items, CursorHideName(v), cfg.CursorHideSec == v, delegate { cfg.CursorHideSec = v; Changed(); }); }
            return m;
        }, S(150)));
        bool iconsVisible = DesktopIcons.Visible;
        c4.AddRow("桌面图标", iconsVisible ? "桌面图标是长期不动的画面，可以隐藏" : "已隐藏，桌面右键「查看」里也能恢复",
            Button(iconsVisible ? "隐藏" : "显示", false, delegate { app.ToggleDesktopIcons(); Later(); }));
        c4.AddRow("显示/隐藏桌面图标快捷键", "也在托盘菜单里", KeyButton(TargetIcons, cfg.IconsMods, cfg.IconsVk));
        AddKeyHint(c4, TargetIcons);
        y = Section("减少静态画面", c4, y);

        var c3 = new Card(W);
        c3.AddRow("全屏应用", "看电影、玩游戏时", Switch(cfg.SkipFullscreen, v => cfg.SkipFullscreen = v));
        c3.AddRow("正在播放声音", "窗口化视频、直播、音乐；挂着音乐离开时也不会黑屏", Switch(cfg.SkipAudio, v => cfg.SkipAudio = v));
        c3.AddRow("手柄操作", "Xbox 等 XInput 手柄的按键和摇杆算作在使用", Switch(cfg.Gamepad, v => cfg.Gamepad = v));
        c3.AddRow("白名单程序", "这些程序在前台时不黑屏", Drop("添加程序", WhitelistMenu, S(120)));
        foreach (var name in cfg.Whitelist)
        {
            string n = name;
            c3.AddRow(n + ".exe", null, Button("移除", false, delegate { cfg.Whitelist.Remove(n); Changed(); }));
        }
        y = Section("这些情况不黑屏", c3, y);

        var c5 = new Card(W);
        c5.AddRow("按时间段使用不同的规则", "比如深夜缩短黑屏等待时间，或某些时段不自动黑屏", Switch(cfg.ScheduleOn, v => { cfg.ScheduleOn = v; Later(); }));
        if (cfg.ScheduleOn)
        {
            string range = HourName(cfg.ScheduleStart) + " 到" + (cfg.ScheduleEnd <= cfg.ScheduleStart ? "次日 " : " ") + HourName(cfg.ScheduleEnd);
            c5.AddRow("时段", cfg.ScheduleStart == cfg.ScheduleEnd ? "开始和结束不能相同" : range,
                Ui.Group(HourDrop(cfg.ScheduleStart, v => cfg.ScheduleStart = v), HourDrop(cfg.ScheduleEnd, v => cfg.ScheduleEnd = v)));
            c5.AddRow("这个时段内", app.InSchedule(DateTime.Now) ? "现在正在这个时段内" : "其他时间按上面的设置", Drop(ScheduleName(cfg.ScheduleTimeoutSec), delegate
            {
                var m = Ui.Menu();
                Ui.Item(m.Items, ScheduleName(0), cfg.ScheduleTimeoutSec == 0, delegate { cfg.ScheduleTimeoutSec = 0; Changed(); });
                foreach (int sec in Timeouts) { int v = sec; Ui.Item(m.Items, ScheduleName(v), cfg.ScheduleTimeoutSec == v, delegate { cfg.ScheduleTimeoutSec = v; Changed(); }); }
                return m;
            }, S(150)));
        }
        y = Section("时段规则", c5, y);
        return y;
    }

    static string WarnName(int sec) { return sec == 0 ? "不提示" : "提前 " + sec + " 秒"; }
    static string DimName(int sec) { return sec == 0 ? "不降低" : TimeName(sec) + "后"; }

    // 亮度百分比下拉; allowKeep = 有"不改"选项 (-1)
    RoundButton PercentDrop(int value, bool allowKeep, Action<int> set)
    {
        return Drop(value < 0 ? "不改" : value + "%", delegate
        {
            var m = Ui.Menu();
            if (allowKeep) Ui.Item(m.Items, "不改", value < 0, delegate { set(-1); Changed(); });
            foreach (int p in Percents) { int v = p; Ui.Item(m.Items, v + "%", value == v, delegate { set(v); Changed(); }); }
            return m;
        }, S(150));
    }
    static string HourName(int h) { return h.ToString("00") + ":00"; }
    static string ScheduleName(int sec) { return sec == 0 ? "不自动黑屏" : TimeName(sec) + "后黑屏"; }

    RoundButton HourDrop(int value, Action<int> set)
    {
        return Drop(HourName(value), delegate
        {
            var m = Ui.Menu();
            for (int h = 0; h < 24; h++) { int v = h; Ui.Item(m.Items, HourName(v), value == v, delegate { set(v); Changed(); }); }
            return m;
        }, S(90));
    }

    static string CursorHideName(int sec) { return sec == 0 ? "不隐藏" : sec + " 秒后"; }

    static readonly int[] LockOffs = { 0, 3, 10, 60, 300 };

    static string LockOffName(int sec)
    {
        if (sec == 0) return "不关闭";
        if (sec <= 3) return "立即";
        return sec < 60 ? sec + " 秒后" : sec / 60 + " 分钟后";
    }

    string BlankDevicesName()
    {
        var list = app.Cfg.BlankDevices;
        if (list.Count == 0) return "全部显示器";
        if (list.Count > 1) return list.Count + " 台显示器";
        foreach (var d in SafeDisplays())
            if (string.Equals(d.Device, list[0], StringComparison.OrdinalIgnoreCase)) return d.Name;
        return list[0].Replace(@"\\.\", "");
    }

    ContextMenuStrip BlankDevicesMenu()
    {
        var list = app.Cfg.BlankDevices;
        var m = Ui.Menu();
        Ui.Item(m.Items, "全部显示器", list.Count == 0, delegate { list.Clear(); Changed(); });
        m.Items.Add(Ui.Separator());
        foreach (var d in SafeDisplays())
        {
            string dev = d.Device;
            bool on = list.Exists(x => string.Equals(x, dev, StringComparison.OrdinalIgnoreCase));
            Ui.Item(m.Items, d.Name + (d.Primary ? "（主）" : "") + "  " + dev.Replace(@"\\.\", ""), on, delegate
            {
                // 从"全部"开始点某一台: 只选这一台; 否则切换勾选
                if (on) list.RemoveAll(x => string.Equals(x, dev, StringComparison.OrdinalIgnoreCase));
                else list.Add(dev);
                Changed();
            });
        }
        return m;
    }

    static List<DisplayInfo> SafeDisplays()
    {
        try { return DisplayController.ListDisplays(); }
        catch { return new List<DisplayInfo>(); }
    }

    ContextMenuStrip WhitelistMenu()
    {
        var cfg = app.Cfg;
        return ProgramPicker(cfg.Whitelist, n => { cfg.Whitelist.Add(n); Changed(); }, this);
    }

    // 列出有窗口的正在运行的程序, 外加"浏览"选择 exe
    public static ContextMenuStrip ProgramPicker(List<string> exclude, Action<string> pick, IWin32Window owner)
    {
        var m = Ui.Menu();
        var names = new SortedSet<string>();
        int self = Process.GetCurrentProcess().Id;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id != self && p.MainWindowHandle != IntPtr.Zero)
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    if (n != "explorer" && n != "applicationframehost" && n != "textinputhost" && n != "systemsettings" && !exclude.Contains(n)) names.Add(n);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var name in names)
        {
            string n = name;
            Ui.Item(m.Items, n + ".exe", false, delegate { pick(n); });
        }
        if (names.Count > 0) m.Items.Add(Ui.Separator());
        Ui.Item(m.Items, "浏览 exe 文件…", false, delegate
        {
            using (var ofd = new OpenFileDialog { Title = "选择程序", Filter = "程序 (*.exe)|*.exe" })
            {
                if (ofd.ShowDialog(owner) != DialogResult.OK) return;
                string n = Path.GetFileNameWithoutExtension(ofd.FileName).ToLowerInvariant();
                if (!exclude.Contains(n)) pick(n);
            }
        });
        return m;
    }

    // ----- 显示模式 -----

    int BuildDisplay(int y)
    {
        var cfg = app.Cfg;
        AddText("显示模式", Theme.Title, Theme.Text, y, S(44));
        y += S(46);
        liveDisplay = AddText("", Theme.Font, Theme.SubText, y, S(24));
        y += S(40);

        List<DisplayInfo> displays;
        try { displays = DisplayController.ListDisplays(); }
        catch { displays = new List<DisplayInfo>(); }
        string current = "主显示器";
        foreach (var d in displays)
            if (string.Equals(d.Device, cfg.DisplayDevice, StringComparison.OrdinalIgnoreCase)) current = d.Name;

        var c1 = new Card(W);
        c1.AddRow("要切换的显示器", displays.Count > 1 ? "多台显示器时选择由哪一台切换" : null, Drop(current, delegate
        {
            var m = Ui.Menu();
            Ui.Item(m.Items, "主显示器（自动）", cfg.DisplayDevice.Length == 0, delegate { cfg.DisplayDevice = ""; Changed(); });
            foreach (var d in displays)
            {
                var dev = d.Device;
                string label = d.Name + (d.Primary ? "（主）" : "") + "  " + dev.Replace(@"\\.\", "");
                Ui.Item(m.Items, label, string.Equals(dev, cfg.DisplayDevice, StringComparison.OrdinalIgnoreCase), delegate { cfg.DisplayDevice = dev; Changed(); });
            }
            return m;
        }, S(180)));
        c1.AddRow("循环切换快捷键", "按顺序在下面的模式之间切换", KeyButton(TargetToggle, cfg.ToggleMods, cfg.ToggleVk));
        if (app.HdrState != null)
        {
            c1.AddRow("HDR", app.HdrState.Value ? "已开启；每个模式也可以设置切换时开或关 HDR" : "已关闭；每个模式也可以设置切换时开或关 HDR",
                Button(app.HdrState.Value ? "关闭" : "开启", false, delegate { app.ToggleHdr(); Later(); }));
            c1.AddRow("开关 HDR 快捷键", "托盘菜单里也可以开关", KeyButton(TargetHdr, cfg.HdrMods, cfg.HdrVk));
            bool calibInstalled = HdrCalibration.Installed;
            c1.AddRow("HDR 校准", calibInstalled ? "用微软的 HDR 校准应用调暗部、亮部和饱和度，需要先开启 HDR" : "需要先从 Microsoft Store 安装微软的「Windows HDR Calibration」",
                Button(calibInstalled ? "打开校准" : "去安装", false, delegate { HdrCalibration.Open(); }));
            c1.AddRow("HDR 系统设置", "常开 HDR 时，在这里调「SDR 内容亮度」让桌面亮度合适", Button("打开", false, delegate { App.OpenUrl("ms-settings:display-hdr"); }));
        }
        AddKeyHint(c1, TargetToggle, TargetHdr);
        y = Section("显示器", c1, y);
        y = BuildHardware(y);

        var c2 = new Card(W);
        if (cfg.Profiles.Count == 0)
            c2.AddNote("还没有显示模式。点下面的「添加模式」，默认会填入现在的分辨率、刷新率和缩放。", Theme.SubText);
        for (int i = 0; i < cfg.Profiles.Count; i++)
        {
            var p = cfg.Profiles[i];
            int idx = i;
            bool isCurrent = i == app.ProfileIndex;
            string sub = p.Describe() + (p.HotkeyVk != 0 ? "   ·   " + KeyCombo.Name(p.HotkeyMods, p.HotkeyVk) : "");
            if (p.AutoApps.Count > 0) sub += "   ·   自动：" + string.Join("、", p.AutoApps.ToArray());

            var apply = Button(isCurrent ? "当前" : "应用", !isCurrent, delegate { app.SwitchProfile(p, this); Later(); });
            apply.Enabled = !isCurrent;
            var edit = Button("编辑", false, delegate { EditProfile(idx, p); });
            var more = Drop("更多", delegate
            {
                var m = Ui.Menu();
                Ui.Item(m.Items, "上移", false, delegate { MoveProfile(idx, -1); }).Enabled = idx > 0;
                Ui.Item(m.Items, "下移", false, delegate { MoveProfile(idx, 1); }).Enabled = idx < cfg.Profiles.Count - 1;
                m.Items.Add(Ui.Separator());
                Ui.Item(m.Items, "删除", false, delegate { DeleteProfile(idx); });
                return m;
            }, S(72));
            var title = c2.AddRow(p.Name, sub, Ui.Group(apply, edit, more));
            if (isCurrent) { title.ForeColor = Theme.Accent; title.Font = Theme.Bold; }
        }
        y = Section("模式", c2, y);

        var add = Drop("添加模式", delegate
        {
            var m = Ui.Menu();
            Ui.Item(m.Items, "从当前设置添加…", false, delegate { EditProfile(-1, NewFromCurrent()); });
            m.Items.Add(Ui.Separator());
            foreach (var preset in Settings.BuiltInProfiles())
            {
                var p = preset;
                Ui.Item(m.Items, "内置：" + p.Name + " · " + p.Describe(), false, delegate { EditProfile(-1, FromPreset(p)); });
            }
            return m;
        }, S(120));
        add.Primary = true;
        add.Location = new Point(X, y - S(12));
        content.Controls.Add(add);
        var import = Button("导入 DisplayModeSwitch 配置", false, delegate { Import(); });
        import.Location = new Point(add.Right + S(8), y - S(12));
        content.Controls.Add(import);
        y += S(36);
        y += AddWrapped("也可以用命令行切换：DisplayKit.exe <模式名> 或 DisplayKit.exe --toggle，适合做成桌面快捷方式或给游戏启动器用。", Theme.Small, Theme.SubText, y).Height;
        return y;
    }

    // 显示器自身的设置 (DDC/CI): 亮度、对比度、输入源, 和按显示器菜单键一样
    int BuildHardware(int y)
    {
        var cfg = app.Cfg;
        var c = new Card(W);
        if (app.Brightness < 0)
            c.AddNote(app.DdcReading ? "正在读取显示器的亮度…"
                : "读不到这台显示器的亮度。需要在显示器菜单里打开 DDC/CI；部分转接线、扩展坞和 VGA / DVI 连接不支持。", Theme.SubText);
        else
        {
            var bright = new Slider(app.Brightness, S(260), null);
            bright.ValueChanged += delegate { app.SetBrightness(bright.Value, false); };
            c.AddRow("亮度", "显示器本身的背光亮度", bright);
            if (app.Contrast >= 0)
            {
                var contrast = new Slider(app.Contrast, S(260), null);
                contrast.ValueChanged += delegate { app.SetContrast(contrast.Value); };
                c.AddRow("对比度", null, contrast);
            }
            if (app.Inputs != null)
                c.AddRow("输入源", "切到其他输入后，这台电脑的画面会消失", Drop(app.Input >= 0 ? Ddc.InputName(app.Input) : "未知", delegate
                {
                    var m = Ui.Menu();
                    foreach (int v in app.Inputs)
                    {
                        int value = v;
                        Ui.Item(m.Items, Ddc.InputName(value), value == app.Input, delegate
                        {
                            if (value != app.Input && app.ConfirmSwitchInput(this, value)) { app.SwitchInput(value); Later(); }
                        });
                    }
                    return m;
                }, S(160)));
        }
        c.AddRow("调亮快捷键", "每次 10%，屏幕下方会显示当前亮度", KeyButton(TargetBrightUp, cfg.BrightUpMods, cfg.BrightUpVk));
        c.AddRow("调暗快捷键", "托盘菜单里也有亮度滑块", KeyButton(TargetBrightDown, cfg.BrightDownMods, cfg.BrightDownVk));
        AddKeyHint(c, TargetBrightUp, TargetBrightDown);
        return Section("显示器亮度与输入源（DDC/CI）", c, y);
    }

    DisplayProfile NewFromCurrent()
    {
        var d = app.Display;
        var p = new DisplayProfile();
        if (d != null) { p.Width = d.Width; p.Height = d.Height; p.RefreshRate = d.RefreshRate; p.Scale = d.Scale; }
        for (int n = 1; ; n++)
        {
            string name = "模式 " + n;
            if (app.Cfg.FindProfile(name) == null) { p.Name = name; break; }
        }
        return p;
    }

    // 内置模式: 重名时加序号, 快捷键已被占用就不带快捷键
    DisplayProfile FromPreset(DisplayProfile p)
    {
        var cfg = app.Cfg;
        string baseName = p.Name;
        for (int n = 2; cfg.FindProfile(p.Name) != null; n++) p.Name = baseName + " " + n;
        if (app.HotkeyOwner(p.HotkeyMods, p.HotkeyVk, null, null) != null) { p.HotkeyVk = 0; p.HotkeyMods = 0; }
        return p;
    }

    void EditProfile(int idx, DisplayProfile source)
    {
        var cfg = app.Cfg;
        var taken = new List<string>();
        for (int i = 0; i < cfg.Profiles.Count; i++)
            if (i != idx) taken.Add(cfg.Profiles[i].Name);

        using (var dlg = new ProfileEditor(app, source.Clone(), idx >= 0 ? cfg.Profiles[idx] : null, taken))
        {
            app.Capture = dlg;
            var result = dlg.ShowDialog(this);
            app.Capture = this;
            if (result != DialogResult.OK) return;
            if (idx >= 0) cfg.Profiles[idx] = dlg.Result;
            else cfg.Profiles.Add(dlg.Result);
        }
        app.Save();
        Rebuild();
    }

    void MoveProfile(int idx, int delta)
    {
        var list = app.Cfg.Profiles;
        int j = idx + delta;
        if (j < 0 || j >= list.Count) return;
        var t = list[idx]; list[idx] = list[j]; list[j] = t;
        Changed();
    }

    void DeleteProfile(int idx)
    {
        var p = app.Cfg.Profiles[idx];
        if (MessageBox.Show(this, "删除模式「" + p.Name + "」？", "DisplayKit", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        app.Cfg.Profiles.RemoveAt(idx);
        Changed();
    }

    void Import()
    {
        using (var ofd = new OpenFileDialog { Title = "选择 DisplayModeSwitch.ini", Filter = "DisplayModeSwitch 配置 (*.ini)|*.ini|所有文件|*.*" })
        {
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                int n = app.Cfg.ImportDisplayModeSwitch(ofd.FileName);
                app.Save();
                Rebuild();
                MessageBox.Show(this, "已导入 " + n + " 个模式。", "DisplayKit", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "导入失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // ----- 通用 -----

    int BuildGeneral(int y)
    {
        AddText("通用", Theme.Title, Theme.Text, y, S(44));
        y += S(64);

        var c1 = new Card(W);
        c1.AddRow("开机自启动", "登录 Windows 后在托盘后台运行", Switch(App.IsAutoStart(), v => App.SetAutoStart(v)));
        if (app.Paused)
            c1.AddRow("防烧屏已暂停", app.PausedUntil == DateTime.MaxValue ? "直到手动恢复" : "到 " + app.PausedUntil.ToString("HH:mm") + " 自动恢复",
                Button("恢复", true, delegate { app.Resume(); Later(); }));
        else
            c1.AddRow("暂停防烧屏", "看视频时临时不自动黑屏，到时间自动恢复", Drop("暂停", delegate
            {
                var m = Ui.Menu();
                foreach (int min in new[] { 30, 60, 120, 0 }) { int v = min; Ui.Item(m.Items, App.PauseName(v), false, delegate { app.Pause(v); Later(); }); }
                return m;
            }, S(120)));
        y = Section("运行", c1, y);
        y = BuildAppearance(y);

        var cfg = app.Cfg;
        var c2 = new Card(W);
        c2.AddRow("自动检查更新", "每天检查一次 GitHub 上的新版本", Switch(cfg.CheckUpdates, v => { cfg.CheckUpdates = v; Later(); }));
        if (cfg.CheckUpdates)
            c2.AddRow("自动安装更新", "新版本发布满一天后，在你不操作电脑时自动安装并重启", Switch(cfg.AutoInstallUpdates, v => cfg.AutoInstallUpdates = v));
        if (app.LatestVersion != null)
        {
            var install = Button(app.Updating ? "更新中…" : "立即更新", true, delegate { app.InstallUpdate(); Later(); });
            install.Enabled = !app.Updating;
            var t = c2.AddRow("发现新版本 v" + app.LatestVersion, app.Updating || (app.UpdateStatus ?? "").StartsWith("更新失败") ? app.UpdateStatus : "当前版本 v" + App.Version, install);
            t.ForeColor = Theme.Accent;
            c2.AddRow("更新内容", "在 GitHub 上查看这个版本改了什么", Button("查看", false, delegate { App.OpenUrl(App.RepoUrl + "/releases/latest"); }));
        }
        else
        {
            var check = Button(app.UpdateChecking ? "检查中…" : "检查更新", false, delegate { app.CheckForUpdate(true); Later(); });
            check.Enabled = !app.UpdateChecking;
            c2.AddRow("DisplayKit v" + App.Version, app.UpdateStatus ?? "OLED 防烧屏 + 显示模式切换", check);
        }
        y = Section("更新", c2, y);

        var c3 = new Card(W);
        c3.AddRow("项目主页", App.RepoUrl.Replace("https://", ""), Button("GitHub", false, delegate { App.OpenUrl(App.RepoUrl); }));
        c3.AddRow("配置与日志", Settings.Dir, Button("打开文件夹", false, delegate
        {
            Directory.CreateDirectory(Settings.Dir);
            App.OpenUrl(Settings.Dir);
        }));
        c3.AddRow("退出程序", "关闭本窗口不会退出，程序会在托盘继续运行", Button("退出", false, delegate { app.ExitThread(); }));
        y = Section("关于", c3, y);
        return y;
    }

    bool wallpaperOpen;           // "顺便换壁纸"展开了
    string coordText, coordError; // 坐标输入框最近一次提交的内容 (重建窗口时输入框失去焦点也会提交, 用它去重) 和错误提示

    // 外观按用途分三张卡片: 深色/浅色、晚上护眼、日出日落时间 (前两张里的自动功能都按它切换)
    int BuildAppearance(int y)
    {
        var cfg = app.Cfg;
        var theme = new Card(W);
        // 关掉再打开开关会立即按当前时间切一次, 所以不再单独放"按时间切换"按钮
        theme.AddRow("跟随日出日落切换", "白天浅色、晚上深色；手动切过后，到下次日出或日落再自动切",
            Switch(cfg.AutoTheme, v => { cfg.AutoTheme = v; app.ApplySunTheme(); Later(); }));
        // 壁纸用得少, 平时收成一行, 点开才显示两张壁纸的设置
        int set = (cfg.LightWallpaper.Length > 0 ? 1 : 0) + (cfg.DarkWallpaper.Length > 0 ? 1 : 0);
        theme.AddRow("顺便换壁纸", set == 0 ? "没设置" : set == 2 ? "浅色、深色各一张" : cfg.LightWallpaper.Length > 0 ? "只设了浅色时的" : "只设了深色时的",
            Button(wallpaperOpen ? "收起" : "设置…", false, delegate { wallpaperOpen = !wallpaperOpen; Later(); }));
        if (wallpaperOpen)
        {
            WallpaperRow(theme, "浅色时的壁纸", cfg.LightWallpaper, v => cfg.LightWallpaper = v);
            WallpaperRow(theme, "深色时的壁纸", cfg.DarkWallpaper, v => cfg.DarkWallpaper = v);
        }
        y = Section("深色 / 浅色", theme, y);

        var night = new Card(W);
        string tintSub = app.HdrState == true ? "现在开着 HDR，调黄不起作用"
            : app.NightTintFailed ? "显示器不接受调色曲线"
            : "看着不刺眼；Windows 自带的「夜间模式」也是调黄，二选一";
        night.AddRow("晚上把屏幕调黄", tintSub,
            Drop(KelvinName(cfg.NightKelvin), delegate
            {
                var m = Ui.Menu();
                foreach (int k in NightKelvins) { int v = k; Ui.Item(m.Items, KelvinName(v), cfg.NightKelvin == v, delegate { cfg.NightKelvin = v; Changed(); }); }
                return m;
            }, S(150)));
        night.AddRow("白天、晚上自动调亮度", app.Brightness < 0 ? "需要显示器支持 DDC/CI（见「显示模式」页）" : "调显示器本身的亮度；手动调过后，到下次日出或日落再调",
            Switch(cfg.AutoBrightness, v => { cfg.AutoBrightness = v; app.ApplySunBrightness(); Later(); }));
        if (cfg.AutoBrightness)
        {
            night.AddRow("白天亮度", null, PercentDrop(cfg.DayBrightness, false, v => { cfg.DayBrightness = v; app.ApplySunBrightness(); }));
            night.AddRow("晚上亮度", null, PercentDrop(cfg.NightBrightness, false, v => { cfg.NightBrightness = v; app.ApplySunBrightness(); }));
        }
        y = Section("晚上护眼", night, y);

        // 上面的自动功能都没开就用不到位置和日出日落时间, 整张卡片不显示
        if (cfg.NeedsLocation)
        {
            var c = new Card(W);
            c.AddRow(SunSummary(), cfg.HasLocation ? "上面开着的自动功能按这两个时间切换" : "有了位置才能算出日出日落时间", null);
            var source = Drop(cfg.UseSystemLocation ? "Windows 定位" : "手动输入", delegate
            {
                var m = Ui.Menu();
                Ui.Item(m.Items, "Windows 定位", cfg.UseSystemLocation, delegate { cfg.UseSystemLocation = true; app.Locate(); Changed(); });
                Ui.Item(m.Items, "手动输入坐标", !cfg.UseSystemLocation, delegate { cfg.UseSystemLocation = false; coordText = null; coordError = null; Changed(); });
                return m;
            }, S(130));
            if (cfg.UseSystemLocation)
            {
                string status = app.Locating ? "正在定位…" : app.LocationStatus
                    ?? (cfg.HasLocation ? "已定位：" + Settings.FormatCoord(cfg.Latitude) + ", " + Settings.FormatCoord(cfg.Longitude) : "还没有定位");
                var relocate = Button("重新定位", false, delegate { app.Locate(); Later(); });
                relocate.Enabled = !app.Locating;
                c.AddRow("位置", status, Ui.Group(source, relocate));
                if (!app.Locating && app.LocationStatus != null)
                    c.AddRow("Windows 定位设置", "需要打开「定位服务」和「允许桌面应用访问你的位置」", Button("打开", false, delegate { App.OpenUrl("ms-settings:privacy-location"); }));
            }
            else
            {
                c.AddRow("位置", "不用定位，按下面填的坐标算", source);
                string shown = coordError != null ? coordText : cfg.HasLocation ? Settings.FormatCoord(cfg.Latitude) + ", " + Settings.FormatCoord(cfg.Longitude) : "";
                var field = new TextField(shown, S(200));
                field.Box.LostFocus += delegate { CommitCoord(field.Box.Text); };
                field.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; CommitCoord(field.Box.Text); } };
                c.AddRow("坐标", "纬度, 经度，比如上海是 31.23, 121.47", field);
                if (coordError != null) c.AddNote(coordError, Theme.Warn);
            }
            y = Section("日出日落时间", c, y);
        }
        return y;
    }

    static string KelvinName(int k)
    {
        switch (k)
        {
            case 0: return "关闭";
            case 5500: return "微暖 · 5500K";
            case 5000: return "较暖 · 5000K";
            case 4500: return "暖 · 4500K";
            case 4000: return "很暖 · 4000K";
        }
        return k + "K";
    }

    // 深浅色切换时 (自动或手动) 换成对应的壁纸
    void WallpaperRow(Card c, string title, string path, Action<string> set)
    {
        string sub = path.Length == 0 ? "没选，不换壁纸" : File.Exists(path) ? Path.GetFileName(path) : "文件不见了：" + Path.GetFileName(path);
        var pick = Button("选择…", false, delegate
        {
            using (var ofd = new OpenFileDialog { Title = "选择" + title, Filter = "图片 (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp" })
            {
                if (path.Length > 0 && File.Exists(path)) ofd.InitialDirectory = Path.GetDirectoryName(path);
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                set(ofd.FileName);
            }
            app.Save();
            app.ApplyWallpaper();
            Later();
        });
        Control right = pick;
        if (path.Length > 0) right = Ui.Group(pick, Button("清除", false, delegate { set(""); Changed(); }));
        c.AddRow(title, sub, right);
    }

    void CommitCoord(string text)
    {
        text = text.Trim();
        if (text == coordText) return;
        coordText = text;
        var cfg = app.Cfg;
        double lat, lon;
        if (text.Length == 0) coordError = null;
        else if (!Settings.ParseLocation(text, out lat, out lon)) coordError = "坐标格式不对：纬度在 -90 到 90 之间，经度在 -180 到 180 之间，用逗号分开。";
        else
        {
            coordError = null;
            if (lat == cfg.Latitude && lon == cfg.Longitude) return;
            cfg.Latitude = lat;
            cfg.Longitude = lon;
            app.Save();
            app.ApplySunTheme();
        }
        Later();
    }

    string SunSummary()
    {
        var cfg = app.Cfg;
        if (!cfg.HasLocation) return "还没有位置信息";
        DateTime rise, set;
        bool polarDay;
        if (!Sun.Times(DateTime.Now, cfg.Latitude, cfg.Longitude, out rise, out set, out polarDay))
            return polarDay ? "今天极昼，全天浅色" : "今天极夜，全天深色";
        return "今天日出 " + rise.ToString("HH:mm") + " · 日落 " + set.ToString("HH:mm");
    }
}

// 添加 / 编辑显示模式
class ProfileEditor : Form, IKeyCapture
{
    readonly App app;
    readonly List<Resolution> resolutions;
    readonly List<string> taken;
    readonly bool isNew;
    readonly DisplayProfile original; // 被编辑的模式本身, 新建时为 null
    public DisplayProfile Result;
    bool capturing;
    string hint;
    bool hintIsError;

    public bool IsCapturing { get { return capturing; } }

    static int S(int v) { return Ui.S(v); }
    int W { get { return S(480); } }

    public ProfileEditor(App app, DisplayProfile profile, DisplayProfile original, List<string> taken)
    {
        this.app = app;
        this.original = original;
        isNew = original == null;
        this.taken = taken;
        Result = profile;
        Text = isNew ? "添加模式" : "编辑模式";
        Icon = app.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Bg;
        Font = Theme.Font;

        try { resolutions = DisplayController.ListResolutions(DisplayController.ResolveDevice(app.Cfg.DisplayDevice)); }
        catch { resolutions = new List<Resolution>(); }
        Build();
        Deactivate += delegate { if (capturing) { capturing = false; hint = null; Later(); } };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindow(Handle);
    }

    void Later() { BeginInvoke((Action)Build); }

    Resolution CurrentResolution()
    {
        foreach (var r in resolutions)
            if (r.Width == Result.Width && r.Height == Result.Height) return r;
        return null;
    }

    ContextMenuStrip lastMenu;

    RoundButton Drop(string text, Func<ContextMenuStrip> factory, int minWidth)
    {
        var b = new RoundButton(text) { Chevron = true };
        b.FitWidth(minWidth);
        b.Click += delegate
        {
            if (lastMenu != null) lastMenu.Dispose();
            lastMenu = factory();
            lastMenu.Show(b, new Point(0, b.Height + S(4)));
        };
        return b;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DPICHANGED)
        {
            var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
            Ui.SetDpi(m.WParam.ToInt32() & 0xFFFF);
            Location = new Point(r.Left, r.Top);
            Build();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    void Build()
    {
        string typed = Result.Name;
        var old = new List<Control>();
        foreach (Control c in Controls) old.Add(c);
        Controls.Clear();
        foreach (var c in old) c.Dispose();

        Font = Theme.Font;
        int x = S(24), y = S(20);
        var card = new Card(W);

        var name = new TextField(typed, S(200));
        name.Box.TextChanged += delegate { Result.Name = name.Box.Text.Trim(); };
        card.AddRow("名称", "也可以在命令行里用名称切换", name);

        card.AddRow("分辨率", null, Drop(Result.Width + " × " + Result.Height, delegate
        {
            var m = Ui.Menu();
            foreach (var r in resolutions)
            {
                var res = r;
                Ui.Item(m.Items, r.Width + " × " + r.Height, r.Width == Result.Width && r.Height == Result.Height, delegate
                {
                    Result.Width = res.Width;
                    Result.Height = res.Height;
                    if (Result.RefreshRate > 0 && !res.Rates.Contains(Result.RefreshRate)) Result.RefreshRate = res.Rates.Count > 0 ? res.Rates[0] : 0;
                    Later();
                });
            }
            if (resolutions.Count == 0) Ui.Item(m.Items, "读取不到显示器支持的分辨率", false, null).Enabled = false;
            return m;
        }, S(200)));

        card.AddRow("刷新率", null, Drop(Result.RefreshRate > 0 ? Result.RefreshRate + " Hz" : "不改", delegate
        {
            var m = Ui.Menu();
            Ui.Item(m.Items, "不改", Result.RefreshRate == 0, delegate { Result.RefreshRate = 0; Later(); });
            var r = CurrentResolution();
            if (r != null)
                foreach (int hz in r.Rates)
                {
                    int v = hz;
                    Ui.Item(m.Items, hz + " Hz", Result.RefreshRate == hz, delegate { Result.RefreshRate = v; Later(); });
                }
            return m;
        }, S(200)));

        card.AddRow("缩放", "和 Windows 显示设置里的缩放相同", Drop(Result.Scale > 0 ? Result.Scale + "%" : "不改", delegate
        {
            var m = Ui.Menu();
            Ui.Item(m.Items, "不改", Result.Scale == 0, delegate { Result.Scale = 0; Later(); });
            foreach (int pct in DisplayController.DpiValues)
            {
                int v = pct;
                Ui.Item(m.Items, pct + "%", Result.Scale == pct, delegate { Result.Scale = v; Later(); });
            }
            return m;
        }, S(200)));

        card.AddRow("HDR", "切换到这个模式时开启或关闭 HDR", Drop(HdrName(Result.Hdr), delegate
        {
            var m = Ui.Menu();
            foreach (int h in new[] { DisplayProfile.HdrKeep, DisplayProfile.HdrOn, DisplayProfile.HdrOff })
            {
                int v = h;
                Ui.Item(m.Items, HdrName(v), Result.Hdr == v, delegate { Result.Hdr = v; Later(); });
            }
            return m;
        }, S(200)));

        card.AddRow("亮度", "切换时把显示器背光调到这个亮度（需要 DDC/CI）", Drop(Result.Brightness < 0 ? "不改" : Result.Brightness + "%", delegate
        {
            var m = Ui.Menu();
            Ui.Item(m.Items, "不改", Result.Brightness < 0, delegate { Result.Brightness = -1; Later(); });
            for (int p = 0; p <= 100; p += 10) { int v = p; Ui.Item(m.Items, v + "%", Result.Brightness == v, delegate { Result.Brightness = v; Later(); }); }
            return m;
        }, S(200)));

        var key = new RoundButton(capturing ? "请按下按键…" : Result.HotkeyVk == 0 ? "未设置" : KeyCombo.Name(Result.HotkeyMods, Result.HotkeyVk)) { Highlight = capturing };
        key.FitWidth(S(200));
        key.Click += delegate
        {
            capturing = !capturing;
            hint = capturing ? "按下新的快捷键，按 Backspace 清除。" : null;
            hintIsError = false;
            Later();
        };
        card.AddRow("快捷键", "可选", key);
        if (hint != null) card.AddNote(hint, hintIsError ? Theme.Warn : Theme.SubText);

        card.Location = new Point(x, y);
        Controls.Add(card);
        y += card.Height + S(16);

        // 自动切换: 关联程序运行时切到这个模式
        var auto = new Card(W);
        auto.AddRow("运行这些程序时自动切换", "程序退出后切回原来的模式，比如关联游戏的 exe",
            Drop("添加程序", delegate { return SettingsForm.ProgramPicker(Result.AutoApps, n => { Result.AutoApps.Add(n); Later(); }, this); }, S(140)));
        foreach (var exe in Result.AutoApps)
        {
            string n = exe;
            var remove = new RoundButton("移除");
            remove.FitWidth(S(72));
            remove.Click += delegate { Result.AutoApps.Remove(n); Later(); };
            auto.AddRow(n + ".exe", null, remove);
        }
        auto.Location = new Point(x, y);
        Controls.Add(auto);
        y += auto.Height + S(20);

        var cancel = new RoundButton("取消");
        cancel.FitWidth(S(96));
        cancel.Location = new Point(x + W - cancel.Width, y);
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        var save = new RoundButton(isNew ? "添加" : "保存") { Primary = true };
        save.FitWidth(S(96));
        save.Location = new Point(cancel.Left - S(8) - save.Width, y);
        save.Click += delegate { TrySave(); };
        Controls.Add(save);
        Controls.Add(cancel);
        y += save.Height + S(20);

        ClientSize = new Size(W + x * 2, y);
    }

    static string HdrName(int h) { return h == DisplayProfile.HdrOn ? "开启" : h == DisplayProfile.HdrOff ? "关闭" : "不改"; }

    void TrySave()
    {
        string err = null;
        string n = Result.Name;
        if (n.Length == 0) err = "请填写名称。";
        else if (n.StartsWith("-")) err = "名称不能以 - 开头（会和命令行参数混淆）。";
        else if (n.IndexOfAny(new[] { '[', ']', '\r', '\n' }) >= 0) err = "名称里不能有 [ ]。";
        else if (taken.Exists(t => string.Equals(t, n, StringComparison.OrdinalIgnoreCase))) err = "已经有叫「" + n + "」的模式了。";
        else if (Result.Width <= 0 || Result.Height <= 0) err = "请选择分辨率。";
        if (err != null)
        {
            hint = err;
            hintIsError = true;
            Build();
            return;
        }
        // 分辨率或刷新率改过, 下次切换时重新确认能否正常显示
        if (original == null || original.Width != Result.Width || original.Height != Result.Height || original.RefreshRate != Result.RefreshRate)
            Result.Verified = false;
        DialogResult = DialogResult.OK;
        Close();
    }

    public void OnCaptured(int mods, int vk)
    {
        if (!capturing) return;
        if (mods == 0 && (vk == 0x08 || vk == 0x2E))
        {
            Result.HotkeyVk = 0;
            Result.HotkeyMods = 0;
        }
        else
        {
            string err = KeyCombo.Validate(mods, vk);
            string owner = err == null ? app.HotkeyOwner(mods, vk, null, original) : null;
            if (owner != null) err = "这个快捷键已被「" + owner + "」使用，请换一个。";
            if (err != null)
            {
                hint = err;
                hintIsError = true;
                Build();
                return;
            }
            Result.HotkeyVk = vk;
            Result.HotkeyMods = mods;
        }
        capturing = false;
        hint = null;
        Build();
    }
}
