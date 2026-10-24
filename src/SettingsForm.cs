using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

// 设置窗口: 左侧导航, 右侧卡片式设置, 修改即时生效
class SettingsForm : Form, IKeyCapture
{
    enum Page { Guard, Display, General }
    const int TargetWake = 1, TargetBlank = 2, TargetToggle = 3;
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
    bool lastTrayOk;
    int lastProfileIndex = -2;

    public bool IsCapturing { get { return captureTarget != 0; } }

    static int S(int v) { return Ui.S(v); }
    int X { get { return S(36); } }
    int W { get { return S(560); } }

    public SettingsForm(App app)
    {
        this.app = app;
        Text = "OledGuard";
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

        int h = Math.Min(S(780), (int)(Screen.PrimaryScreen.WorkingArea.Height * 0.9));
        ClientSize = new Size(nav.Width + X * 2 + W + SystemInformation.VerticalScrollBarWidth, h);

        ApplyColors();
        BuildNav();
        Rebuild();
        Deactivate += delegate { if (captureTarget != 0) { captureTarget = 0; keyHint = null; Rebuild(); } };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindow(Handle);
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

        var logo = Ui.Text("OledGuard", Theme.Header, Theme.Text, Theme.Bg);
        logo.Bounds = new Rectangle(S(22), S(18), nav.Width - S(30), S(28));
        nav.Controls.Add(logo);
        var ver = Ui.Text("v" + App.Version + " · 防烧屏 + 显示模式", Theme.Small, Theme.SubText, Theme.Bg);
        ver.Bounds = new Rectangle(S(22), S(46), nav.Width - S(30), S(20));
        nav.Controls.Add(ver);

        int y = S(84);
        AddNav("\uE7F4", "防烧屏", Page.Guard, ref y);
        AddNav("\uE7F8", "显示模式", Page.Display, ref y);
        AddNav("\uE713", "通用", Page.General, ref y);
    }

