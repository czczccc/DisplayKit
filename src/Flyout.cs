using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

// 左键托盘图标弹出的快捷面板 (类似 Twinkle Tray / Windows 快速设置): 亮度、显示模式、常用开关; 点到别处就关闭
class Flyout : Form
{
    readonly App app;
    int shownBrightness;  // 面板上显示的亮度, 读到新值时据此决定要不要重建
    bool touched;         // 用户动过滑块, 之后读到的值不要覆盖

    static int S(int v) { return Ui.S(v); }
    int W { get { return S(360); } }

    public Flyout(App app)
    {
        this.app = app;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Build();
        Deactivate += delegate { Close(); };
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW: 不出现在 Alt+Tab 里
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindow(Handle); // 圆角 + 深色
    }

    // 贴着任务栏、靠近鼠标 (托盘图标) 的位置弹出
    public void ShowAt(Point cursor)
    {
        var screen = Screen.FromPoint(cursor);
        var wa = screen.WorkingArea;
        int gap = S(12);
        int x = cursor.X > wa.Right ? wa.Right - Width - gap
              : cursor.X < wa.Left ? wa.Left + gap
              : Math.Max(wa.Left + gap, Math.Min(cursor.X - Width / 2, wa.Right - Width - gap));
        int y = cursor.Y > wa.Bottom ? wa.Bottom - Height - gap
              : cursor.Y < wa.Top ? wa.Top + gap
              : Math.Max(wa.Top + gap, Math.Min(cursor.Y - Height / 2, wa.Bottom - Height - gap));
        Location = new Point(x, y);
        Show();
        Activate();
        Native.SetForegroundWindow(Handle); // 拿到焦点, 点到别处时才会触发 Deactivate 关闭
    }

    public void Rebuild()
    {
        var old = new List<Control>();
        foreach (Control c in Controls) old.Add(c);
        Controls.Clear();
        foreach (var c in old) c.Dispose();
        Build();
    }

    // 读到显示器亮度后: 之前显示"读不到"或数值变了, 并且用户没在拖滑块, 才刷新
    public void OnDdcRead()
    {
        if (!touched && shownBrightness != app.Brightness) Rebuild();
    }

