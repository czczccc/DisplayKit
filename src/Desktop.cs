using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// 鼠标指针闲置隐藏: 把所有系统指针换成透明的, 恢复时让系统重新加载用户的指针方案
static class CursorHider
{
    // OCR_NORMAL / IBEAM / WAIT / CROSS / UP / SIZENWSE / SIZENESW / SIZEWE / SIZENS / SIZEALL / NO / HAND / APPSTARTING / HELP
    static readonly uint[] Ids = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651 };
    static volatile bool hidden;

    public static bool Hidden { get { return hidden; } }

    public static void Hide()
    {
        if (hidden) return;
        var and = new byte[128]; var xor = new byte[128];
        for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
        foreach (uint id in Ids)
        {
            // SetSystemCursor 会接管并销毁传进去的指针, 每种都要新建一个
            IntPtr c = Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, and, xor);
            if (c != IntPtr.Zero && !Native.SetSystemCursor(c, id)) Native.DestroyCursor(c);
        }
        hidden = true;
    }

    // 启动和退出时也调用一次: 万一上次异常退出时指针还是透明的
    public static void Restore()
    {
        hidden = false;
        Native.SystemParametersInfo(Native.SPI_SETCURSORS, 0, IntPtr.Zero, 0);
    }
}

// 桌面图标显示/隐藏, 和桌面右键"查看 > 显示桌面图标"相同
static class DesktopIcons
{
    const int ToggleCommand = 0x7402;

    static IntPtr DefView()
    {
        IntPtr v = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (v != IntPtr.Zero) return v;
        // 用过动态壁纸等程序后, 图标所在的 DefView 会挪到某个 WorkerW 下
        IntPtr w = IntPtr.Zero;
        while ((w = Native.FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
        {
            v = Native.FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (v != IntPtr.Zero) return v;
        }
        return IntPtr.Zero;
    }

    public static bool Visible
    {
        get
        {
            IntPtr list = Native.FindWindowEx(DefView(), IntPtr.Zero, "SysListView32", null);
            return list == IntPtr.Zero || Native.IsWindowVisible(list);
        }
    }

    public static bool Toggle()
    {
        IntPtr v = DefView();
        if (v == IntPtr.Zero) return false;
        IntPtr result;
        // 同步发送, 返回时状态已经变了 (资源管理器卡住时最多等 1 秒)
        Native.SendMessageTimeout(v, Native.WM_COMMAND, new IntPtr(ToggleCommand), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 1000, out result);
        return true;
    }
}

// 微软的 Windows HDR Calibration 应用: 已安装就直接打开, 没装就打开 Store 的安装页
static class HdrCalibration
{
    const string StoreUrl = "ms-windows-store://pdp/?ProductId=9N7F2SM5D1LR";
    const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    // 已安装时返回 "包系列名!应用 ID", 用来通过 shell:AppsFolder 启动
    static string FindApp()
    {
        try
        {
            using (var root = Registry.CurrentUser.OpenSubKey(PackagesKey))
            {
                if (root == null) return null;
                foreach (string full in root.GetSubKeyNames())
                {
                    if (full.IndexOf("HDRCalibration", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    using (var k = root.OpenSubKey(full))
                    {
                        string folder = k == null ? null : k.GetValue("PackageRootFolder") as string;
                        if (folder == null || !File.Exists(Path.Combine(folder, "AppxManifest.xml"))) continue;
                        var m = Regex.Match(File.ReadAllText(Path.Combine(folder, "AppxManifest.xml")), "<Application\\s[^>]*\\bId=\"([^\"]+)\"");
                        if (!m.Success) continue;
                        // 完整包名 = 名称_版本_架构_资源_发布者ID, 包系列名 = 名称_发布者ID
                        var parts = full.Split('_');
                        return parts[0] + "_" + parts[parts.Length - 1] + "!" + m.Groups[1].Value;
                    }
                }
            }
        }
        catch (Exception ex) { Log.Write("查找 HDR 校准应用失败: " + ex.Message); }
        return null;
    }

    public static bool Installed { get { return FindApp() != null; } }

    public static void Open()
    {
        string app = FindApp();
        if (app == null) { App.OpenUrl(StoreUrl); return; }
        try { System.Diagnostics.Process.Start("explorer.exe", @"shell:AppsFolder\" + app); }
        catch (Exception ex) { Log.Write("打开 HDR 校准失败: " + ex.Message); App.OpenUrl(StoreUrl); }
    }
}

static class Wallpaper
{
    public static string Current()
    {
        var sb = new StringBuilder(520);
        return Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) ? sb.ToString() : "";
    }

    // 设置壁纸会广播通知, 可能要等一会儿, 放到后台线程
    public static void Set(string path)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, path, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE))
                Log.Write("设置壁纸失败: " + path);
        });
    }
}

