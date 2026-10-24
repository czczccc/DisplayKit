using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

class DisplayState
{
    public string Device;
    public int Width, Height, RefreshRate;
    public int Scale; // 读取失败时为 0

    public string Describe()
    {
        string s = Width + "×" + Height + " · " + RefreshRate + "Hz";
        return Scale > 0 ? s + " · " + Scale + "%" : s;
    }
}

class DisplayInfo
{
    public string Device, Name;
    public bool Primary;
}

class Resolution
{
    public int Width, Height;
    public List<int> Rates = new List<int>(); // 从高到低
}

// 分辨率 / 刷新率 / 缩放的读取与切换 (来自 DisplayModeSwitch)
static class DisplayController
{
    // Windows 缩放档位, DisplayConfig 用相对"推荐缩放"的档位下标表示缩放
    public static readonly int[] DpiValues = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    // 配置里的显示器 (留空 = 主显示器) 解析成 \\.\DISPLAYn; 配置的显示器不在了就退回主显示器
    public static string ResolveDevice(string configured)
    {
        var all = ListDisplays();
        if (!string.IsNullOrEmpty(configured))
            foreach (var d in all)
                if (string.Equals(d.Device, configured.Trim(), StringComparison.OrdinalIgnoreCase)) return d.Device;
        foreach (var d in all)
            if (d.Primary) return d.Device;
        if (all.Count > 0) return all[0].Device;
        throw new InvalidOperationException("找不到显示器。");
    }

    public static List<DisplayInfo> ListDisplays()
    {
        var names = FriendlyNames();
        var result = new List<DisplayInfo>();
        var dd = Native.DISPLAY_DEVICE.Create();
        for (uint i = 0; Native.EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
            {
                string name;
                if (!names.TryGetValue(dd.DeviceName, out name) || string.IsNullOrEmpty(name))
                {
                    var mon = Native.DISPLAY_DEVICE.Create();
                    name = Native.EnumDisplayDevices(dd.DeviceName, 0, ref mon, 0) ? mon.DeviceString : dd.DeviceString;
                }
                result.Add(new DisplayInfo
                {
                    Device = dd.DeviceName,
                    Name = name,
                    Primary = (dd.StateFlags & Native.DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                });
            }
            dd = Native.DISPLAY_DEVICE.Create();
        }
        return result;
    }

    public static DisplayState GetState(string device)
    {
        var dm = Native.DEVMODE.Create();
        if (!Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref dm))
            throw new InvalidOperationException("读取 " + device + " 当前显示模式失败。");

        int scale = 0;
        try { scale = GetScale(device).Current; } catch { } // 缩放读不到时只显示分辨率

        return new DisplayState { Device = device, Width = dm.dmPelsWidth, Height = dm.dmPelsHeight, RefreshRate = dm.dmDisplayFrequency, Scale = scale };
    }

    public static bool Matches(DisplayState s, DisplayProfile p)
    {
        return s.Width == p.Width && s.Height == p.Height
            && (p.RefreshRate <= 0 || Math.Abs(s.RefreshRate - p.RefreshRate) <= 1)
            && (p.Scale <= 0 || s.Scale == 0 || s.Scale == p.Scale);
    }

    // 显示器支持的分辨率及刷新率, 按像素数从大到小
    public static List<Resolution> ListResolutions(string device)
    {
        var map = new Dictionary<long, Resolution>();
        var dm = Native.DEVMODE.Create();
        for (int i = 0; Native.EnumDisplaySettings(device, i, ref dm); i++)
        {
            long key = ((long)dm.dmPelsWidth << 32) | (uint)dm.dmPelsHeight;
            Resolution r;
            if (!map.TryGetValue(key, out r))
            {
                r = new Resolution { Width = dm.dmPelsWidth, Height = dm.dmPelsHeight };
                map[key] = r;
            }
            if (dm.dmDisplayFrequency > 1 && !r.Rates.Contains(dm.dmDisplayFrequency)) r.Rates.Add(dm.dmDisplayFrequency);
            dm = Native.DEVMODE.Create();
        }
        var list = new List<Resolution>(map.Values);
        foreach (var r in list) r.Rates.Sort((a, b) => b.CompareTo(a));
        list.Sort((a, b) =>
        {
            int c = ((long)b.Width * b.Height).CompareTo((long)a.Width * a.Height);
            return c != 0 ? c : b.Width.CompareTo(a.Width);
        });
        return list;
    }

    // 应用一个显示模式: 先改分辨率/刷新率, 再改缩放 (推荐缩放随分辨率变化, 顺序不能反)
    public static void Apply(string device, DisplayProfile p)
    {
        SetResolution(device, p.Width, p.Height, p.RefreshRate);
        if (p.Scale <= 0) return;

        // 分辨率刚切换完, DisplayConfig 偶尔还没刷新, 读回不对就稍等重试
        for (int attempt = 0; ; attempt++)
        {
            SetScale(device, p.Scale);
            if (GetScale(device).Current == p.Scale) break;
            if (attempt >= 3)
                throw new InvalidOperationException("缩放设置为 " + p.Scale + "% 后读回的值不一致，请到 Windows 显示设置里确认。");
            Thread.Sleep(500);
        }
    }