    void Build()
    {
        BackColor = Theme.Bg;
        Font = Theme.Font;
        int pad = S(16), w = W - pad * 2, y = S(14);

        // 标题: 显示器名称 + 当前模式, 右侧齿轮打开设置
        var gear = new RoundButton("\uE713") { Font = Theme.Icons };
        gear.Size = new Size(S(36), S(32));
        gear.Location = new Point(W - pad - gear.Width, y + S(2));
        gear.Click += delegate { Close(); app.OpenSettings(); };
        Controls.Add(gear);
        var title = Ui.Text(MonitorName(), Theme.Bold, Theme.Text, Theme.Bg);
        title.Bounds = new Rectangle(pad, y, w - gear.Width - S(8), S(22));
        Controls.Add(title);
        var d = app.Display;
        string sub = d == null ? "无法读取当前显示模式" : d.Describe();
        if (d != null && app.ProfileIndex >= 0) sub = app.Cfg.Profiles[app.ProfileIndex].Name + " · " + sub;
        var subLabel = Ui.Text(sub, Theme.Small, Theme.SubText, Theme.Bg);
        subLabel.Bounds = new Rectangle(pad, y + S(22), w - gear.Width - S(8), S(18));
        Controls.Add(subLabel);
        y += S(52);

        // 亮度 (DDC/CI)
        // 亮度 (DDC/CI): 放在单独的卡片里
        shownBrightness = app.Brightness;
        var module = new Card(w) { Location = new Point(pad, y) };
        var head = Ui.Text("显示器亮度", Theme.Bold, Theme.Text, Theme.Card);
        head.Bounds = new Rectangle(S(14), S(10), w - S(28), S(22));
        module.Controls.Add(head);
        if (app.Brightness >= 0)
        {
            var slider = new Slider(app.Brightness, w - S(20), "\uE706");
            slider.Location = new Point(S(6), S(36));
            slider.ValueChanged += delegate { touched = true; app.SetBrightnessFromUser(slider.Value); };
            module.Controls.Add(slider);
            module.Height = S(80);
        }
        else
        {
            var note = Ui.Text(app.DdcReading ? "正在读取…" : "读不到（需要 DDC/CI），稍后自动重试", Theme.Small, Theme.SubText, Theme.Card);
            note.Bounds = new Rectangle(S(14), S(34), w - S(28), S(20));
            module.Controls.Add(note);
            module.Height = S(66);
        }
        Controls.Add(module);
        y += module.Height + S(14);

        // 显示模式
        var cfg = app.Cfg;
        if (cfg.Profiles.Count > 0)
        {
            y = Heading("显示模式", y);
            var tiles = new List<Tile>();
            for (int i = 0; i < cfg.Profiles.Count; i++)
            {
                var p = cfg.Profiles[i];
                tiles.Add(MakeTile("\uE7F4", p.Name, i == app.ProfileIndex, true, delegate { app.SwitchProfile(p, null); }));
            }
            y = Grid(tiles, y);
        }

        // 常用开关
        y = Heading("快捷操作", y);
        var quick = new List<Tile>();
        if (app.HdrState != null) quick.Add(MakeTile("\uE790", "HDR", app.HdrState.Value, true, delegate { app.ToggleHdr(); }));
        quick.Add(MakeTile("\uE708", "深色模式", !SystemTheme.IsLight(), false, delegate { app.ToggleTheme(); }));
        quick.Add(MakeTile("\uE769", app.Paused ? "已暂停" : "暂停防烧屏", app.Paused, false, delegate { if (app.Paused) app.Resume(); else app.Pause(30); }));
        quick.Add(MakeTile("\uED1A", "隐藏图标", !DesktopIcons.Visible, false, delegate { app.ToggleDesktopIcons(); }));
        quick.Add(MakeTile("\uE793", "立即黑屏", false, true, delegate { app.BlankNow(); }));
        quick.Add(MakeTile("\uE7E8", "关闭显示器", false, true, delegate { app.PowerOffNow(); }));
        y = Grid(quick, y);

        ClientSize = new Size(W, y + S(4));
    }

    string MonitorName()
    {
        var d = app.Display;
        try
        {
            if (d != null)
                foreach (var m in DisplayController.ListDisplays())
                    if (string.Equals(m.Device, d.Device, StringComparison.OrdinalIgnoreCase)) return m.Name;
        }
        catch { }
        return "DisplayKit";
    }

    int Heading(string text, int y)
    {
        var l = Ui.Text(text, Theme.Small, Theme.SubText, Theme.Bg);
        l.Bounds = new Rectangle(S(16), y, W - S(32), S(20));
        Controls.Add(l);
        return y + S(24);
    }

    // closes: 点完关闭面板 (会改变画面或盖住屏幕的操作), 否则刷新面板显示新状态
    Tile MakeTile(string glyph, string text, bool on, bool closes, Action action)
    {
        var t = new Tile(glyph, text, on);
        t.Click += delegate
        {
            if (closes) Close();
            action();
            if (!closes && !IsDisposed) BeginInvoke((Action)Rebuild);
        };
        return t;
    }

    // 每行 3 个
    int Grid(List<Tile> tiles, int y)
    {
        int pad = S(16), gap = S(10), cols = 3;
        int tw = (W - pad * 2 - gap * (cols - 1)) / cols, th = S(74);
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            t.Size = new Size(tw, th);
            t.Location = new Point(pad + (i % cols) * (tw + gap), y + (i / cols) * (th + gap));
            Controls.Add(t);
        }
        int rows = (tiles.Count + cols - 1) / cols;
        return y + rows * (th + gap) + S(8);
    }
}
