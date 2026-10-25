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
        Deactivate += delegate
        {
            IntPtr fg = Native.GetForegroundWindow();
            // 焦点跑到了动画替身上 (按说不会, 以防万一): 抢回来, 不关面板
            if (ghost != null && fg == ghost.Handle) { Activate(); return; }
            if (unchecked(Environment.TickCount - shownAt) < 500)
            {
                var cls = new System.Text.StringBuilder(64);
                if (fg != IntPtr.Zero) Native.GetClassName(fg, cls, cls.Capacity);
                Log.Write("快捷面板刚打开就失去焦点, 当前前台窗口: " + (fg == IntPtr.Zero ? "无" : cls.ToString()));
            }
            FadeClose();
        };
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) FadeClose(); };
    }

    bool closing;
    int shownAt;        // 打开的时间, 用来发现"刚打开就被关掉"的情况
    int slide;          // 滑动方向和距离: 任务栏在下面时为正 (从下往上滑出)
    GhostWindow ghost;  // 动画期间代替面板显示的截图

    // 动画: 真面板截一张图做成逐像素透明的分层窗口, 动画时只移动这张图 (显卡合成, 不重绘几十个控件, 不闪);
    // 动画结束再换回真面板. 帧由 Anim 跟显示器刷新同步驱动

    // 关闭: 截图替身往任务栏方向退出并淡出 (点到别处、Esc、再点一次托盘图标)
    public void FadeClose()
    {
        if (closing || IsDisposed) return;
        closing = true;
        if (!Anim.Enabled || !Visible) { Close(); return; }
        var pos = Location;
        ShowGhost(pos, 1);
        Hide(); // 真面板立刻藏起来, 由替身做退出动画
        Anim.Run(this, "show", 0, 1, 160, Anim.Linear, t =>
        {
            if (ghost == null) return;
            float e = Anim.Accelerate(t);
            ghost.Present(pos.X, pos.Y + (int)Math.Round(slide * 0.5f * e), 1 - e);
        }, delegate { DisposeGhost(); Close(); });
    }

    void ShowGhost(Point pos, float opacity)
    {
        DisposeGhost();
        int pad;
        using (var shot = Snapshot(out pad)) ghost = new GhostWindow(shot, pad);
        ghost.Present(pos.X, pos.Y, opacity);
    }

    void DisposeGhost()
    {
        if (ghost == null) return;
        ghost.Dispose();
        ghost = null;
    }

    // 面板截图, 加上和 Windows 11 弹出面板相近的圆角、细边框和柔和阴影 (四周留出 pad 画阴影)
    Bitmap Snapshot(out int pad)
    {
        pad = S(20);
        var bmp = new Bitmap(Width + pad * 2, Height + pad * 2, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var raw = new Bitmap(Width, Height))
        using (var g = Graphics.FromImage(bmp))
        {
            DrawToBitmap(raw, new Rectangle(0, 0, Width, Height));
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = new RectangleF(pad, pad, Width, Height);
            float r = S(8);
            // 一圈圈往外扩、越往外越淡, 稍微往下偏
            for (int i = pad; i >= 1; i--)
                using (var path = Theme.RoundRect(new RectangleF(rect.X - i, rect.Y - i + i / 3f, rect.Width + i * 2, rect.Height + i * 2), r + i))
                using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 6 : 3, 0, 0, 0)))
                    g.FillPath(b, path);
            using (var path = Theme.RoundRect(rect, r))
            {
                using (var tb = new TextureBrush(raw))
                {
                    tb.TranslateTransform(pad, pad);
                    g.FillPath(tb, path);
                }
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
        }
        return bmp;
    }

    // 动画结束: 显示真面板 (去掉分层样式, Windows 11 才会给它加圆角和阴影), 再撤掉替身
    void Reveal()
    {
        if (closing || IsDisposed) return;
        Opacity = 1;
        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex & ~Native.WS_EX_LAYERED);
        Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        Update();
        DisposeGhost();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeGhost();
        base.Dispose(disposing);
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
        // 和 Windows 11 快速设置一样: 从任务栏方向滑出, 前 1/3 时间里淡入, 很快到位再慢慢停稳
        slide = cursor.Y < wa.Top ? -S(40) : S(40);
        shownAt = Environment.TickCount;
        Opacity = 0; // 真面板先透明地显示出来 (要拿焦点, 也要靠它截图)
        Location = new Point(x, y);
        Show();
        Activate();
        Native.SetForegroundWindow(Handle); // 拿到焦点, 点到别处时才会触发 Deactivate 关闭
        if (!Anim.Enabled) { Reveal(); return; }
        ShowGhost(new Point(x, y + slide), 0);
        Anim.Run(this, "show", 0, 1, 330, Anim.Linear, t =>
        {
            if (closing || ghost == null) return;
            ghost.Present(x, y + (int)Math.Round(slide * (1 - Anim.Decelerate(t))), Math.Min(1, t * 3));
        }, Reveal);
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

// 动画用的"替身": 一张带透明通道的图做成的分层窗口. 图只上传一次, 之后每帧只改位置和透明度,
// 由桌面合成器 (显卡) 完成, 不触发任何重绘.
// 用最底层的原生窗口而不是 Form: Form 显示置顶窗口时可能抢走焦点, 面板一失去焦点就会关掉
class GhostWindow : NativeWindow, IDisposable
{
    readonly int pad;          // 图四周为阴影留的边距
    readonly Native.SIZE size;
    IntPtr memDc, bitmap, oldBitmap;
    bool uploaded;

    public GhostWindow(Bitmap image, int pad)
    {
        this.pad = pad;
        size = new Native.SIZE { cx = image.Width, cy = image.Height };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        memDc = Native.CreateCompatibleDC(screen);
        Native.ReleaseDC(IntPtr.Zero, screen);
        bitmap = image.GetHbitmap(Color.FromArgb(0));
        oldBitmap = Native.SelectObject(memDc, bitmap);
        CreateHandle(new CreateParams
        {
            Style = unchecked((int)0x80000000), // WS_POPUP
            ExStyle = 0x80 | 0x8 | 0x20 | Native.WS_EX_LAYERED | 0x08000000, // TOOLWINDOW | TOPMOST | TRANSPARENT | LAYERED | NOACTIVATE
            X = -32000, Y = -32000, Width = 1, Height = 1,
        });
    }

    // x, y 是面板本身左上角的位置 (阴影边距自动扣掉)
    public void Present(int x, int y, float opacity)
    {
        var pt = new Native.POINT { X = x - pad, Y = y - pad };
        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Round(Math.Max(0, Math.Min(1, opacity)) * 255), AlphaFormat = Native.AC_SRC_ALPHA };
        if (!uploaded)
        {
            var src = new Native.POINT();
            var sz = size;
            if (!Native.UpdateLayeredWindow(Handle, IntPtr.Zero, ref pt, ref sz, memDc, ref src, 0, ref blend, Native.ULW_ALPHA))
                Log.Write("动画窗口 UpdateLayeredWindow 失败, 错误码 " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            uploaded = true;
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); // 显示但不激活
        }
        else Native.MoveLayeredWindow(Handle, IntPtr.Zero, ref pt, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, Native.ULW_ALPHA);
    }

    public void Dispose()
    {
        if (memDc != IntPtr.Zero)
        {
            Native.SelectObject(memDc, oldBitmap);
            Native.DeleteObject(bitmap);
            Native.DeleteDC(memDc);
            memDc = IntPtr.Zero;
        }
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
