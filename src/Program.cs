// OledGuard: OLED 防烧屏 + 显示模式切换
// 编译: build.bat (使用 Windows 自带的 .NET Framework 4 csc.exe, 无需安装任何东西)
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    // 不带参数: 常驻托盘
    // 带模式名: 直接切换后退出, 方便做成桌面快捷方式, 例如 OledGuard.exe 游戏 / OledGuard.exe --toggle
    [STAThread]
    static int Main(string[] args)
    {
        Native.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool autostart = false;
        string command = null;
        foreach (var a in args)
        {
            if (a == "--autostart") autostart = true;
            else if (command == null) command = a;
        }
        if (command != null) return RunCommand(command);

        bool created;
        using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OledGuard_Show"))
        using (var mutex = new Mutex(true, @"Local\OledGuard_SingleInstance", out created))
        {
            if (!created)
            {
                // 已在运行: 让已有实例打开设置窗口
                Native.AllowSetForegroundWindow(-1);
                showSignal.Set();
                return 0;
            }
            Application.ThreadException += (s, e) => Log.Write("异常: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("未处理异常: " + e.ExceptionObject);
            Application.Run(new App(autostart, showSignal));
        }
        return 0;
    }

    static int RunCommand(string arg)
    {
        try
        {
            var cfg = Settings.Load();
            if (cfg.Profiles.Count == 0) throw new InvalidOperationException("还没有设置显示模式，请先运行 OledGuard 在「显示模式」里添加。");
            string device = DisplayController.ResolveDevice(cfg.DisplayDevice);

            DisplayProfile target;
            if (arg == "--toggle" || arg == "-t")
            {
                var state = DisplayController.GetState(device);
                int idx = cfg.Profiles.FindIndex(p => DisplayController.Matches(state, p));
                target = cfg.Profiles[(idx + 1) % cfg.Profiles.Count];
            }
            else
            {
                target = cfg.FindProfile(arg.Trim());
                if (target == null)
                {
                    var names = new List<string>();
                    foreach (var p in cfg.Profiles) names.Add(p.Name);
                    throw new ArgumentException("没有名为「" + arg + "」的模式。可用：" + string.Join("、", names.ToArray()));
                }
            }
            DisplayController.Apply(device, target);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "OledGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
