using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

static class Log
{
    static readonly object sync = new object(); // 钩子线程也会写日志

    public static void Write(string msg)
    {
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                string path = Path.Combine(Settings.Dir, "log.txt");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 200 * 1024)
                {
                    // 满了就挪到 log.old.txt, 保留上一段日志
                    string old = Path.Combine(Settings.Dir, "log.old.txt");
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}

// 按键组合 (修饰键 + 主键)
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

    // 当前按住的修饰键 (不含 exceptVk 本身). 在低级钩子里调用时, 反映的是这次按键之前的状态
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
        return wantVk != 0 && Normalize(vk) == wantVk && mods == (wantMods & ~ModBit(wantVk));
    }

    // 两个设置好的快捷键是否相同
    public static bool Same(int mods1, int vk1, int mods2, int vk2)
    {
        return vk1 != 0 && vk1 == vk2 && (mods1 & ~ModBit(vk1)) == (mods2 & ~ModBit(vk2));
    }

    // 全局快捷键是否可用, 不可用时返回原因
    public static string Validate(int mods, int vk)
    {
        if (IsModifier(vk)) return "快捷键不能只是修饰键，请重新按。";
        bool special = (vk >= 0x70 && vk <= 0x87) || vk == 0x13 || vk == 0x91; // F1-F24 / Pause / ScrollLock
        if (mods == 0 && !special) return "快捷键需要带 Ctrl / Alt / Shift / Win，或使用 F1–F24、Pause、ScrollLock，以免打字时误触。请重新按。";
        return null;
    }

    // 显示用名称 (中文)
    public static string Name(int mods, int vk)
    {
        if (vk == 0) return "";
        var parts = new List<string>();
        int n = Normalize(vk);
        if ((mods & Ctrl) != 0 && n != 0x11) parts.Add("Ctrl");
        if ((mods & Shift) != 0 && n != 0x10) parts.Add("Shift");
        if ((mods & Alt) != 0 && n != 0x12) parts.Add("Alt");
        if ((mods & Win) != 0 && n != 0x5B) parts.Add("Win");
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

    // 配置文件里的写法, 如 Ctrl+Alt+F10
    public static string Format(int mods, int vk)
    {
        if (vk == 0) return "";
        var parts = new List<string>();
        int n = Normalize(vk);
        if ((mods & Ctrl) != 0 && n != 0x11) parts.Add("Ctrl");
        if ((mods & Shift) != 0 && n != 0x10) parts.Add("Shift");
        if ((mods & Alt) != 0 && n != 0x12) parts.Add("Alt");
        if ((mods & Win) != 0 && n != 0x5B) parts.Add("Win");
        switch (n)
        {
            case 0x10: parts.Add("Shift"); break;
            case 0x11: parts.Add("Ctrl"); break;
            case 0x12: parts.Add("Alt"); break;
            case 0x5B: parts.Add("Win"); break;
            default: parts.Add(((Keys)n).ToString()); break;
        }
        return string.Join("+", parts.ToArray());
    }

    public static bool Parse(string text, out int mods, out int vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return false;
        var tokens = text.Split('+');
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i].Trim();
            if (t.Length == 0) return false;
            int bit = 0, modVk = 0;
            switch (t.ToLowerInvariant())
            {
                case "ctrl": case "control": bit = Ctrl; modVk = 0x11; break;
                case "alt": bit = Alt; modVk = 0x12; break;
                case "shift": bit = Shift; modVk = 0x10; break;
                case "win": case "windows": bit = Win; modVk = 0x5B; break;
            }
            if (bit != 0)
            {
                // 最后一个词是修饰键且前面没有主键: 这个修饰键本身就是按键 (如唤醒键 = Ctrl)
                if (i == tokens.Length - 1 && vk == 0) vk = modVk;
                else mods |= bit;
                continue;
            }
            if (vk != 0) return false;
            if (t.Length == 1 && char.IsDigit(t[0])) t = "D" + t;
            Keys k;
            if (!Enum.TryParse(t, true, out k) || (int)k <= 0 || (int)k > 255) return false;
            vk = (int)k;
        }
        return vk != 0;
    }
}