    public static void SetResolution(string device, int width, int height, int refreshRate)
    {
        var current = Native.DEVMODE.Create();
        if (Native.EnumDisplaySettings(device, Native.ENUM_CURRENT_SETTINGS, ref current)
            && current.dmPelsWidth == width && current.dmPelsHeight == height
            && (refreshRate <= 0 || Math.Abs(current.dmDisplayFrequency - refreshRate) <= 1)) // 59/60Hz 视为相同, 和 Matches 一致
            return;

        var dm = Native.DEVMODE.Create();
        dm.dmPelsWidth = width;
        dm.dmPelsHeight = height;
        dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT;
        if (refreshRate > 0)
        {
            dm.dmDisplayFrequency = refreshRate;
            dm.dmFields |= Native.DM_DISPLAYFREQUENCY;
        }

        string mode = width + "×" + height + (refreshRate > 0 ? " " + refreshRate + "Hz" : "");
        int test = Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, Native.CDS_TEST, IntPtr.Zero);
        if (test != Native.DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException("显卡/显示器不支持 " + mode + "（错误码 " + test + "）。\n" + DescribeSupportedModes(device, width, height));

        int rc = Native.ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, Native.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (rc == Native.DISP_CHANGE_RESTART)
            throw new InvalidOperationException("该模式需要重启电脑后生效。");
        if (rc != Native.DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException("切换到 " + mode + " 失败（错误码 " + rc + "）。");
    }

    static string DescribeSupportedModes(string device, int width, int height)
    {
        foreach (var r in ListResolutions(device))
            if (r.Width == width && r.Height == height)
            {
                var rates = new List<string>();
                foreach (int hz in r.Rates) rates.Add(hz + "Hz");
                return width + "×" + height + " 可用刷新率：" + string.Join(" / ", rates.ToArray());
            }
        return "这台显示器没有 " + width + "×" + height + " 这个分辨率。";
    }

    // ---------- 缩放 ----------

    public struct ScaleInfo { public int Min, Current, Recommended, Max; }

    public static ScaleInfo GetScale(string device)
    {
        var src = FindSource(device);
        var get = new Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET();
        get.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE;
        get.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET));
        get.header.adapterId = src.adapterId;
        get.header.id = src.id;

        int rc = Native.DisplayConfigGetDeviceInfo(ref get);
        if (rc != Native.ERROR_SUCCESS)
            throw new InvalidOperationException("读取缩放失败（错误码 " + rc + "）。");

        // minScaleRel 是负数, 其绝对值就是"推荐缩放"在档位表里的下标
        int rec = Math.Abs(get.minScaleRel);
        return new ScaleInfo
        {
            Recommended = DpiAt(rec),
            Min = DpiAt(rec + get.minScaleRel),
            Current = DpiAt(rec + get.curScaleRel),
            Max = DpiAt(rec + get.maxScaleRel),
        };
    }

    public static void SetScale(string device, int percent)
    {
        int target = Array.IndexOf(DpiValues, percent);
        if (target < 0)
            throw new ArgumentException("不支持的缩放 " + percent + "%。");

        var info = GetScale(device);
        if (info.Current == percent) return;
        if (percent < info.Min || percent > info.Max)
            throw new InvalidOperationException("当前分辨率下缩放只能在 " + info.Min + "%~" + info.Max + "% 之间，无法设为 " + percent + "%。");

        var src = FindSource(device);
        var set = new Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET();
        set.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE;
        set.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET));
        set.header.adapterId = src.adapterId;
        set.header.id = src.id;
        set.scaleRel = target - Array.IndexOf(DpiValues, info.Recommended);

        int rc = Native.DisplayConfigSetDeviceInfo(ref set);
        if (rc != Native.ERROR_SUCCESS)
            throw new InvalidOperationException("设置缩放为 " + percent + "% 失败（错误码 " + rc + "）。");
    }

    static int DpiAt(int index)
    {
        if (index < 0) index = 0;
        if (index >= DpiValues.Length) index = DpiValues.Length - 1;
        return DpiValues[index];
    }

    static Native.DISPLAYCONFIG_PATH_INFO[] QueryPaths()
    {
        uint numPaths, numModes;
        int rc = Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes);
        if (rc != Native.ERROR_SUCCESS)
            throw new InvalidOperationException("查询显示配置失败（错误码 " + rc + "）。");
        var paths = new Native.DISPLAYCONFIG_PATH_INFO[numPaths];
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[numModes];
        rc = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
        if (rc != Native.ERROR_SUCCESS)
            throw new InvalidOperationException("查询显示配置失败（错误码 " + rc + "）。");
        Array.Resize(ref paths, (int)numPaths);
        return paths;
    }

    static string SourceName(Native.DISPLAYCONFIG_PATH_INFO path)
    {
        var name = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        name.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        name.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME));
        name.header.adapterId = path.sourceInfo.adapterId;
        name.header.id = path.sourceInfo.id;
        return Native.DisplayConfigGetDeviceInfo(ref name) == Native.ERROR_SUCCESS ? name.viewGdiDeviceName : null;
    }

    static Native.DISPLAYCONFIG_PATH_SOURCE_INFO FindSource(string device)
    {
        foreach (var p in QueryPaths())
            if (string.Equals(SourceName(p), device, StringComparison.OrdinalIgnoreCase))
                return p.sourceInfo;
        throw new InvalidOperationException("在当前显示配置里找不到 " + device + "。");
    }

    // \\.\DISPLAYn -> 显示器型号名 (来自 EDID, 如 "LG OLED")
    static Dictionary<string, string> FriendlyNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in QueryPaths())
            {
                string src = SourceName(p);
                if (src == null || result.ContainsKey(src)) continue;
                var t = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                t.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                t.header.size = (uint)Marshal.SizeOf(typeof(Native.DISPLAYCONFIG_TARGET_DEVICE_NAME));
                t.header.adapterId = p.targetInfo.adapterId;
                t.header.id = p.targetInfo.id;
                if (Native.DisplayConfigGetDeviceInfo(ref t) == Native.ERROR_SUCCESS && !string.IsNullOrEmpty(t.monitorFriendlyDeviceName))
                    result[src] = t.monitorFriendlyDeviceName;
            }
        }
        catch { }
        return result;
    }
}
