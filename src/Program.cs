// DisplayKit: OLED 防烧屏 + 显示模式切换
// 编译: build.bat (使用 Windows 自带的 .NET Framework 4 csc.exe, 无需安装任何东西)
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    // 不带参数: 常驻托盘
    // 带模式名: 直接切换后退出, 方便做成桌面快捷方式, 例如 DisplayKit.exe 游戏 / DisplayKit.exe --toggle
    [STAThread]
    static int Main(string[] args)
    {
        Native.EnableDpiAwareness();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool autostart = false, updated = false;
        var commands = new List<string>();
        foreach (var a in args)
        {
            if (a == "--autostart") autostart = true;
            else if (a == "--updated") updated = true; // 自动更新后由旧版本启动
            else if (a == "--help" || a == "-h" || a == "/?")
            {
                MessageBox.Show(Usage, "DisplayKit", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            else commands.Add(a);
        }
        if (commands.Count > 0)
        {
            string error = null;
            if (commands.Count > 1) error = "参数太多。模式名带空格时请用引号括起来，例如 DisplayKit.exe \"模式 1\"。";
            else if (commands[0].StartsWith("-") && commands[0] != "--toggle" && commands[0] != "-t") error = "不认识的参数：" + commands[0];
            if (error != null)
            {
                MessageBox.Show(error + "\n\n" + Usage, "DisplayKit", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            return RunCommand(commands[0]);
        }

        bool created;
        using (var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DisplayKit_Show"))
        using (var mutex = AcquireInstance(updated, out created))
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
            Application.Run(new App(autostart, updated, showSignal));
        }
        return 0;
    }

    // 单实例锁. 自动更新时旧版本还在退出中, 最多等它 15 秒
    static Mutex AcquireInstance(bool wait, out bool created)
    {
        for (int i = 0; ; i++)
        {
            var mutex = new Mutex(true, @"Local\DisplayKit_SingleInstance", out created);
            if (created || !wait || i >= 75) return mutex;
            mutex.Dispose();
            Thread.Sleep(200);
        }
    }

    const string Usage =
        "用法：\n" +
        "  DisplayKit.exe            常驻托盘\n" +
        "  DisplayKit.exe 模式名     切换到指定模式后退出\n" +
        "  DisplayKit.exe --toggle   切换到下一个模式后退出\n" +
        "  DisplayKit.exe --help     显示本说明";

    static int RunCommand(string arg)
    {
        try
        {
            var cfg = Settings.Load();
            if (cfg.Profiles.Count == 0) throw new InvalidOperationException("还没有设置显示模式，请先运行 DisplayKit 在「显示模式」里添加。");
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
            MessageBox.Show(ex.Message, "DisplayKit", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
