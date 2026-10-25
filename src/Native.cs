using System;
using System.Runtime.InteropServices;

static class Native
{
    // ---------- 输入钩子 / 窗口 ----------

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }
    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);
    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax, uint remove);
    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
    public const int WM_QUIT = 0x12;

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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    public const int WM_SETTINGCHANGE = 0x1A, WM_COMMAND = 0x111;
    public const uint SMTO_ABORTIFHUNG = 2;

    // ---------- 桌面: 指针 / 桌面图标 / 壁纸 / 调色曲线 ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")]
    public static extern bool DestroyCursor(IntPtr cursor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint action, uint param, string pv, uint winIni);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder pv, uint winIni);
    public const uint SPI_SETCURSORS = 0x57, SPI_SETDESKWALLPAPER = 0x14, SPI_GETDESKWALLPAPER = 0x73;
    public const uint SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateDC(string driver, string device, string output, IntPtr initData);
    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    public static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")]
    public static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")]
    public static extern IntPtr CreateCursor(IntPtr hInst, int x, int y, int w, int h, byte[] andMask, byte[] xorMask);
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    public const int WM_DPICHANGED = 0x02E0;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    public static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

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

    // ---------- 分辨率 / 刷新率 (ChangeDisplaySettingsEx) ----------

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int DM_PELSWIDTH = 0x00080000, DM_PELSHEIGHT = 0x00100000, DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint CDS_UPDATEREGISTRY = 0x1, CDS_TEST = 0x2;
    public const int DISP_CHANGE_SUCCESSFUL = 0, DISP_CHANGE_RESTART = 1;
    public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1, DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;

        public static DEVMODE Create()
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return dm;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;

        public static DISPLAY_DEVICE Create()
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            return dd;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);

    // ---------- 缩放 / 显示器名称 (DisplayConfig) ----------
    // 设置缩放用的是 Windows "设置 > 显示 > 缩放" 背后同一套调用, -3 / -4 两个类型未公开文档, 但从 Windows 10 起一直可用

    public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    public const int ERROR_SUCCESS = 0;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    public const int DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE = -3;
    public const int DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE = -4;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx;
        public int outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public int scanLineOrdering, targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // 只用来占位, 不读取其中的联合体
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public int infoType;
        public uint id;
        public LUID adapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public int outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int minScaleRel, curScaleRel, maxScaleRel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public int scaleRel;
    }

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr currentTopologyId);
    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME packet);
    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME packet);
    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_GET packet);
    [DllImport("user32.dll")]
    public static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DPI_SCALE_SET packet);
}
