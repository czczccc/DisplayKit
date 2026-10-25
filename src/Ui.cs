using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

// Windows 11 风格的配色, 跟随系统深色/浅色模式
static class Theme
{
    public static bool Dark;
    public static Color Bg, Card, Border, ControlBorder, Text, SubText, Accent, AccentHover, AccentPressed, AccentText;
    public static Color Control, ControlHover, ControlPressed, MenuHover, NavHover, NavSelected, Warn;
    public static Font Font, Bold, Small, Header, Title, Icons;

    const string Face = "Microsoft YaHei UI";
    const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static void Load()
    {
        Dark = ReadDword(PersonalizeKey, "AppsUseLightTheme", 1) == 0;
        if (Dark)
        {
            Bg = C(0x202020); Card = C(0x2B2B2B); Border = C(0x3A3A3A); ControlBorder = C(0x454545);
            Text = C(0xFFFFFF); SubText = C(0xA8A8A8);
            Accent = C(0x4CC2FF); AccentHover = C(0x48B1E8); AccentPressed = C(0x429CCB); AccentText = C(0x000000);
            Control = C(0x373737); ControlHover = C(0x3D3D3D); ControlPressed = C(0x313131);
            MenuHover = C(0x3A3A3A); NavHover = C(0x2A2A2A); NavSelected = C(0x2D2D2D); Warn = C(0xFCE100);
        }
        else
        {
            Bg = C(0xF3F3F3); Card = C(0xFBFBFB); Border = C(0xE5E5E5); ControlBorder = C(0xD9D9D9);
            Text = C(0x1B1B1B); SubText = C(0x5F5F5F);
            Accent = C(0x005FB8); AccentHover = C(0x1A6FC2); AccentPressed = C(0x3380CB); AccentText = C(0xFFFFFF);
            Control = C(0xFEFEFE); ControlHover = C(0xF6F6F6); ControlPressed = C(0xF0F0F0);
            MenuHover = C(0xF0F0F0); NavHover = C(0xEAEAEA); NavSelected = C(0xE5E5E5); Warn = C(0x9D5D00);
        }
        if (Font == null) MakeFonts();
    }

    // 字体按像素和当前缩放计算 (不用磅值: 磅值换算依赖进程启动时的系统 DPI, 缩放改变后不会更新)
    public static void MakeFonts()
    {
        float s = Ui.Scale;
        Font = new Font(Face, 12.7f * s, GraphicsUnit.Pixel);
        Bold = new Font(Face, 12.7f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        Small = new Font(Face, 11.3f * s, GraphicsUnit.Pixel);
        Header = new Font(Face, 14.7f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        Title = new Font(Face, 22.7f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        Icons = new Font("Segoe Fluent Icons", 14.7f * s, GraphicsUnit.Pixel);
        if (Icons.Name != "Segoe Fluent Icons") Icons = new Font("Segoe MDL2 Assets", 14.7f * s, GraphicsUnit.Pixel); // Windows 10
    }

    public static bool TaskbarLight() { return ReadDword(PersonalizeKey, "SystemUsesLightTheme", 0) == 1; }

    static int ReadDword(string path, string name, int def)
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(path))
            {
                object v = k == null ? null : k.GetValue(name);
                return v is int ? (int)v : def;
            }
        }
        catch { return def; }
    }

    static Color C(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

    // 深色标题栏 + 圆角窗口
    public static void ApplyWindow(IntPtr hwnd)
    {
        int dark = Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
        int round = 2;
        Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4);
    }

    public static void RoundPopup(ToolStripDropDown d)
    {
        d.Opened += delegate { int v = 3; Native.DwmSetWindowAttribute(d.Handle, 33, ref v, 4); };
    }