class DisplayProfile
{
    public string Name = "";
    public int Width, Height;
    public int RefreshRate; // 0 = 不改
    public int Scale;       // 0 = 不改
    public int HotkeyVk, HotkeyMods;
    public List<string> AutoApps = new List<string>(); // 这些程序运行时自动切到此模式 (进程名, 小写, 不含 .exe)
    public bool Verified; // 用户已确认过这个模式能正常显示, 之后切换不再询问
    public const int HdrKeep = 0, HdrOn = 1, HdrOff = 2;
    public int Hdr = HdrKeep;
    public int Brightness = -1; // 切换时把显示器亮度 (DDC/CI) 调到这个百分比, -1 = 不改

    public string Describe()
    {
        string s = Width + "×" + Height;
        s += RefreshRate > 0 ? " · " + RefreshRate + "Hz" : " · 刷新率不变";
        if (Scale > 0) s += " · " + Scale + "%";
        if (Hdr == HdrOn) s += " · HDR 开";
        else if (Hdr == HdrOff) s += " · HDR 关";
        if (Brightness >= 0) s += " · 亮度 " + Brightness + "%";
        return s;
    }

    public DisplayProfile Clone()
    {
        var p = (DisplayProfile)MemberwiseClone();
        p.AutoApps = new List<string>(AutoApps);
        return p;
    }

    public static List<string> SplitList(string v)
    {
        var list = new List<string>();
        foreach (var name in v.Split('|'))
            if (name.Trim().Length > 0) list.Add(name.Trim().ToLowerInvariant());
        return list;
    }
}

class Settings
{
    public const int WakeAny = 0, WakeAnyKey = 1, WakeKey = 2;

    // ----- 防烧屏 -----
    public int TimeoutSec = 180;        // 无操作多久后黑屏
    public int MoveThreshold = 60;      // 鼠标移动超过多少像素才算"真的在用"
    public int PowerOffMin = 0;         // 黑屏多久后再真正关闭显示器, 0 = 不关
    public bool SkipFullscreen = true;  // 前台是全屏应用时不黑屏
    public bool SkipAudio = true;       // 有声音播放时不黑屏
    public bool Gamepad = true;         // 手柄操作算作活动
    public int WakeMode = WakeKey;
    public int WakeVk = 0x20, WakeMods = 0;                          // 唤醒键, 默认空格
    public int BlankVk = (int)Keys.B, BlankMods = KeyCombo.Ctrl | KeyCombo.Alt; // 立即黑屏, 默认 Ctrl+Alt+B
    public List<string> Whitelist = new List<string>(); // 进程名, 小写, 不含 .exe
    public List<string> BlankDevices = new List<string>(); // 要黑屏的显示器 (\\.\DISPLAYn), 空 = 全部
    public int PowerOffVk, PowerOffMods;    // 立即关闭显示器
    public int IconsVk, IconsMods;          // 显示/隐藏桌面图标
    public int CursorHideSec;               // 鼠标多久不动隐藏指针, 0 = 不隐藏
    public int WarnSec = 5;                 // 黑屏前几秒开始变暗提示, 0 = 不提示
    public int DimAfterSec;                 // 闲置多久先把显示器亮度 (DDC/CI) 降下来, 0 = 不降
    public int DimLevel = 20;               // 降到的亮度百分比
    public int DimmedFrom = -1;             // 降亮度前的亮度; 程序异常退出没来得及恢复时, 下次启动据此恢复
    public int BrightUpVk, BrightUpMods, BrightDownVk, BrightDownMods; // 调亮 / 调暗快捷键
    public bool ScheduleOn;                 // 时段规则: 某个时段内用另一个黑屏等待时间
    public int ScheduleStart = 23, ScheduleEnd = 7; // 整点, 可以跨午夜
    public int ScheduleTimeoutSec = 60;     // 时段内的黑屏等待时间, 0 = 不自动黑屏
    public int LockScreenOffSec = 10;   // 锁屏后多少秒关闭显示器, 0 = 不关
    public bool CheckUpdates = true;
    public bool AutoInstallUpdates = true; // 发现新版本后自动下载并重启到新版本

    // ----- 显示模式 -----
    public string DisplayDevice = "";   // 留空 = 主显示器
    public int ToggleVk, ToggleMods;    // 在模式间循环切换
    public int HdrVk, HdrMods;          // 开关 HDR
    public List<DisplayProfile> Profiles = new List<DisplayProfile>();

