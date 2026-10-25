using System;
using System.Threading;
using Microsoft.Win32;

// 日出日落时间 (NOAA 简化算法, 误差一两分钟, 不需要联网)
static class Sun
{
    const double Zenith = 90.833; // 太阳上沿刚好在地平线 (含大气折射)

    static double Sin(double deg) { return Math.Sin(deg * Math.PI / 180); }
    static double Cos(double deg) { return Math.Cos(deg * Math.PI / 180); }
    static double Norm(double v, double max) { v %= max; return v < 0 ? v + max : v; }

    // 某天的日出 / 日落 (本地时间). 极昼极夜时返回 false, polarDay 表示整天都是白天
    public static bool Times(DateTime day, double lat, double lon, out DateTime rise, out DateTime set, out bool polarDay)
    {
        rise = set = DateTime.MinValue;
        polarDay = false;
        double r, s;
        int state = Calc(day, lat, lon, true, out r);
        if (state == 0) state = Calc(day, lat, lon, false, out s); else s = 0;
        if (state != 0) { polarDay = state > 0; return false; }
        rise = ToLocal(day, r);
        set = ToLocal(day, s);
        return true;
    }

    public static bool IsDay(DateTime now, double lat, double lon)
    {
        DateTime rise, set;
        bool polarDay;
        if (!Times(now, lat, lon, out rise, out set, out polarDay)) return polarDay;
        return now >= rise && now < set;
    }

    // UT 小时数换成 day 这一天的本地时间 (时区和经度对不上时可能跨天, 拉回同一天)
    static DateTime ToLocal(DateTime day, double ut)
    {
        var t = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Utc).AddHours(ut).ToLocalTime();
        if (t.Date > day.Date) t = t.AddDays(-1);
        else if (t.Date < day.Date) t = t.AddDays(1);
        return t;
    }

    // 返回 0 = 正常, 1 = 整天不落 (极昼), -1 = 整天不升 (极夜)
    static int Calc(DateTime day, double lat, double lon, bool sunrise, out double ut)
    {
        ut = 0;
        double lngHour = lon / 15;
        double t = day.DayOfYear + ((sunrise ? 6 : 18) - lngHour) / 24;
        double m = 0.9856 * t - 3.289;
        double l = Norm(m + 1.916 * Sin(m) + 0.020 * Sin(2 * m) + 282.634, 360);
        double ra = Norm(Math.Atan(0.91764 * Math.Tan(l * Math.PI / 180)) * 180 / Math.PI, 360);
        ra = (ra + Math.Floor(l / 90) * 90 - Math.Floor(ra / 90) * 90) / 15;
        double sinDec = 0.39782 * Sin(l), cosDec = Math.Cos(Math.Asin(sinDec));
        double cosH = (Cos(Zenith) - sinDec * Sin(lat)) / (cosDec * Cos(lat));
        if (cosH > 1) return -1;
        if (cosH < -1) return 1;
        double h = Math.Acos(cosH) * 180 / Math.PI;
        if (sunrise) h = 360 - h;
        ut = Norm(h / 15 + ra - 0.06571 * t - 6.622 - lngHour, 24);
        return 0;
    }
}

// Windows 的深色 / 浅色模式 (应用和系统一起切, 和"设置 > 个性化 > 颜色"里选浅色/深色相同)
static class SystemTheme
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsLight()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                return !(v is int) || (int)v != 0;
            }
        }
        catch { return true; }
    }

    public static void Set(bool light)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
        {
            k.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        }
        // 通知任务栏和各程序重新读取主题; 广播会挨个等窗口响应, 放到后台线程
        ThreadPool.QueueUserWorkItem(delegate
        {
            IntPtr result;
            Native.SendMessageTimeout(Native.HWND_BROADCAST, Native.WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", Native.SMTO_ABORTIFHUNG, 1000, out result);
        });
    }
}
