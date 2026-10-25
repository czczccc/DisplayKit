using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Windows 自带的"关闭屏幕"时间 (当前电源方案, 插电/电池两个值, 单位秒, 0 = 从不) 和屏保开关.
// 接管时改成从不 / 关闭, 由 DisplayKit 的黑屏和关闭显示器代替; 原来的值由 App 记在配置里, 交还时写回去
static class SystemIdle
{
    static Guid VideoSubgroup = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
    static Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    public static bool ActiveScheme(out Guid scheme)
    {
        scheme = Guid.Empty;
        IntPtr p;
        try
        {
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return false;
        }
        catch { return false; }
        scheme = (Guid)Marshal.PtrToStructure(p, typeof(Guid));
        Native.LocalFree(p);
        return true;
    }

    public static bool ReadTimeout(Guid scheme, out uint ac, out uint dc)
    {
        dc = 0;
        return Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, out ac) == 0
            && Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, out dc) == 0;
    }

    // 写进去之后, 如果是当前方案, 还要重新激活一次才会生效
    public static bool WriteTimeout(Guid scheme, uint ac, uint dc)
    {
        if (Native.PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, ac) != 0
            || Native.PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref VideoSubgroup, ref VideoIdle, dc) != 0) return false;
        Guid active;
        if (ActiveScheme(out active) && active == scheme) Native.PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        return true;
    }

    public static bool ScreenSaverActive
    {
        get
        {
            int on = 0;
            Native.SystemParametersInfo(Native.SPI_GETSCREENSAVEACTIVE, 0, ref on, 0);
            return on != 0;
        }
    }

    public static bool SetScreenSaverActive(bool on)
    {
        return Native.SystemParametersInfo(Native.SPI_SETSCREENSAVEACTIVE, on ? 1u : 0u, IntPtr.Zero, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
    }

    // 组策略强制了屏保 (公司电脑常见) 时改了也会被改回去, 不去碰它
    public static bool ScreenSaverByPolicy
    {
        get
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Policies\Microsoft\Windows\Control Panel\Desktop"))
                    return k != null && (k.GetValue("ScreenSaveActive") != null || k.GetValue("ScreenSaveTimeOut") != null);
            }
            catch { return false; }
        }
    }
}