    // ----- 外观 -----
    public bool AutoTheme;                  // 跟随日出日落切换深色/浅色
    public bool UseSystemLocation = true;   // 用 Windows 定位, 否则用手动填的坐标
    public double Latitude = double.NaN, Longitude = double.NaN; // 最近一次的位置, 定位失败时沿用
    public int NightKelvin;                 // 日落后屏幕色温, 0 = 不调
    public bool AutoBrightness;             // 跟随日出日落调显示器亮度 (DDC/CI)
    public int DayBrightness = 80, NightBrightness = 40;
    public string LightWallpaper = "", DarkWallpaper = ""; // 浅色/深色模式下的壁纸, 空 = 不换

    public bool HasLocation { get { return !double.IsNaN(Latitude) && !double.IsNaN(Longitude); } }
    public bool NeedsLocation { get { return AutoTheme || NightKelvin > 0 || AutoBrightness; } }

    public static string FormatCoord(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }

    // "纬度, 经度", 允许中文逗号或空格分隔
    public static bool ParseLocation(string text, out double lat, out double lon)
    {
        lat = lon = double.NaN;
        var parts = text.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat) && Math.Abs(lat) <= 90
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon) && Math.Abs(lon) <= 180;
    }

    public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DisplayKit"); } }
    public static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }
    static string LegacyDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OledGuard"); } }

    // 本程序以前叫 OledGuard: 第一次运行时把旧的配置搬过来
    static void MigrateFromOledGuard()
    {
        try
        {
            string old = Path.Combine(LegacyDir, "config.ini");
            if (File.Exists(FilePath) || !File.Exists(old)) return;
            Directory.CreateDirectory(Dir);
            File.Copy(old, FilePath);
            Log.Write("已从 OledGuard 迁移配置");
        }
        catch (Exception ex) { Log.Write("迁移 OledGuard 配置失败: " + ex.Message); }
    }

    public DisplayProfile FindProfile(string name)
    {
        return Profiles.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    // 内置模式: 4K 办公 / 1080p 游戏 (快捷键沿用 DisplayModeSwitch 的默认值)
    public static List<DisplayProfile> BuiltInProfiles()
    {
        return new List<DisplayProfile>
        {
            new DisplayProfile { Name = "办公", Width = 3840, Height = 2160, RefreshRate = 60, Scale = 200, HotkeyVk = (int)Keys.F10, HotkeyMods = KeyCombo.Ctrl | KeyCombo.Alt },
            new DisplayProfile { Name = "游戏", Width = 1920, Height = 1080, RefreshRate = 240, Scale = 100, HotkeyVk = (int)Keys.F11, HotkeyMods = KeyCombo.Ctrl | KeyCombo.Alt },
        };
    }

    // 第一次使用 (配置里还没有显示模式相关设置) 时加入内置模式
    void AddBuiltInsOnFirstRun()
    {
        Profiles.AddRange(BuiltInProfiles());
        ToggleVk = (int)Keys.F12;
        ToggleMods = KeyCombo.Ctrl | KeyCombo.Alt;
    }

    public static Settings Load()
    {
        var s = new Settings();
        bool sawDisplay = false;
        MigrateFromOledGuard();
        try
        {
            if (!File.Exists(FilePath)) { s.AddBuiltInsOnFirstRun(); return s; }
            string section = "guard";
            DisplayProfile mode = null;
            foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    mode = null;
                    if (name.StartsWith("Mode:", StringComparison.OrdinalIgnoreCase))
                    {
                        mode = new DisplayProfile { Name = name.Substring(5).Trim() };
                        s.Profiles.Add(mode);
                    }
                    section = name.ToLowerInvariant();
                    if (section == "display") sawDisplay = true;
                    continue;
                }
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                if (mode != null) ReadMode(mode, k, v);
                else if (section == "display") s.ReadDisplay(k, v);
                else if (section == "appearance") s.ReadAppearance(k, v);
                else s.ReadGuard(k, v); // 没有段落头的旧版配置也当作防烧屏设置
            }
            s.Profiles.RemoveAll(p => p.Width <= 0 || p.Height <= 0 || p.Name.Length == 0);
            // 用户自己删光模式后不再自动加回来 (那时配置里已经有 [Display] 段)
            if (!sawDisplay && s.Profiles.Count == 0) s.AddBuiltInsOnFirstRun();
        }
        catch (Exception ex) { Log.Write("读取配置失败: " + ex.Message); }
        return s;
    }

    void ReadGuard(string k, string v)
    {
        int n, m;
        switch (k)
        {
            case "TimeoutSec": if (int.TryParse(v, out n) && n >= 10) TimeoutSec = n; break;
            case "MoveThreshold": if (int.TryParse(v, out n) && n > 0) MoveThreshold = n; break;
            case "PowerOffMin": if (int.TryParse(v, out n) && n >= 0) PowerOffMin = n; break;
            case "WakeMode": if (int.TryParse(v, out n) && n >= 0 && n <= 2) WakeMode = n; break;
            case "SkipFullscreen": SkipFullscreen = v == "1"; break;
            case "SkipAudio": SkipAudio = v == "1"; break;
            case "Gamepad": Gamepad = v == "1"; break;
            case "WakeKey": if (KeyCombo.Parse(v, out m, out n)) { WakeVk = n; WakeMods = m; } break;
            case "BlankKey": if (KeyCombo.Parse(v, out m, out n)) { BlankVk = n; BlankMods = m; } break;
            case "PowerOffKey": if (KeyCombo.Parse(v, out m, out n)) { PowerOffVk = n; PowerOffMods = m; } break;
            case "IconsKey": if (KeyCombo.Parse(v, out m, out n)) { IconsVk = n; IconsMods = m; } break;
            case "CursorHideSec": if (int.TryParse(v, out n) && n >= 0) CursorHideSec = n; break;
            case "WarnSec": if (int.TryParse(v, out n) && n >= 0) WarnSec = n; break;
            case "DimAfterSec": if (int.TryParse(v, out n) && n >= 0) DimAfterSec = n; break;
            case "DimLevel": if (int.TryParse(v, out n) && n >= 0 && n <= 100) DimLevel = n; break;
            case "DimmedFrom": if (int.TryParse(v, out n) && n >= -1 && n <= 100) DimmedFrom = n; break;
            case "BrightUpKey": if (KeyCombo.Parse(v, out m, out n)) { BrightUpVk = n; BrightUpMods = m; } break;
            case "BrightDownKey": if (KeyCombo.Parse(v, out m, out n)) { BrightDownVk = n; BrightDownMods = m; } break;
            case "ScheduleOn": ScheduleOn = v == "1"; break;
            case "ScheduleStart": if (int.TryParse(v, out n) && n >= 0 && n < 24) ScheduleStart = n; break;
            case "ScheduleEnd": if (int.TryParse(v, out n) && n >= 0 && n < 24) ScheduleEnd = n; break;
            case "ScheduleTimeoutSec": if (int.TryParse(v, out n) && (n == 0 || n >= 10)) ScheduleTimeoutSec = n; break;
            // v0.1 的写法
            case "WakeVk": if (int.TryParse(v, out n) && n > 0 && n < 256) WakeVk = n; break;
            case "WakeMods": if (int.TryParse(v, out n) && n >= 0 && n < 16) WakeMods = n; break;
            case "BlankVk": if (int.TryParse(v, out n) && n > 0 && n < 256) BlankVk = n; break;
            case "BlankMods": if (int.TryParse(v, out n) && n >= 0 && n < 16) BlankMods = n; break;
            case "Whitelist": Whitelist = DisplayProfile.SplitList(v); break;
            case "BlankDevices":
                BlankDevices.Clear();
                foreach (var d in v.Split('|'))
                    if (d.Trim().Length > 0) BlankDevices.Add(d.Trim());
                break;
            case "LockScreenOffSec": if (int.TryParse(v, out n) && n >= 0) LockScreenOffSec = n; break;
            case "CheckUpdates": CheckUpdates = v == "1"; break;
            case "AutoInstallUpdates": AutoInstallUpdates = v == "1"; break;
        }
    }

    void ReadDisplay(string k, string v)
    {
        int m, n;
        if (k == "Device") DisplayDevice = v;
        else if (k == "ToggleHotkey" && KeyCombo.Parse(v, out m, out n)) { ToggleVk = n; ToggleMods = m; }
        else if (k == "HdrHotkey" && KeyCombo.Parse(v, out m, out n)) { HdrVk = n; HdrMods = m; }
    }

    void ReadAppearance(string k, string v)
    {
        double lat, lon;
        int n;
        if (k == "AutoTheme") AutoTheme = v == "1";
        else if (k == "NightKelvin" && int.TryParse(v, out n) && (n == 0 || (n >= 3000 && n < 6500))) NightKelvin = n;
        else if (k == "AutoBrightness") AutoBrightness = v == "1";
        else if (k == "DayBrightness" && int.TryParse(v, out n) && n >= 0 && n <= 100) DayBrightness = n;
        else if (k == "NightBrightness" && int.TryParse(v, out n) && n >= 0 && n <= 100) NightBrightness = n;
        else if (k == "LightWallpaper") LightWallpaper = v;
        else if (k == "DarkWallpaper") DarkWallpaper = v;
        else if (k == "UseSystemLocation") UseSystemLocation = v == "1";
        else if (k == "Location" && ParseLocation(v, out lat, out lon)) { Latitude = lat; Longitude = lon; }
    }

    static void ReadMode(DisplayProfile p, string k, string v)
    {
        int n, m;
        if (string.Equals(k, "Width", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out n)) p.Width = n;
        else if (string.Equals(k, "Height", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out n)) p.Height = n;
        else if (string.Equals(k, "RefreshRate", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out n)) p.RefreshRate = Math.Max(0, n);
        else if (string.Equals(k, "Scale", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.TrimEnd('%'), out n)) p.Scale = Math.Max(0, n);
        else if (string.Equals(k, "Hotkey", StringComparison.OrdinalIgnoreCase) && KeyCombo.Parse(v, out m, out n)) { p.HotkeyVk = n; p.HotkeyMods = m; }
        else if (k == "AutoApps") p.AutoApps = DisplayProfile.SplitList(v);
        else if (k == "Verified") p.Verified = v == "1";
        else if (k == "Hdr" && int.TryParse(v, out n) && n >= 0 && n <= 2) p.Hdr = n;
        else if (k == "Brightness" && int.TryParse(v, out n) && n >= -1 && n <= 100) p.Brightness = n;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            sb.AppendLine("; DisplayKit 配置文件, 一般在设置窗口里修改即可");
            sb.AppendLine("[Guard]");
            sb.AppendLine("TimeoutSec=" + TimeoutSec);
            sb.AppendLine("MoveThreshold=" + MoveThreshold);
            sb.AppendLine("PowerOffMin=" + PowerOffMin);
            sb.AppendLine("SkipFullscreen=" + (SkipFullscreen ? "1" : "0"));
            sb.AppendLine("SkipAudio=" + (SkipAudio ? "1" : "0"));
            sb.AppendLine("Gamepad=" + (Gamepad ? "1" : "0"));
            sb.AppendLine("WakeMode=" + WakeMode);
            sb.AppendLine("WakeKey=" + KeyCombo.Format(WakeMods, WakeVk));
            sb.AppendLine("BlankKey=" + KeyCombo.Format(BlankMods, BlankVk));
            sb.AppendLine("PowerOffKey=" + KeyCombo.Format(PowerOffMods, PowerOffVk));
            sb.AppendLine("IconsKey=" + KeyCombo.Format(IconsMods, IconsVk));
            sb.AppendLine("CursorHideSec=" + CursorHideSec);
            sb.AppendLine("WarnSec=" + WarnSec);
            sb.AppendLine("DimAfterSec=" + DimAfterSec);
            sb.AppendLine("DimLevel=" + DimLevel);
            sb.AppendLine("DimmedFrom=" + DimmedFrom);
            sb.AppendLine("BrightUpKey=" + KeyCombo.Format(BrightUpMods, BrightUpVk));
            sb.AppendLine("BrightDownKey=" + KeyCombo.Format(BrightDownMods, BrightDownVk));
            sb.AppendLine("ScheduleOn=" + (ScheduleOn ? "1" : "0"));
            sb.AppendLine("ScheduleStart=" + ScheduleStart);
            sb.AppendLine("ScheduleEnd=" + ScheduleEnd);
            sb.AppendLine("ScheduleTimeoutSec=" + ScheduleTimeoutSec);
            sb.AppendLine("Whitelist=" + string.Join("|", Whitelist.ToArray()));
            sb.AppendLine("BlankDevices=" + string.Join("|", BlankDevices.ToArray()));
            sb.AppendLine("LockScreenOffSec=" + LockScreenOffSec);
            sb.AppendLine("CheckUpdates=" + (CheckUpdates ? "1" : "0"));
            sb.AppendLine("AutoInstallUpdates=" + (AutoInstallUpdates ? "1" : "0"));
            sb.AppendLine();
            sb.AppendLine("[Appearance]");
            sb.AppendLine("AutoTheme=" + (AutoTheme ? "1" : "0"));
            sb.AppendLine("UseSystemLocation=" + (UseSystemLocation ? "1" : "0"));
            sb.AppendLine("Location=" + (HasLocation ? FormatCoord(Latitude) + "," + FormatCoord(Longitude) : ""));
            sb.AppendLine("NightKelvin=" + NightKelvin);
            sb.AppendLine("AutoBrightness=" + (AutoBrightness ? "1" : "0"));
            sb.AppendLine("DayBrightness=" + DayBrightness);
            sb.AppendLine("NightBrightness=" + NightBrightness);
            sb.AppendLine("LightWallpaper=" + LightWallpaper);
            sb.AppendLine("DarkWallpaper=" + DarkWallpaper);
            sb.AppendLine();
            sb.AppendLine("[Display]");
            sb.AppendLine("Device=" + DisplayDevice);
            sb.AppendLine("ToggleHotkey=" + KeyCombo.Format(ToggleMods, ToggleVk));
            sb.AppendLine("HdrHotkey=" + KeyCombo.Format(HdrMods, HdrVk));
            foreach (var p in Profiles)
            {
                sb.AppendLine();
                sb.AppendLine("[Mode:" + p.Name + "]");
                sb.AppendLine("Width=" + p.Width);
                sb.AppendLine("Height=" + p.Height);
                sb.AppendLine("RefreshRate=" + p.RefreshRate);
                sb.AppendLine("Scale=" + p.Scale);
                sb.AppendLine("Hotkey=" + KeyCombo.Format(p.HotkeyMods, p.HotkeyVk));
                sb.AppendLine("AutoApps=" + string.Join("|", p.AutoApps.ToArray()));
                sb.AppendLine("Verified=" + (p.Verified ? "1" : "0"));
                sb.AppendLine("Hdr=" + p.Hdr);
                sb.AppendLine("Brightness=" + p.Brightness);
            }
            // 先写临时文件再替换, 写到一半断电/崩溃也不会把原来的配置弄坏
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(true));
            if (!File.Exists(FilePath)) File.Move(tmp, FilePath);
            else
            {
                try { File.Replace(tmp, FilePath, null); }
                catch (IOException) { File.Copy(tmp, FilePath, true); File.Delete(tmp); } // 个别文件系统不支持 Replace
            }
        }
        catch (Exception ex) { Log.Write("保存配置失败: " + ex.Message); }
    }

    // 导入 DisplayModeSwitch.ini, 同名模式会被覆盖, 返回导入的模式数
    public int ImportDisplayModeSwitch(string path)
    {
        var imported = new List<DisplayProfile>();
        DisplayProfile cur = null;
        bool inSettings = false;
        int m, n;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
            if (line[0] == '[' && line.EndsWith("]"))
            {
                string name = line.Substring(1, line.Length - 2).Trim();
                inSettings = string.Equals(name, "Settings", StringComparison.OrdinalIgnoreCase);
                cur = inSettings ? null : new DisplayProfile { Name = name };
                if (cur != null) imported.Add(cur);
                continue;
            }
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
            if (inSettings)
            {
                if (string.Equals(k, "Display", StringComparison.OrdinalIgnoreCase) && v.Length > 0) DisplayDevice = v;
                else if (string.Equals(k, "ToggleHotkey", StringComparison.OrdinalIgnoreCase) && KeyCombo.Parse(v, out m, out n)) { ToggleVk = n; ToggleMods = m; }
            }
            else if (cur != null) ReadMode(cur, k, v);
        }
        imported.RemoveAll(p => p.Width <= 0 || p.Height <= 0 || p.Name.Length == 0);
        if (imported.Count == 0) throw new FormatException("文件里没有找到显示模式。");
        foreach (var p in imported)
        {
            int idx = Profiles.FindIndex(q => string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) Profiles[idx] = p; else Profiles.Add(p);
        }
        return imported.Count;
    }
}