    public static void ThemeScrollbars(Control c)
    {
        Native.SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

static class Ui
{
    static float scale;

    public static float Scale
    {
        get
        {
            if (scale == 0) SetDpi(DpiAt(Cursor.Position));
            return scale;
        }
    }

    // 切换界面缩放 (显示器缩放改变或窗口移到另一块屏幕时), 返回是否有变化
    public static bool SetDpi(int dpi)
    {
        float s = Math.Max(1f, dpi / 96f);
        if (Math.Abs(s - scale) < 0.001f) return false;
        scale = s;
        Theme.MakeFonts();
        return true;
    }

    // 某个点所在显示器的实际缩放
    public static int DpiAt(Point p)
    {
        try
        {
            var pt = new Native.POINT { X = p.X, Y = p.Y };
            uint dx, dy;
            if (Native.GetDpiForMonitor(Native.MonitorFromPoint(pt, 2), 0, out dx, out dy) == 0 && dx > 0) return (int)dx;
        }
        catch { }
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) return (int)g.DpiX;
    }

    public static int S(int px) { return (int)Math.Round(px * Scale); }

    public static Label Text(string text, Font font, Color color, Color back)
    {
        return new Label
        {
            Text = text, Font = font, ForeColor = color, BackColor = back,
            AutoSize = false, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft,
        };
    }

    // 几个控件横向排在一起, 作为卡片行右侧的内容
    public static Panel Group(params Control[] items)
    {
        var p = new Panel { BackColor = Theme.Card };
        int x = 0, h = 0;
        foreach (var c in items)
        {
            c.Location = new Point(x, 0);
            p.Controls.Add(c);
            x += c.Width + S(8);
            h = Math.Max(h, c.Height);
        }
        p.Size = new Size(Math.Max(0, x - S(8)), h);
        return p;
    }

    public static ContextMenuStrip Menu()
    {
        var m = new ContextMenuStrip { Font = Theme.Font, Padding = new Padding(0, S(4), 0, S(4)) };
        Theme.RoundPopup(m);
        return m;
    }

    public static ToolStripMenuItem Item(ToolStripItemCollection items, string text, bool check, EventHandler click)
    {
        var it = new ToolStripMenuItem(text) { Checked = check, Padding = new Padding(S(2), S(4), S(6), S(4)) };
        if (click != null) it.Click += click;
        items.Add(it);
        return it;
    }

    public static ToolStripMenuItem Sub(ToolStripItemCollection items, string text)
    {
        var it = Item(items, text, false, null);
        it.DropDown.Font = Theme.Font;
        it.DropDown.Padding = new Padding(0, S(4), 0, S(4));
        Theme.RoundPopup(it.DropDown);
        return it;
    }

    public static ToolStripLabel Label(string text)
    {
        return new ToolStripLabel(text) { ForeColor = Theme.SubText, Padding = new Padding(S(4), S(3), S(6), S(3)) };
    }

    public static ToolStripSeparator Separator() { return new ToolStripSeparator(); }
}

// 菜单绘制: 圆角高亮, 跟随深浅色
class ModernRenderer : ToolStripProfessionalRenderer
{
    public ModernRenderer() { RoundedEdges = false; }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { e.Graphics.Clear(Theme.Card); }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Theme.MenuHover))
        using (var path = Theme.RoundRect(new RectangleF(Ui.S(4), 1, e.Item.Width - Ui.S(8), e.Item.Height - 2), Ui.S(4)))
            g.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var mi = e.Item as ToolStripMenuItem;
        if (e.Item is ToolStripLabel) e.TextColor = Theme.SubText;
        else if (!e.Item.Enabled) e.TextColor = Theme.SubText;
        else if (mi != null && e.Text == mi.ShortcutKeyDisplayString) e.TextColor = Theme.SubText;
        else e.TextColor = Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.SubText;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Ui.S(8), y, e.Item.Width - Ui.S(8), y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var r = e.ImageRectangle;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var p = new Pen(Theme.Accent, Math.Max(1.5f, Ui.Scale * 1.6f)))
        {
            p.StartCap = p.EndCap = LineCap.Round;
            g.DrawLines(p, new[]
            {
                new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
            });
        }
    }
}

// 自绘控件的基类: 双缓冲 + 悬停/按下状态
class FlatControl : Control
{
    protected bool hover, down;

    public FlatControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        Font = Theme.Font;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected Graphics Begin(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        return g;
    }
}

class RoundButton : FlatControl
{
    public bool Primary;   // 强调色按钮
    public bool Highlight; // 正在录入按键等状态
    public bool Chevron;   // 右侧下拉箭头

    public RoundButton(string text)
    {
        Text = text;
        Height = Ui.S(32);
        FitWidth(0);
    }

    public RoundButton FitWidth(int min)
    {
        int w = TextRenderer.MeasureText(Text, Font).Width + Ui.S(28) + (Chevron ? Ui.S(22) : 0);
        Width = Math.Max(min, w);
        return this;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        bool accent = (Primary || Highlight) && Enabled;
        Color fill = accent ? (down ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent)
                            : (down ? Theme.ControlPressed : hover && Enabled ? Theme.ControlHover : Theme.Control);
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(4)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (!accent) using (var p = new Pen(Theme.ControlBorder)) g.DrawPath(p, path);
        }

        Color fg = accent ? Theme.AccentText : Enabled ? Theme.Text : Theme.SubText;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (Chevron)
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(12), 0, Width - Ui.S(40), Height), fg, flags | TextFormatFlags.Left);
            float cx = Width - Ui.S(18), cy = Height / 2f, d = Ui.S(4);
            using (var p = new Pen(accent ? fg : Theme.SubText, Math.Max(1f, Ui.Scale * 1.2f)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                g.DrawLines(p, new[] { new PointF(cx - d, cy - d / 2), new PointF(cx, cy + d / 2), new PointF(cx + d, cy - d / 2) });
            }
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg, flags | TextFormatFlags.HorizontalCenter);
        }
    }
}

