using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// DDC/CI: 通过显卡直接读写显示器自身的设置 (亮度、对比度、输入源), 和按显示器菜单键效果一样.
// 每次读写要几十毫秒, 不要在 UI 线程上频繁调用 (写入走 DdcWriter)
static class Ddc
{
    public const byte Brightness = 0x10, Contrast = 0x12, Input = 0x60;

    static readonly Dictionary<string, int> maxCache = new Dictionary<string, int>(); // "设备|代码" -> 最大值

    // 打开显示器的物理句柄, 用完要 Close
    static Native.PHYSICAL_MONITOR[] Open(string device)
    {
        foreach (var s in Screen.AllScreens)
        {
            if (!string.Equals(s.DeviceName, device, StringComparison.OrdinalIgnoreCase)) continue;
            var b = s.Bounds;
            IntPtr hm = Native.MonitorFromPoint(new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 }, 2);
            uint n;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out n) || n == 0) return null;
            var arr = new Native.PHYSICAL_MONITOR[n];
            return Native.GetPhysicalMonitorsFromHMONITOR(hm, n, arr) ? arr : null;
        }
        return null;
    }

    static void Close(Native.PHYSICAL_MONITOR[] mons) { Native.DestroyPhysicalMonitors((uint)mons.Length, mons); }

    public static bool Get(string device, byte code, out int current, out int max)
    {
        current = max = 0;
        var mons = Open(device);
        if (mons == null) return false;
        try
        {
            uint c, m;
            if (!Native.GetVCPFeatureAndVCPFeatureReply(mons[0].hPhysicalMonitor, code, IntPtr.Zero, out c, out m)) return false;
            current = (int)c;
            max = (int)m;
            lock (maxCache) maxCache[device + "|" + code] = max;
            return true;
        }
        finally { Close(mons); }
    }

    public static bool Set(string device, byte code, int value)
    {
        var mons = Open(device);
        if (mons == null) return false;
        try
        {
            bool ok = true;
            foreach (var m in mons) ok &= Native.SetVCPFeature(m.hPhysicalMonitor, code, (uint)value);
            return ok;
        }
        finally { Close(mons); }
    }

    // 百分比和显示器原始值互换 (大多数显示器最大值就是 100)
    public static int GetPercent(string device, byte code)
    {
        int c, m;
        if (!Get(device, code, out c, out m) || m <= 0) return -1;
        return (int)Math.Round(c * 100.0 / m);
    }

    public static bool SetPercent(string device, byte code, int percent)
    {
        int max, c;
        bool known;
        lock (maxCache) known = maxCache.TryGetValue(device + "|" + code, out max);
        if (!known && !Get(device, code, out c, out max)) max = 100;
        if (max <= 0) max = 100;
        return Set(device, code, (int)Math.Round(Math.Max(0, Math.Min(100, percent)) * max / 100.0));
    }

    // 显示器声明支持的输入源 (能力字符串里的 60(...)), 读不到时返回 null. 要一两秒, 放后台调用
    public static List<int> Inputs(string device)
    {
        var mons = Open(device);
        if (mons == null) return null;
        try
        {
            IntPtr h = mons[0].hPhysicalMonitor;
            uint len;
            if (!Native.GetCapabilitiesStringLength(h, out len) || len == 0) return null;
            var sb = new StringBuilder((int)len);
            if (!Native.CapabilitiesRequestAndCapabilitiesReply(h, sb, len)) return null;
            var m = Regex.Match(sb.ToString(), @"(?<![0-9A-Fa-f])60\s*\(([0-9A-Fa-f\s]+)\)");
            if (!m.Success) return null;
            var list = new List<int>();
            foreach (var t in m.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(Convert.ToInt32(t, 16));
            return list.Count > 0 ? list : null;
        }
        catch { return null; }
        finally { Close(mons); }
    }

    // MCCS 标准里的输入源编号; 不同显示器不一定都按标准来
    public static readonly int[] StandardInputs = { 0x0F, 0x10, 0x11, 0x12, 0x1B, 0x03, 0x01 };

    public static string InputName(int v)
    {
        switch (v)
        {
            case 0x01: return "VGA";
            case 0x02: return "VGA 2";
            case 0x03: return "DVI";
            case 0x04: return "DVI 2";
            case 0x0F: return "DisplayPort 1";
            case 0x10: return "DisplayPort 2";
            case 0x11: return "HDMI 1";
            case 0x12: return "HDMI 2";
            case 0x1B: return "USB-C";
        }
        return "输入 0x" + v.ToString("X2");
    }

    // 命令行写法: dp1 / dp2 / hdmi1 / hdmi2 / usbc, 或者数字 (0x11 / 17)
    public static bool ParseInput(string s, out int v)
    {
        switch (s.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", ""))
        {
            case "dp": case "dp1": case "displayport": case "displayport1": v = 0x0F; return true;
            case "dp2": case "displayport2": v = 0x10; return true;
            case "hdmi": case "hdmi1": v = 0x11; return true;
            case "hdmi2": v = 0x12; return true;
            case "usbc": case "typec": v = 0x1B; return true;
            case "dvi": v = 0x03; return true;
            case "vga": v = 0x01; return true;
        }
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out v) && v > 0 && v < 256;
        return int.TryParse(s, out v) && v > 0 && v < 256;
    }
}

// 后台写入: 同一项只保留最新的值, 每次写完至少隔 150ms (拖动滑块时不会把指令堆满, 也少写几次显示器存储)
class DdcWriter
{
    readonly object sync = new object();
    readonly Dictionary<byte, KeyValuePair<string, int>> pending = new Dictionary<byte, KeyValuePair<string, int>>();
    bool running;

    public void SetPercent(string device, byte code, int percent)
    {
        lock (sync)
        {
            pending[code] = new KeyValuePair<string, int>(device, percent);
            if (running) return;
            running = true;
        }
        ThreadPool.QueueUserWorkItem(Run);
    }

    void Run(object state)
    {
        while (true)
        {
            byte code;
            KeyValuePair<string, int> item;
            lock (sync)
            {
                if (pending.Count == 0) { running = false; return; }
                var e = pending.GetEnumerator();
                e.MoveNext();
                code = e.Current.Key;
                item = e.Current.Value;
                pending.Remove(code);
            }
            if (!Ddc.SetPercent(item.Key, code, item.Value)) Log.Write("DDC/CI 写入失败: 0x" + code.ToString("X2") + " = " + item.Value + "%");
            Thread.Sleep(150);
        }
    }
}
