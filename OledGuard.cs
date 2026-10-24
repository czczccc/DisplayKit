// OLED 防烧屏工具 (OledGuard)
// 编译: build.bat  (使用 Windows 自带的 .NET Framework 4 csc.exe, 无需安装任何东西)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Native
{
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string name);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")]
    public static extern IntPtr CreateCursor(IntPtr hInst, int x, int y, int w, int h, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
    }
    [DllImport("xinput1_4.dll")]
    public static extern int XInputGetState(int index, out XINPUT_STATE state);

    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    public const int WM_MOUSEMOVE = 0x200, WM_MOUSEWHEEL = 0x20A, WM_MOUSEHWHEEL = 0x20E;
    public const int WM_SYSCOMMAND = 0x112, SC_MONITORPOWER = 0xF170;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    public static void EnableDpiAwareness()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }
}

// ---------- 系统声音检测 (Core Audio 峰值表) ----------

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    int GetPeakValue(out float peak);
}

class AudioMeter
{
    IAudioMeterInformation meter;
    int acquiredAt;

    // 默认输出设备当前是否有声音 (测量的是音量调节之前的信号, 静音也能检测到)
    public bool IsPlaying()
    {
        try
        {
            // 定期重新获取, 以跟随默认设备切换 (耳机/音箱)
            if (meter == null || unchecked(Environment.TickCount - acquiredAt) > 30000)
            {
                meter = null;
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                IMMDevice dev;
                if (en.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return false;
                var iid = typeof(IAudioMeterInformation).GUID;
                object o;
                if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) != 0) return false;
                meter = (IAudioMeterInformation)o;
                acquiredAt = Environment.TickCount;
            }
            float peak;
            if (meter.GetPeakValue(out peak) != 0) { meter = null; return false; }
            return peak > 0.0005f;
        }
        catch { meter = null; return false; }
    }
}

// ---------- 手柄检测 (XInput, 带摇杆死区以防漂移) ----------

class GamepadWatcher
{
    readonly Native.XINPUT_STATE[] last = new Native.XINPUT_STATE[4];
    readonly bool[] connected = new bool[4];
    int lastScan = -100000;
    bool available = true;

    public bool HasActivity()
    {
        if (!available) return false;
        bool active = false;
        bool rescan = unchecked(Environment.TickCount - lastScan) > 5000; // 未连接的槽位查询较慢, 每 5 秒扫一次
        if (rescan) lastScan = Environment.TickCount;
        for (int i = 0; i < 4; i++)
        {
            if (!connected[i] && !rescan) continue;
            Native.XINPUT_STATE s;
            try
            {
                if (Native.XInputGetState(i, out s) != 0) { connected[i] = false; continue; }
            }
            catch { available = false; return false; }
            if (connected[i])
            {
                var p = last[i];
                if (s.Buttons != p.Buttons || Math.Abs(s.LeftTrigger - p.LeftTrigger) > 30 || Math.Abs(s.RightTrigger - p.RightTrigger) > 30 ||
                    Math.Abs(s.LX - p.LX) > 6000 || Math.Abs(s.LY - p.LY) > 6000 || Math.Abs(s.RX - p.RX) > 6000 || Math.Abs(s.RY - p.RY) > 6000)
                {
                    active = true;
                    last[i] = s;
                }
            }
            else
            {
                connected[i] = true;
                last[i] = s;
            }
        }
        return active;
    }
}

class Settings
{
    public const int WakeAny = 0, WakeAnyKey = 1, WakeKey = 2;