// 开关
class Toggle : FlatControl
{
    bool on;
    public event EventHandler Changed;

    public Toggle(bool value)
    {
        on = value;
        Size = new Size(Ui.S(40), Ui.S(20));
    }

    public bool Checked { get { return on; } }

    protected override void OnClick(EventArgs e)
    {
        on = !on;
        Invalidate();
        if (Changed != null) Changed(this, e);
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundRect(r, r.Height / 2))
        {
            if (on)
                using (var b = new SolidBrush(hover ? Theme.AccentHover : Theme.Accent)) g.FillPath(b, path);
            else
            {
                using (var b = new SolidBrush(hover ? Theme.ControlHover : Theme.Control)) g.FillPath(b, path);
                using (var p = new Pen(Theme.SubText, Math.Max(1f, Ui.Scale))) g.DrawPath(p, path);
            }
        }
        float d = Ui.S(hover ? 14 : 12);
        float cx = on ? Width - Height / 2f : Height / 2f, cy = Height / 2f;
        using (var b = new SolidBrush(on ? Theme.AccentText : Theme.SubText))
            g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
    }
}

// 滑块 (0–100), 左侧可选图标, 右侧显示数值. 拖动或滚轮时触发 ValueChanged
class Slider : FlatControl
{
    int value;
    bool dragging;
    readonly string glyph;
    public event EventHandler ValueChanged;

    public Slider(int value, int width, string glyph)
    {
        this.value = Math.Max(0, Math.Min(100, value));
        this.glyph = glyph;
        BackColor = Theme.Card;
        Size = new Size(width, Ui.S(32));
    }

    public int Value { get { return value; } }

    int Left0 { get { return glyph != null ? Ui.S(34) : Ui.S(12); } } // 留出半个滑块的位置, 0% 时不被裁掉
    int TextW { get { return Ui.S(46); } }
    float TrackW { get { return Width - Left0 - TextW - Ui.S(8); } }

    void SetFromX(int x)
    {
        int v = (int)Math.Round((x - Left0) / TrackW * 100);
        Change(v);
    }

    void Change(int v)
    {
        v = Math.Max(0, Math.Min(100, v));
        if (v == value) return;
        value = v;
        Invalidate();
        if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        dragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        dragging = false;
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Change(value + (e.Delta > 0 ? 5 : -5));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cy = Height / 2f, th = Ui.S(4), x0 = Left0, w = TrackW;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        if (glyph != null)
            TextRenderer.DrawText(g, glyph, Theme.Icons, new Rectangle(0, 0, Ui.S(28), Height), Theme.Text, flags | TextFormatFlags.HorizontalCenter);
        using (var path = Theme.RoundRect(new RectangleF(x0, cy - th / 2, w, th), th / 2))
        using (var b = new SolidBrush(Theme.ControlBorder)) g.FillPath(b, path);
        float fill = w * value / 100f;
        if (fill > th)
            using (var path = Theme.RoundRect(new RectangleF(x0, cy - th / 2, fill, th), th / 2))
            using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
        float d = Ui.S(18), cx = x0 + fill;
        using (var b = new SolidBrush(Theme.Control)) g.FillEllipse(b, cx - d / 2, cy - d / 2, d, d);
        using (var p = new Pen(Theme.ControlBorder)) g.DrawEllipse(p, cx - d / 2, cy - d / 2, d, d);
        float dot = Ui.S(hover || dragging ? 10 : 8);
        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, cx - dot / 2, cy - dot / 2, dot, dot);
        TextRenderer.DrawText(g, value + "%", Font, new Rectangle(Width - TextW, 0, TextW, Height), Theme.Text, flags | TextFormatFlags.Right);
    }
}

// 快捷面板里的按钮 (Windows 11 快速设置样式): 上面是带图标的圆角按钮, 打开时强调色填充; 文字在按钮下方
class Tile : FlatControl
{
    readonly string glyph;
    public bool On;

    public Tile(string glyph, string text, bool on)
    {
        this.glyph = glyph;
        Text = text;
        On = on;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        int bh = Ui.S(48);
        Color fill = On ? (down ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent)
                        : (down ? Theme.ControlPressed : hover ? Theme.ControlHover : Theme.Control);
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, bh - 1.5f), Ui.S(4)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (!On) using (var p = new Pen(Theme.ControlBorder)) g.DrawPath(p, path);
        }
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, glyph, Theme.Icons, new Rectangle(0, 0, Width, bh), On ? Theme.AccentText : Theme.Text, flags);
        TextRenderer.DrawText(g, Text, Theme.Small, new Rectangle(0, bh + Ui.S(2), Width, Height - bh - Ui.S(2)), Theme.Text, flags | TextFormatFlags.EndEllipsis);
    }
}

