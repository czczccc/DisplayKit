using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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
    const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    public const int InfoNone = 0, InfoInfo = 1, InfoWarning = 2, InfoError = 3;
    const int CallbackMsg = 0x8001; // WM_APP + 1
    static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    Icon icon;
    string tip = "DisplayKit";
    public bool Added { get; private set; }
    public ContextMenuStrip Menu;
    public event Action LeftClick, BalloonClick;

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
        set
        {
            if (value.Length > 127) value = value.Substring(0, 127);
            if (tip != value) { tip = value; Update(); }
        }
    }

    // Windows 11 上显示为系统通知
    public void ShowBalloon(string title, string text, int kind)
    {
        if (!Added) return;
        var d = Data();
        d.uFlags = NIF_INFO;
        d.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
        d.szInfo = text.Length > 255 ? text.Substring(0, 255) : text;
        d.dwInfoFlags = kind;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    // 关闭显示器 (由这个隐藏的顶层窗口交给系统处理)
    public void MonitorOff()
    {
        Native.PostMessage(Handle, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MONITORPOWER), new IntPtr(2));
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
            if (ev == 0x202 && LeftClick != null) LeftClick(); // 左键: 打开设置
            else if (ev == 0x205) ShowMenu();                   // 右键: 菜单
            else if (ev == 0x405 && BalloonClick != null) BalloonClick(); // 点击了通知
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