    public int TimeoutSec = 180;        // 无操作多久后黑屏
    public int MoveThreshold = 60;      // 鼠标移动超过多少像素才算"真的在用"
    public int PowerOffMin = 0;         // 黑屏多久后再真正关闭显示器, 0 = 不关
    public bool SkipFullscreen = true;  // 前台是全屏应用(看视频/游戏)时不黑屏
    public bool SkipAudio = true;       // 有声音播放时不黑屏
    public bool Gamepad = true;         // 手柄操作算作活动
    public int WakeMode = WakeKey;      // 唤醒方式
    public int WakeVk = (int)Keys.Space;
    public int WakeMods = 0;            // 修饰键: 1=Alt 2=Ctrl 4=Shift 8=Win
    public int BlankVk = (int)Keys.B;   // 立即黑屏快捷键
    public int BlankMods = 2 | 1;       // Ctrl+Alt
    public List<string> Whitelist = new List<string>(); // 前台是这些程序时不黑屏 (进程名, 小写, 不含 .exe)

    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OledGuard"); } }
    static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                int n;
                if (k == "TimeoutSec" && int.TryParse(v, out n) && n >= 10) s.TimeoutSec = n;
                else if (k == "MoveThreshold" && int.TryParse(v, out n) && n > 0) s.MoveThreshold = n;
                else if (k == "PowerOffMin" && int.TryParse(v, out n) && n >= 0) s.PowerOffMin = n;
                else if (k == "WakeMode" && int.TryParse(v, out n) && n >= 0 && n <= 2) s.WakeMode = n;
                else if (k == "WakeVk" && int.TryParse(v, out n) && n > 0 && n < 256) s.WakeVk = n;
                else if (k == "WakeMods" && int.TryParse(v, out n) && n >= 0 && n < 16) s.WakeMods = n;
                else if (k == "BlankVk" && int.TryParse(v, out n) && n > 0 && n < 256) s.BlankVk = n;
                else if (k == "BlankMods" && int.TryParse(v, out n) && n >= 0 && n < 16) s.BlankMods = n;
                else if (k == "SkipFullscreen") s.SkipFullscreen = v == "1";
                else if (k == "SkipAudio") s.SkipAudio = v == "1";
                else if (k == "Gamepad") s.Gamepad = v == "1";
                else if (k == "Whitelist")
                    foreach (var name in v.Split('|'))
                        if (name.Trim().Length > 0) s.Whitelist.Add(name.Trim().ToLowerInvariant());
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[] {
                "TimeoutSec=" + TimeoutSec,
                "MoveThreshold=" + MoveThreshold,
                "PowerOffMin=" + PowerOffMin,
                "SkipFullscreen=" + (SkipFullscreen ? "1" : "0"),
                "SkipAudio=" + (SkipAudio ? "1" : "0"),
                "Gamepad=" + (Gamepad ? "1" : "0"),
                "WakeMode=" + WakeMode,
                "WakeVk=" + WakeVk,
                "WakeMods=" + WakeMods,
                "BlankVk=" + BlankVk,
                "BlankMods=" + BlankMods,
                "Whitelist=" + string.Join("|", Whitelist.ToArray()),
            });
        }
        catch { }
    }
}

// 覆盖单个显示器的纯黑窗口
class BlankForm : Form
{
    static Cursor blankCursor;

    public BlankForm(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = bounds;
        if (blankCursor == null)
        {
            var and = new byte[128]; var xor = new byte[128];
            for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
            blankCursor = new Cursor(Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor));
        }
        Cursor = blankCursor;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8; // WS_EX_TOOLWINDOW | WS_EX_TOPMOST
            return cp;
        }
    }

    public void KeepOnTop()
    {
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}

// 按键组合 (修饰键 + 主键) 的工具方法
static class KeyCombo
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public static bool IsModifier(int vk)
    {
        return (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
    }

    // 左右 Shift/Ctrl/Alt/Win 统一成通用键码
    public static int Normalize(int vk)
    {
        if (vk == 0xA0 || vk == 0xA1) return 0x10;
        if (vk == 0xA2 || vk == 0xA3) return 0x11;
        if (vk == 0xA4 || vk == 0xA5) return 0x12;
        if (vk == 0x5C) return 0x5B;
        return vk;
    }

    static int ModBit(int vk)
    {
        switch (Normalize(vk)) { case 0x10: return Shift; case 0x11: return Ctrl; case 0x12: return Alt; case 0x5B: return Win; }
        return 0;
    }

    static bool Down(int vk) { return (Native.GetAsyncKeyState(vk) & 0x8000) != 0; }

    // 当前按住的修饰键 (不含 exceptVk 本身). 在低级钩子里调用时, 反映的是当前这次按键之前的状态
    public static int CurrentMods(int exceptVk)
    {
        int m = 0;
        if (Down(0x11)) m |= Ctrl;
        if (Down(0x10)) m |= Shift;
        if (Down(0x12)) m |= Alt;
        if (Down(0x5B) || Down(0x5C)) m |= Win;
        return m & ~ModBit(exceptVk);
    }

    public static bool Matches(int vk, int mods, int wantVk, int wantMods)
    {
        return Normalize(vk) == wantVk && mods == (wantMods & ~ModBit(wantVk));
    }

    public static string Name(int mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & Ctrl) != 0 && Normalize(vk) != 0x11) parts.Add("Ctrl");
        if ((mods & Shift) != 0 && Normalize(vk) != 0x10) parts.Add("Shift");
        if ((mods & Alt) != 0 && Normalize(vk) != 0x12) parts.Add("Alt");
        if ((mods & Win) != 0 && Normalize(vk) != 0x5B) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts.ToArray());
    }

    static string KeyName(int vk)
    {
        switch (vk)
        {
            case 0x10: return "Shift";
            case 0x11: return "Ctrl";
            case 0x12: return "Alt";
            case 0x5B: return "Win";
            case 0x20: return "空格";
            case 0x0D: return "回车";
            case 0x1B: return "Esc";
            case 0x08: return "退格";
            case 0x09: return "Tab";
            case 0x13: return "Pause";
            case 0x91: return "ScrollLock";
            case 0x2D: return "Insert";
            case 0x2E: return "Delete";
            case 0x24: return "Home";
            case 0x23: return "End";
            case 0x21: return "PageUp";
            case 0x22: return "PageDown";
            case 0x25: return "←";
            case 0x26: return "↑";
            case 0x27: return "→";
            case 0x28: return "↓";
            case 0xC0: return "`";
            case 0xBD: return "-";
            case 0xBB: return "=";
            case 0xDB: return "[";
            case 0xDD: return "]";
            case 0xDC: return "\\";
            case 0xBA: return ";";
            case 0xDE: return "'";
            case 0xBC: return ",";
            case 0xBE: return ".";
            case 0xBF: return "/";
        }
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "小键盘" + (vk - 0x60);
        return ((Keys)vk).ToString();
    }
}