// 夜间暖色: 按色温调整每台显示器的调色曲线 (gamma ramp), 在原曲线上叠加, 不会冲掉显示器校色
static class NightTint
{
    public const int Neutral = 6500;
    static readonly Dictionary<string, ushort[]> baseRamps = new Dictionary<string, ushort[]>(StringComparer.OrdinalIgnoreCase);
    static bool failed;

    public static bool Active { get { return baseRamps.Count > 0; } }
    public static bool Failed { get { return failed; } } // 显示器不接受调色曲线 (比如开着 HDR)

    // 色温对应的 RGB 系数 (黑体辐射近似, 以 6500K 为白)
    static void Multipliers(double kelvin, out double r, out double g, out double b)
    {
        r = g = b = 1;
        if (kelvin >= Neutral) return;
        double t = kelvin / 100;
        g = (99.4708025861 * Math.Log(t) - 161.1195681661) / (99.4708025861 * Math.Log(Neutral / 100.0) - 161.1195681661);
        b = (138.5177312231 * Math.Log(t - 10) - 305.0447927307) / (138.5177312231 * Math.Log(Neutral / 100.0 - 10) - 305.0447927307);
        g = Math.Max(0, Math.Min(1, g));
        b = Math.Max(0, Math.Min(1, b));
    }

    public static void Apply(double kelvin)
    {
        double mr, mg, mb;
        Multipliers(kelvin, out mr, out mg, out mb);
        bool anyFailed = false;
        foreach (var s in Screen.AllScreens)
        {
            IntPtr dc = Native.CreateDC("DISPLAY", s.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            try
            {
                ushort[] orig;
                if (!baseRamps.TryGetValue(s.DeviceName, out orig))
                {
                    orig = new ushort[768];
                    if (!Native.GetDeviceGammaRamp(dc, orig))
                        for (int i = 0; i < 256; i++) orig[i] = orig[256 + i] = orig[512 + i] = (ushort)(i * 257);
                    baseRamps[s.DeviceName] = orig;
                }
                var ramp = new ushort[768];
                for (int i = 0; i < 256; i++)
                {
                    ramp[i] = (ushort)(orig[i] * mr);
                    ramp[256 + i] = (ushort)(orig[256 + i] * mg);
                    ramp[512 + i] = (ushort)(orig[512 + i] * mb);
                }
                if (!Native.SetDeviceGammaRamp(dc, ramp)) anyFailed = true;
            }
            finally { Native.DeleteDC(dc); }
        }
        if (anyFailed && !failed) Log.Write("有显示器不接受调色曲线 (HDR 开启或驱动不支持)");
        failed = anyFailed;
    }

    // 恢复原来的曲线, 之后不再碰调色曲线 (不和其他调色工具抢)
    public static void Restore()
    {
        foreach (var s in Screen.AllScreens)
        {
            ushort[] orig;
            if (!baseRamps.TryGetValue(s.DeviceName, out orig)) continue;
            IntPtr dc = Native.CreateDC("DISPLAY", s.DeviceName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) continue;
            Native.SetDeviceGammaRamp(dc, orig);
            Native.DeleteDC(dc);
        }
        baseRamps.Clear();
        failed = false;
    }
}