// 左侧导航项
class NavButton : FlatControl
{
    readonly string glyph;
    public bool Selected;

    public NavButton(string glyph, string text)
    {
        this.glyph = glyph;
        Text = text;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Begin(e);
        if (Selected || hover)
            using (var b = new SolidBrush(Selected ? Theme.NavSelected : Theme.NavHover))
            using (var path = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(4)))
                g.FillPath(b, path);
        if (Selected)
            using (var b = new SolidBrush(Theme.Accent))
            using (var path = Theme.RoundRect(new RectangleF(0, Height / 2f - Ui.S(8), Ui.S(3), Ui.S(16)), Ui.S(1)))
                g.FillPath(b, path);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(g, glyph, Theme.Icons, new Rectangle(Ui.S(14), 0, Ui.S(24), Height), Theme.Text, flags | TextFormatFlags.HorizontalCenter);
        TextRenderer.DrawText(g, Text, Theme.Font, new Rectangle(Ui.S(48), 0, Width - Ui.S(52), Height), Theme.Text, flags | TextFormatFlags.Left);
    }
}

// 圆角输入框
class TextField : Panel
{
    public readonly TextBox Box;

    public TextField(string text, int width)
    {
        Size = new Size(width, Ui.S(32));
        BackColor = Theme.Control;
        Box = new TextBox
        {
            Text = text, BorderStyle = BorderStyle.None, Font = Theme.Font,
            BackColor = Theme.Control, ForeColor = Theme.Text,
        };
        Box.Location = new Point(Ui.S(10), (Height - Box.PreferredHeight) / 2 + Ui.S(1));
        Box.Width = width - Ui.S(20);
        Box.GotFocus += delegate { Invalidate(); };
        Box.LostFocus += delegate { Invalidate(); };
        Controls.Add(Box);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(4)))
        {
            using (var b = new SolidBrush(Theme.Control)) g.FillPath(b, path);
            using (var p = new Pen(Theme.ControlBorder)) g.DrawPath(p, path);
        }
        if (Box.Focused)
            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, Ui.S(2), Height - Ui.S(2), Width - Ui.S(4), Ui.S(2));
    }
}

// 设置卡片: 圆角白底, 每行左侧标题+说明, 右侧控件
class Card : Panel
{
    readonly List<int> separators = new List<int>();
    readonly int pad;
    int y, rows;

    public Card(int width)
    {
        Width = width;
        BackColor = Theme.Card;
        DoubleBuffered = true;
        pad = Ui.S(16);
        y = Ui.S(2);
        Height = y + Ui.S(2);
    }

    public Label AddRow(string title, string sub, Control right)
    {
        if (rows > 0) separators.Add(y);
        int h = sub == null ? Ui.S(56) : Ui.S(68);
        int textW = Width - pad * 2 - (right == null ? 0 : right.Width + Ui.S(16));

        var t = Ui.Text(title, Theme.Font, Theme.Text, Theme.Card);
        // 标签不要盖住行与行之间的分隔线
        t.Bounds = sub == null ? new Rectangle(pad, y + Ui.S(2), textW, h - Ui.S(4)) : new Rectangle(pad, y + Ui.S(12), textW, Ui.S(24));
        Controls.Add(t);
        if (sub != null)
        {
            var s = Ui.Text(sub, Theme.Small, Theme.SubText, Theme.Card);
            s.Bounds = new Rectangle(pad, y + Ui.S(36), textW, Ui.S(20));
            Controls.Add(s);
        }
        if (right != null)
        {
            right.Location = new Point(Width - pad - right.Width, y + (h - right.Height) / 2);
            Controls.Add(right);
        }
        y += h;
        rows++;
        Height = y + Ui.S(2);
        return t;
    }

    public Label AddNote(string text, Color color)
    {
        if (rows > 0) separators.Add(y);
        int w = Width - pad * 2;
        int h = TextRenderer.MeasureText(text, Theme.Small, new Size(w, 0), TextFormatFlags.WordBreak).Height;
        var l = Ui.Text(text, Theme.Small, color, Theme.Card);
        l.AutoEllipsis = false;
        l.Bounds = new Rectangle(pad, y + Ui.S(12), w, h + Ui.S(4));
        Controls.Add(l);
        y += h + Ui.S(28);
        rows++;
        Height = y + Ui.S(2);
        return l;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(8)))
        {
            using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
            using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
        }
        g.SmoothingMode = SmoothingMode.None;
        using (var p = new Pen(Theme.Border))
            foreach (int sy in separators) g.DrawLine(p, pad, sy, Width - pad, sy);
    }
}