// 窗口布局工具: 全部用自动布局, 按系统 DPI 缩放, 避免高分屏下文字被截断
static class Ui
{
    static float scale;

    public static int S(int px)
    {
        if (scale == 0)
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
        return (int)Math.Round(px * scale);
    }

    public static void SetupDialog(Form f, string title)
    {
        f.Text = title;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false;
        f.StartPosition = FormStartPosition.CenterScreen;
        f.AutoScaleMode = AutoScaleMode.None;
        f.AutoSize = true;
        f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        f.Font = new Font("Microsoft YaHei UI", 9f);
    }

    public static TableLayoutPanel Table(int columns)
    {
        return new TableLayoutPanel
        {
            ColumnCount = columns,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(12)),
        };
    }

    public static Button Button(string text)
    {
        return new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(8), S(2), S(8), S(2)),
            TabStop = false,
        };
    }

    public static Label Label(string text, int maxWidth)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(maxWidth, 0),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(S(3), S(6), S(3), S(6)),
        };
    }
}

// 快捷键设置窗口: 点"修改"后直接按下想要的按键或组合键
class HotkeySettingsForm : Form
{
    public int WakeVk, WakeMods, BlankVk, BlankMods;
    public int Capturing; // 0 = 无, 1 = 唤醒键, 2 = 黑屏快捷键
    readonly Label wakeLabel, blankLabel, hint;
    const string DefaultHint = "支持单键或组合键 (如 Ctrl+Shift+F12)。两个可以设成同一个键, 这样一键切换黑屏/唤醒。";

    public HotkeySettingsForm(Settings cfg)
    {
        WakeVk = cfg.WakeVk; WakeMods = cfg.WakeMods; BlankVk = cfg.BlankVk; BlankMods = cfg.BlankMods;
        Ui.SetupDialog(this, "快捷键设置");
        MinimizeBox = false;
        TopMost = true;

        var t = Ui.Table(3);
        var bold = new Font(Font, FontStyle.Bold);

        t.Controls.Add(Ui.Label("唤醒屏幕:", 0), 0, 0);
        wakeLabel = Ui.Label("", 0);
        wakeLabel.Font = bold;
        wakeLabel.MinimumSize = new Size(Ui.S(200), 0);
        t.Controls.Add(wakeLabel, 1, 0);
        var wakeBtn = Ui.Button("修改");
        wakeBtn.Click += delegate { BeginCapture(1); };
        t.Controls.Add(wakeBtn, 2, 0);

        t.Controls.Add(Ui.Label("立即黑屏:", 0), 0, 1);
        blankLabel = Ui.Label("", 0);
        blankLabel.Font = bold;
        blankLabel.MinimumSize = new Size(Ui.S(200), 0);
        t.Controls.Add(blankLabel, 1, 1);
        var blankBtn = Ui.Button("修改");
        blankBtn.Click += delegate { BeginCapture(2); };
        t.Controls.Add(blankBtn, 2, 1);

        hint = Ui.Label(DefaultHint, Ui.S(380));
        hint.ForeColor = Color.DimGray;
        t.Controls.Add(hint, 0, 2);
        t.SetColumnSpan(hint, 3);

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.Right };
        var ok = Ui.Button("保存");
        ok.DialogResult = DialogResult.OK;
        var cancel = Ui.Button("取消");
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += delegate { Close(); };
        cancel.Click += delegate { Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        t.Controls.Add(buttons, 0, 3);
        t.SetColumnSpan(buttons, 3);

        Controls.Add(t);
        RefreshLabels();
    }

    // 按键全部由全局钩子捕获, 窗口自身不处理按键, 避免 Esc/回车 触发按钮
    protected override bool ProcessDialogKey(Keys keyData) { return true; }