    void AddNav(string glyph, string text, Page p, ref int y)
    {
        var b = new NavButton(glyph, text) { Selected = page == p, Bounds = new Rectangle(S(8), y, nav.Width - S(16), S(40)) };
        b.Click += delegate
        {
            if (page == p) return;
            page = p;
            captureTarget = 0;
            keyHint = null;
            foreach (var nb in navButtons) { nb.Selected = nb == b; nb.Invalidate(); }
            content.AutoScrollPosition = Point.Empty;
            Rebuild();
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
        lastProfileIndex = app.ProfileIndex;
        UpdateLive();
    }

    // 定时刷新的状态文字
    public void UpdateLive()
    {
        if (page == Page.Guard && lastTrayOk != app.TrayOk) { Rebuild(); return; }
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

    RoundButton Drop(string text, Func<ContextMenuStrip> factory, int minWidth)
    {
        var b = new RoundButton(text) { Chevron = true };
        b.FitWidth(minWidth);
        b.Click += delegate
        {
            var m = factory();
            m.Closed += delegate { BeginInvoke((Action)m.Dispose); };
            m.Show(b, new Point(0, b.Height + S(4)));
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
            keyHint = active ? null : target == TargetToggle ? "按下新的快捷键，按 Backspace 清除。" : "按下新的按键或组合键。";
            keyHintIsError = false;
            app.Capture = this;
            Later();
        };
        return b;
    }

    public void OnCaptured(int mods, int vk)
    {
        var cfg = app.Cfg;
        int t = captureTarget;
        if (t == 0) return;
        if (t == TargetToggle && mods == 0 && (vk == 0x08 || vk == 0x2E))
        {
            cfg.ToggleVk = 0;
            cfg.ToggleMods = 0;
        }
        else
        {
            string err = t == TargetWake ? null : KeyCombo.Validate(mods, vk);
            if (err != null)
            {
                keyHint = err;
                keyHintIsError = true;
                Rebuild();
                return;
            }
            if (t == TargetWake) { cfg.WakeVk = vk; cfg.WakeMods = mods; cfg.WakeMode = Settings.WakeKey; }
            else if (t == TargetBlank) { cfg.BlankVk = vk; cfg.BlankMods = mods; }
            else { cfg.ToggleVk = vk; cfg.ToggleMods = mods; }
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
        AddKeyHint(c2, TargetWake, TargetBlank);
        y = Section("唤醒", c2, y);

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
        return y;
    }

    ContextMenuStrip WhitelistMenu()
    {
        var cfg = app.Cfg;
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
                    if (n != "explorer" && n != "applicationframehost" && n != "textinputhost" && !cfg.Whitelist.Contains(n)) names.Add(n);
                }
            }
            catch { }
            finally { p.Dispose(); }
        }
        if (names.Count == 0) Ui.Item(m.Items, "没有可添加的程序", false, null).Enabled = false;
        foreach (var name in names)
        {
            string n = name;
            Ui.Item(m.Items, n + ".exe", false, delegate { cfg.Whitelist.Add(n); Changed(); });
        }
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
        AddKeyHint(c1, TargetToggle);
        y = Section("显示器", c1, y);

        var c2 = new Card(W);
        if (cfg.Profiles.Count == 0)
            c2.AddNote("还没有显示模式。点下面的「添加模式」，默认会填入现在的分辨率、刷新率和缩放。", Theme.SubText);
        for (int i = 0; i < cfg.Profiles.Count; i++)
        {
            var p = cfg.Profiles[i];
            int idx = i;
            bool isCurrent = i == app.ProfileIndex;
            string sub = p.Describe() + (p.HotkeyVk != 0 ? "   ·   " + KeyCombo.Name(p.HotkeyMods, p.HotkeyVk) : "");

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

        var add = Button("添加模式", true, delegate { EditProfile(-1, NewFromCurrent()); });
        add.Location = new Point(X, y - S(12));
        content.Controls.Add(add);
        var import = Button("导入 DisplayModeSwitch 配置", false, delegate { Import(); });
        import.Location = new Point(add.Right + S(8), y - S(12));
        content.Controls.Add(import);
        y += S(36);
        y += AddWrapped("也可以用命令行切换：OledGuard.exe <模式名> 或 OledGuard.exe --toggle，适合做成桌面快捷方式或给游戏启动器用。", Theme.Small, Theme.SubText, y).Height;
        return y;
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

    void EditProfile(int idx, DisplayProfile source)
    {
        var cfg = app.Cfg;
        var taken = new List<string>();
        for (int i = 0; i < cfg.Profiles.Count; i++)
            if (i != idx) taken.Add(cfg.Profiles[i].Name);

        using (var dlg = new ProfileEditor(app, source.Clone(), idx < 0, taken))
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
        if (MessageBox.Show(this, "删除模式「" + p.Name + "」？", "OledGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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
                MessageBox.Show(this, "已导入 " + n + " 个模式。", "OledGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            c1.AddRow("暂停防烧屏", "临时不自动黑屏，快捷键和显示模式不受影响", Drop("暂停", delegate
            {
                var m = Ui.Menu();
                foreach (int min in new[] { 30, 60, 120, 0 }) { int v = min; Ui.Item(m.Items, App.PauseName(v), false, delegate { app.Pause(v); Later(); }); }
                return m;
            }, S(120)));
        y = Section("运行", c1, y);

        var c2 = new Card(W);
        c2.AddRow("OledGuard v" + App.Version, "OLED 防烧屏 + 显示模式切换", Button("GitHub", false, delegate { Open("https://github.com/czczccc/OledGuard"); }));
        c2.AddRow("配置与日志", Settings.Dir, Button("打开文件夹", false, delegate
        {
            System.IO.Directory.CreateDirectory(Settings.Dir);
            Open(Settings.Dir);
        }));
        c2.AddRow("退出程序", "关闭本窗口不会退出，程序会在托盘继续运行", Button("退出", false, delegate { app.ExitThread(); }));
        y = Section("关于", c2, y);
        return y;
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("打开 " + target + " 失败: " + ex.Message); }
    }
}

// 添加 / 编辑显示模式
class ProfileEditor : Form, IKeyCapture
{
    readonly App app;
    readonly List<Resolution> resolutions;
    readonly List<string> taken;
    readonly bool isNew;
    public DisplayProfile Result;
    bool capturing;
    string hint;
    bool hintIsError;

    public bool IsCapturing { get { return capturing; } }

    static int S(int v) { return Ui.S(v); }
    int W { get { return S(460); } }

    public ProfileEditor(App app, DisplayProfile profile, bool isNew, List<string> taken)
    {
        this.app = app;
        this.isNew = isNew;
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

    RoundButton Drop(string text, Func<ContextMenuStrip> factory)
    {
        var b = new RoundButton(text) { Chevron = true };
        b.FitWidth(S(200));
        b.Click += delegate
        {
            var m = factory();
            m.Closed += delegate { BeginInvoke((Action)m.Dispose); };
            m.Show(b, new Point(0, b.Height + S(4)));
        };
        return b;
    }

    void Build()
    {
        string typed = Result.Name;
        var old = new List<Control>();
        foreach (Control c in Controls) old.Add(c);
        Controls.Clear();
        foreach (var c in old) c.Dispose();

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
        }));

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
        }));

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
        }));

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
        y += card.Height + S(20);

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
