using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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

// 黑屏前的提示: 半透明黑色逐渐加深, 中间显示倒计时. 鼠标键盘全部穿透, 不影响正在用的程序
class WarnForm : Form
{
    readonly Label text;

    public WarnForm(Rectangle bounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.Black;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = bounds;
        Opacity = 0;
        text = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(220, 220, 220),
            BackColor = Color.Black,
            Font = new Font("Microsoft YaHei UI", Math.Max(16, bounds.Height / 36), GraphicsUnit.Pixel),
        };
        Controls.Add(text);
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80 | 0x8 | 0x20 | 0x80000 | 0x08000000; // TOOLWINDOW | TOPMOST | TRANSPARENT | LAYERED | NOACTIVATE
            return cp;
        }
    }

    // progress: 0 → 1 表示从开始提示到黑屏
    public void SetProgress(double progress, int secondsLeft)
    {
        Opacity = 0.3 + 0.45 * Math.Max(0, Math.Min(1, progress));
        string s = secondsLeft + " 秒后黑屏\n动一下鼠标或按任意键取消";
        if (text.Text != s) text.Text = s;
    }
}