    void BeginCapture(int which)
    {
        Capturing = which;
        RefreshLabels();
    }

    public void OnCaptured(int mods, int vk)
    {
        if (Capturing == 2 && mods == 0 && !(vk >= 0x70 && vk <= 0x87) && vk != 0x13 && vk != 0x91)
        {
            hint.ForeColor = Color.Firebrick;
            hint.Text = "立即黑屏快捷键需要带 Ctrl/Alt/Shift/Win, 或使用 F1-F24/Pause/ScrollLock, 以免打字时误触。请重新按。";
            return;
        }
        if (Capturing == 2 && KeyCombo.IsModifier(vk))
        {
            hint.ForeColor = Color.Firebrick;
            hint.Text = "立即黑屏快捷键不能只是修饰键, 请重新按。";
            return;
        }
        if (Capturing == 1) { WakeVk = vk; WakeMods = mods; }
        else if (Capturing == 2) { BlankVk = vk; BlankMods = mods; }
        Capturing = 0;
        hint.ForeColor = Color.DimGray;
        hint.Text = DefaultHint;
        RefreshLabels();
    }

    void RefreshLabels()
    {
        wakeLabel.Text = Capturing == 1 ? "请按下按键..." : KeyCombo.Name(WakeMods, WakeVk);
        blankLabel.Text = Capturing == 2 ? "请按下按键..." : KeyCombo.Name(BlankMods, BlankVk);
        wakeLabel.ForeColor = Capturing == 1 ? Color.RoyalBlue : SystemColors.ControlText;
        blankLabel.ForeColor = Capturing == 2 ? Color.RoyalBlue : SystemColors.ControlText;
    }
}

static class Log
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OledGuard", "log.txt");

    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
            File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
        }
        catch { }
    }
}

// 直接调用 Shell_NotifyIcon 的托盘图标: 失败可重试并记录原因, 资源管理器重启后自动重新添加
class TrayIcon : NativeWindow
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int RegisterWindowMessage(string s);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const int CallbackMsg = 0x8001; // WM_APP + 1
    static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    Icon icon;
    string tip = "OLED 防烧屏";
    public bool Added { get; private set; }
    public ContextMenuStrip Menu;

    public TrayIcon() { CreateHandle(new CreateParams()); }

    NOTIFYICONDATA Data()
    {
        var d = new NOTIFYICONDATA();
        d.cbSize = Marshal.SizeOf(d);
        d.hWnd = Handle;
        d.uID = 1;
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        d.uCallbackMessage = CallbackMsg;
        d.hIcon = icon != null ? icon.Handle : IntPtr.Zero;
        d.szTip = tip;
        return d;
    }

    public bool TryAdd()
    {
        var d = Data();
        if (Shell_NotifyIcon(NIM_ADD, ref d) || Shell_NotifyIcon(NIM_MODIFY, ref d))
        {
            if (!Added) Log.Write("托盘图标已添加");
            Added = true;
        }
        else
        {
            Log.Write("托盘图标添加失败, 错误码 " + Marshal.GetLastWin32Error());
        }
        return Added;
    }

    void Update()
    {
        if (!Added) return;
        var d = Data();
        if (!Shell_NotifyIcon(NIM_MODIFY, ref d)) Added = false; // 下次定时器会重新添加
    }

    public Icon Icon
    {
        get { return icon; }
        set { if (icon != value) { icon = value; Update(); } }
    }

    public string Text
    {
        get { return tip; }
        set { if (tip != value) { tip = value; Update(); } }
    }

    public void ShowMenu()
    {
        if (Menu == null) return;
        SetForegroundWindow(Handle);
        Menu.Show(Cursor.Position);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == CallbackMsg)
        {
            int ev = m.LParam.ToInt32() & 0xFFFF;
            if (ev == 0x202 || ev == 0x205) ShowMenu(); // 左键或右键单击都弹出菜单
        }
        else if (m.Msg == TaskbarCreated && TaskbarCreated != 0)
        {
            Log.Write("资源管理器已重启, 重新添加托盘图标");
            Added = false;
            TryAdd();
        }
        base.WndProc(ref m);
    }

    public void Remove()
    {
        var d = Data();
        Shell_NotifyIcon(NIM_DELETE, ref d);
        Added = false;
        DestroyHandle();
    }
}

// 控制窗口: 手动启动或再次双击 exe 时显示, 托盘图标出问题时也能操作
class ControlForm : Form
{
    public bool AllowClose;
    readonly Label status, trayStatus;
    public event Action<Control> MenuRequested;
    public event Action BlankRequested;

