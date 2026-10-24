using System.Reflection;

// 版本号只在 App.Version 一处维护; 发布时 GitHub Actions 会检查它和标签是否一致
[assembly: AssemblyTitle("DisplayKit")]
[assembly: AssemblyDescription("OLED 防烧屏 + 显示模式切换")]
[assembly: AssemblyProduct("DisplayKit")]
[assembly: AssemblyCopyright("czczccc")]
[assembly: AssemblyVersion(App.Version + ".0")]
[assembly: AssemblyFileVersion(App.Version + ".0")]
[assembly: AssemblyInformationalVersion(App.Version)]