    public ControlForm()
    {
        Ui.SetupDialog(this, "OLED 防烧屏");

        var t = Ui.Table(1);
        int width = Ui.S(400);
        status = Ui.Label("", width);
        status.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        status.MinimumSize = new Size(width, 0);
        trayStatus = Ui.Label("", width);
        trayStatus.MinimumSize = new Size(width, 0);
        trayStatus.ForeColor = Color.DimGray;

        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, Ui.S(8), 0, 0) };
        var menuBtn = Ui.Button("设置菜单");
        var blankBtn = Ui.Button("立即黑屏");
        var hideBtn = Ui.Button("隐藏到后台");
        menuBtn.Click += delegate { if (MenuRequested != null) MenuRequested(menuBtn); };
        blankBtn.Click += delegate { if (BlankRequested != null) BlankRequested(); };
        hideBtn.Click += delegate { Hide(); };
        buttons.Controls.AddRange(new Control[] { menuBtn, blankBtn, hideBtn });

        t.Controls.Add(status);
        t.Controls.Add(trayStatus);
        t.Controls.Add(buttons);
        Controls.Add(t);
    }

    public void SetStatus(string text, bool trayOk)
    {
        if (status.Text != text) status.Text = text;
        string t = trayOk
            ? "托盘图标已显示 (在任务栏右下角, 可能收在 ^ 里)。关闭本窗口后程序继续在后台运行, 再次双击 exe 可重新打开本窗口。"
            : "托盘图标未能显示, 原因已记录在 %APPDATA%\\OledGuard\\log.txt。程序仍在正常工作, 再次双击 exe 可打开本窗口。";
        if (trayStatus.Text != t) trayStatus.Text = t;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }
}

class GuardApp : ApplicationContext
{
    const string AppName = "OledGuard";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const int GraceMs = 800; // 黑屏后这段时间内忽略输入(比如松开快捷键)

    readonly Settings cfg = Settings.Load();
    readonly TrayIcon tray = new TrayIcon();
    readonly ControlForm control = new ControlForm();
    int lastTrayTry = Environment.TickCount;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly Control invoker = new Control();
    readonly List<BlankForm> overlays = new List<BlankForm>();
    readonly AudioMeter audio = new AudioMeter();
    readonly GamepadWatcher gamepad = new GamepadWatcher();
    readonly Native.HookProc mouseProc, keyProc; // 保持引用, 防止被 GC
    readonly int selfPid = Process.GetCurrentProcess().Id;
    IntPtr mouseHook, keyHook;
    Icon iconActive, iconPaused;

    // 以下字段只在 UI 线程 (含钩子回调) 访问
    int lastActivity = Environment.TickCount; // 最后一次"有效"操作
    int lastRawInput = Environment.TickCount; // 最后一次任何输入(含抖动)
    int anchorX, anchorY;
    bool overlayVisible, dismissQueued;
    int overlayShownAt, monitorOffAt;
    bool monitorOffSent;
    DateTime pausedUntil = DateTime.MinValue;
    readonly HashSet<int> swallowMouseUp = new HashSet<int>();
    readonly HashSet<int> swallowKeyUp = new HashSet<int>();
    string lastTip, keepAwakeReason;
    HotkeySettingsForm keyDialog; // 快捷键设置窗口打开时不为 null
    int pendingModOnly;           // 设置快捷键时: 只按了修饰键还没按别的键
    IntPtr fgHwnd; string fgProcess; // 最近一个前台程序 (不含本程序/任务栏), 用于加入白名单

    static int Since(int tick) { return unchecked(Environment.TickCount - tick); }

    public GuardApp(bool autostart, EventWaitHandle showSignal)
    {
        var h = invoker.Handle; // 强制创建句柄, 供 BeginInvoke 使用
        var p = Cursor.Position; anchorX = p.X; anchorY = p.Y;
        Log.Write("启动" + (autostart ? " (开机自启)" : ""));

        iconActive = MakeIcon(Color.FromArgb(0, 200, 120));
        iconPaused = MakeIcon(Color.Gray);
        tray.Icon = iconActive;
        tray.TryAdd();

        control.MenuRequested += btn => tray.Menu.Show(btn, new Point(0, btn.Height));
        control.BlankRequested += ShowOverlay;
        // 再次运行 exe 时, 新进程会发信号让这里显示控制窗口
        ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { invoker.BeginInvoke((Action)ShowControl); }, null, -1, false);

        mouseProc = MouseHook;
        keyProc = KeyHook;
        IntPtr mod = Native.GetModuleHandle(null);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, mod, 0);
        keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, mod, 0);

        BuildMenu();

        SystemEvents.DisplaySettingsChanged += delegate { if (overlayVisible) { HideOverlay(); ShowOverlay(); } };

        timer.Interval = 500;
        timer.Tick += delegate { Tick(); };
        timer.Start();

        Tick();
        if (!autostart) ShowControl();
    }

    void ShowControl()
    {
        control.Show();
        if (control.WindowState == FormWindowState.Minimized) control.WindowState = FormWindowState.Normal;
        control.Activate();
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

            if (keyDialog != null && keyDialog.Capturing != 0)
            {
                // 设置快捷键: 按下普通键时连同已按住的修饰键一起记录; 单独按下并松开修饰键则记录该修饰键
                if (down && !isMod)
                {
                    int mods = KeyCombo.CurrentMods(vk), v = KeyCombo.Normalize(vk);
                    pendingModOnly = 0;
                    invoker.BeginInvoke((Action)delegate { if (keyDialog != null) keyDialog.OnCaptured(mods, v); });
                    swallowKeyUp.Add(vk);
                    return new IntPtr(1);
                }
                if (down && isMod) pendingModOnly = KeyCombo.Normalize(vk);
                else if (!down && isMod && pendingModOnly == KeyCombo.Normalize(vk))
                {
                    int mods = KeyCombo.CurrentMods(vk), v = pendingModOnly;
                    pendingModOnly = 0;
                    invoker.BeginInvoke((Action)delegate { if (keyDialog != null) keyDialog.OnCaptured(mods, v); });
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
            else if (!isMod && keyDialog == null && KeyCombo.Matches(vk, KeyCombo.CurrentMods(vk), cfg.BlankVk, cfg.BlankMods))
            {
                invoker.BeginInvoke((Action)ShowOverlay); // 立即黑屏快捷键
                swallowKeyUp.Add(vk);
                return new IntPtr(1);
            }
            else
            {
                lastActivity = Environment.TickCount;
            }
        }
        return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
    }

    void QueueDismiss()
    {
        if (dismissQueued) return;
        dismissQueued = true;
        invoker.BeginInvoke((Action)HideOverlay);
    }

    // ---------- 黑屏 ----------

    void ShowOverlay()
    {
        if (overlayVisible) return;
        foreach (var s in Screen.AllScreens)
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
        var b = Screen.FromHandle(hwnd).Bounds;
        return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
    }

    // 返回不该黑屏的原因, null 表示可以黑屏
    string KeepAwakeReason(bool fullscreen)
    {
        if (fgProcess != null && Native.GetForegroundWindow() == fgHwnd && cfg.Whitelist.Contains(fgProcess)) return "白名单: " + fgProcess;
        if (cfg.SkipFullscreen && fullscreen) return "全屏应用";
        if (cfg.SkipAudio && audio.IsPlaying()) return "正在播放声音";
        return null;
    }

    void Tick()
    {
        bool paused = DateTime.Now < pausedUntil;
        bool fullscreen = UpdateForeground();

        if (cfg.Gamepad && gamepad.HasActivity())
        {
            if (overlayVisible && cfg.WakeMode == Settings.WakeAny) HideOverlay();
            else if (!overlayVisible) lastActivity = Environment.TickCount;
        }

        if (overlayVisible)
        {
            foreach (var f in overlays) f.KeepOnTop();
            // 可选: 黑屏一段时间后真正关闭显示器; 若被抖动唤醒(仍是黑屏), 静止 10 秒后再关一次
            if (cfg.PowerOffMin > 0 && Since(overlayShownAt) >= cfg.PowerOffMin * 60000 && overlays.Count > 0)
            {
                bool wokenSince = monitorOffSent && unchecked(lastRawInput - monitorOffAt) > 0;
                if ((!monitorOffSent || wokenSince) && Since(lastRawInput) >= 10000)
                {
                    Native.PostMessage(overlays[0].Handle, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MONITORPOWER), new IntPtr(2));
                    monitorOffSent = true;
                    monitorOffAt = Environment.TickCount;
                }
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

        tray.Icon = paused ? iconPaused : iconActive;
        string tip;
        if (overlayVisible) tip = "OLED 防烧屏 - 黑屏中";
        else if (paused) tip = pausedUntil == DateTime.MaxValue ? "OLED 防烧屏 - 已暂停" : "OLED 防烧屏 - 暂停至 " + pausedUntil.ToString("HH:mm");
        else if (keepAwakeReason != null) tip = "OLED 防烧屏 - 不黑屏 (" + keepAwakeReason + ")";
        else
        {
            int left = Math.Max(0, cfg.TimeoutSec - Since(lastActivity) / 1000);
            tip = string.Format("OLED 防烧屏 - {0}:{1:00} 后黑屏", left / 60, left % 60);
        }
        if (tip.Length > 63) tip = tip.Substring(0, 63);
        if (tip != lastTip) { tray.Text = tip; lastTip = tip; }

        // 托盘图标没加上 (例如开机时任务栏还没就绪) 就每 3 秒重试
        if (!tray.Added && Since(lastTrayTry) > 3000)
        {
            lastTrayTry = Environment.TickCount;
            tray.TryAdd();
        }
        if (control.Visible) control.SetStatus(tip.Replace("OLED 防烧屏 - ", "运行中: "), tray.Added);
    }

    // ---------- 快捷键设置 ----------

    void OpenKeySettings()
    {
        if (keyDialog != null) { keyDialog.Activate(); return; }
        keyDialog = new HotkeySettingsForm(cfg);
        keyDialog.FormClosed += delegate
        {
            var d = keyDialog;
            keyDialog = null;
            pendingModOnly = 0;
            if (d.DialogResult != DialogResult.OK) return;
            cfg.WakeVk = d.WakeVk; cfg.WakeMods = d.WakeMods;
            cfg.BlankVk = d.BlankVk; cfg.BlankMods = d.BlankMods;
            cfg.WakeMode = Settings.WakeKey;
            Save();
        };
        keyDialog.Show();
        keyDialog.Activate();
    }

    // ---------- 托盘菜单 ----------

    void BuildMenu()
    {
        var old = tray.Menu;
        var m = new ContextMenuStrip();

        m.Items.Add("立即黑屏 (" + KeyCombo.Name(cfg.BlankMods, cfg.BlankVk) + ")", null, delegate { ShowOverlay(); });
        m.Items.Add("快捷键设置...", null, delegate { OpenKeySettings(); });
        m.Items.Add(new ToolStripSeparator());

        var timeout = new ToolStripMenuItem("无操作后黑屏");
        foreach (int sec in new[] { 30, 60, 120, 180, 300, 600, 900, 1800 })
        {
            int v = sec;
            var it = new ToolStripMenuItem(sec < 60 ? sec + " 秒" : sec / 60 + " 分钟");
            it.Checked = cfg.TimeoutSec == v;
            it.Click += delegate { cfg.TimeoutSec = v; Save(); };
            timeout.DropDownItems.Add(it);
        }
        m.Items.Add(timeout);

        var wake = new ToolStripMenuItem("唤醒方式");
        var w1 = new ToolStripMenuItem("仅按 " + KeyCombo.Name(cfg.WakeMods, cfg.WakeVk));
        w1.Checked = cfg.WakeMode == Settings.WakeKey;
        w1.Click += delegate { cfg.WakeMode = Settings.WakeKey; Save(); };
        var w2 = new ToolStripMenuItem("键盘任意键");
        w2.Checked = cfg.WakeMode == Settings.WakeAnyKey;
        w2.Click += delegate { cfg.WakeMode = Settings.WakeAnyKey; Save(); };
        var w3 = new ToolStripMenuItem("任意操作 (按键/点击/大幅移动鼠标/手柄)");
        w3.Checked = cfg.WakeMode == Settings.WakeAny;
        w3.Click += delegate { cfg.WakeMode = Settings.WakeAny; Save(); };
        wake.DropDownItems.Add(w1);
        wake.DropDownItems.Add(w2);
        wake.DropDownItems.Add(w3);
        wake.DropDownItems.Add(new ToolStripSeparator());
        wake.DropDownItems.Add("更换唤醒按键...", null, delegate { OpenKeySettings(); });
        m.Items.Add(wake);

        var keep = new ToolStripMenuItem("这些情况不黑屏");
        var fs = new ToolStripMenuItem("前台是全屏应用 (视频/游戏)");
        fs.Checked = cfg.SkipFullscreen;
        fs.Click += delegate { cfg.SkipFullscreen = !cfg.SkipFullscreen; Save(); };
        var au = new ToolStripMenuItem("正在播放声音 (窗口化视频/直播等)");
        au.Checked = cfg.SkipAudio;
        au.Click += delegate { cfg.SkipAudio = !cfg.SkipAudio; Save(); };
        var gp = new ToolStripMenuItem("手柄操作算作活动");
        gp.Checked = cfg.Gamepad;
        gp.Click += delegate { cfg.Gamepad = !cfg.Gamepad; Save(); };
        keep.DropDownItems.Add(fs);
        keep.DropDownItems.Add(au);
        keep.DropDownItems.Add(gp);
        m.Items.Add(keep);

        var wl = new ToolStripMenuItem("白名单 (前台是这些程序时不黑屏)");
        var add = new ToolStripMenuItem("添加当前程序");
        add.Click += delegate
        {
            if (fgProcess != null && !cfg.Whitelist.Contains(fgProcess)) { cfg.Whitelist.Add(fgProcess); Save(); }
        };
        wl.DropDownItems.Add(add);
        if (cfg.Whitelist.Count > 0) wl.DropDownItems.Add(new ToolStripSeparator());
        foreach (var name in cfg.Whitelist)
        {
            string n = name;
            var it = new ToolStripMenuItem(n + ".exe  (点击移除)");
            it.Click += delegate { cfg.Whitelist.Remove(n); Save(); };
            wl.DropDownItems.Add(it);
        }
        wl.DropDownOpening += delegate
        {
            add.Text = fgProcess == null ? "添加当前程序 (先切换到该程序)" : "添加当前程序: " + fgProcess + ".exe";
            add.Enabled = fgProcess != null && !cfg.Whitelist.Contains(fgProcess);
        };
        m.Items.Add(wl);

        var thr = new ToolStripMenuItem("鼠标防抖 (移动多远才算操作)");
        foreach (var pair in new[] { new KeyValuePair<string, int>("低 - 20 像素", 20), new KeyValuePair<string, int>("中 - 60 像素", 60),
                                     new KeyValuePair<string, int>("高 - 150 像素", 150), new KeyValuePair<string, int>("很高 - 300 像素", 300) })
        {
            int v = pair.Value;
            var it = new ToolStripMenuItem(pair.Key);
            it.Checked = cfg.MoveThreshold == v;
            it.Click += delegate { cfg.MoveThreshold = v; Save(); };
            thr.DropDownItems.Add(it);
        }
        m.Items.Add(thr);

        var off = new ToolStripMenuItem("黑屏后关闭显示器");
        foreach (int min in new[] { 0, 1, 5, 15, 30, 60 })
        {
            int v = min;
            var it = new ToolStripMenuItem(min == 0 ? "不关闭 (只保持黑屏)" : "黑屏 " + min + " 分钟后");
            it.Checked = cfg.PowerOffMin == v;
            it.Click += delegate { cfg.PowerOffMin = v; Save(); };
            off.DropDownItems.Add(it);
        }
        m.Items.Add(off);

        m.Items.Add(new ToolStripSeparator());
        bool paused = DateTime.Now < pausedUntil;
        if (paused)
            m.Items.Add("恢复", null, delegate { pausedUntil = DateTime.MinValue; lastActivity = Environment.TickCount; BuildMenu(); });
        var pause = new ToolStripMenuItem(paused ? "更改暂停时间" : "暂停");
        foreach (int min in new[] { 30, 60, 120, 0 })
        {
            int v = min;
            pause.DropDownItems.Add(min == 0 ? "直到手动恢复" : (min < 60 ? min + " 分钟" : min / 60 + " 小时"), null, delegate
            {
                pausedUntil = v == 0 ? DateTime.MaxValue : DateTime.Now.AddMinutes(v);
                BuildMenu();
            });
        }
        m.Items.Add(pause);

        var auto = new ToolStripMenuItem("开机自启动");
        auto.Checked = IsAutoStart();
        auto.Click += delegate { SetAutoStart(!IsAutoStart()); BuildMenu(); };
        m.Items.Add(auto);

        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("退出", null, delegate { ExitThread(); });

        tray.Menu = m;
        if (old != null) old.Dispose();
    }

    void Save() { cfg.Save(); lastActivity = Environment.TickCount; BuildMenu(); }

    static bool IsAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(AppName) != null;
    }

    static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --autostart");
            else k.DeleteValue(AppName, false);
        }
    }

    static Icon MakeIcon(Color dot)
    {
        using (var bmp = new Bitmap(32, 32))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var frame = new Pen(Color.White, 3)) g.DrawRectangle(frame, 3, 5, 26, 18);
            g.FillRectangle(Brushes.Black, 5, 7, 23, 15);
            using (var b = new SolidBrush(dot)) g.FillEllipse(b, 11, 9, 10, 10);
            g.FillRectangle(Brushes.White, 13, 24, 6, 3);
            g.FillRectangle(Brushes.White, 9, 27, 14, 2);
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        if (overlayVisible) HideOverlay();
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
        tray.Remove();
        control.AllowClose = true;
        control.Close();
        Log.Write("退出");
        base.ExitThreadCore();
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\OledGuard_Show"))
        using (var mutex = new Mutex(true, "Local\\OledGuard_SingleInstance", out created))
        {
            if (!created) { showSignal.Set(); return; } // 已在运行: 让已有实例显示控制窗口
            Application.ThreadException += (s, e) => Log.Write("异常: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("未处理异常: " + e.ExceptionObject);
            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool autostart = Array.IndexOf(args, "--autostart") >= 0;
            Application.Run(new GuardApp(autostart, showSignal));
        }
    }
}
