# OledGuard

A small Windows tool that prevents burn-in on OLED monitors. Windows' own "turn off the display" is easily woken by mouse jitter. When this tool decides you're idle, it covers every monitor with a pure black window (on an OLED, black pixels are fully off). The screen only wakes when you press the key you chose.

一个 Windows 下的 OLED 防烧屏小工具。系统自带的息屏很容易被鼠标抖动唤醒；本工具在你真正离开时用纯黑窗口盖住所有显示器（OLED 的黑色像素是完全不发光的），并且只有按下你指定的按键才会唤醒。

## 功能

- **防抖的空闲检测**：自己监听键盘鼠标，鼠标轻微抖动不算操作（阈值可调 20–300 像素）
- **黑屏覆盖所有显示器**，隐藏鼠标指针；黑屏期间的按键、点击都会被拦截，不会误操作到后面的程序
- **唤醒方式可选**：仅指定按键 / 组合键（默认空格）、键盘任意键、或任意操作
- **自定义快捷键**：唤醒键和"立即黑屏"快捷键（默认 `Ctrl+Alt+B`）都支持组合键，可设成同一个键实现一键切换
- **使用中不黑屏**：
  - 前台是全屏应用（电影、游戏）
  - 正在播放声音（窗口化视频、直播等）
  - 手柄操作（XInput，带摇杆死区防漂移）
  - 白名单程序在前台时
- **可选真正关闭显示器**：黑屏 N 分钟后发送关屏指令；被抖动唤醒也只会亮起黑屏，静止后再次关闭（很多 OLED 显示器只在待机时执行像素刷新）
- 暂停（30 分钟 / 1–2 小时 / 直到恢复）、开机自启动
- 托盘图标 + 控制窗口；重复运行 exe 会打开已运行实例的控制窗口

## 编译与安装

不需要安装任何开发工具，使用 Windows 自带的 .NET Framework 4 编译器：

```bat
build.bat
```

生成的程序在 `%LOCALAPPDATA%\Programs\OledGuard\OledGuard.exe`，单文件、无依赖。运行后在托盘菜单里勾选"开机自启动"即可。

> 如果源码所在文件夹带有低完整性标签（某些沙箱工具会设置），直接放在那里的 exe 会以低权限运行，导致托盘图标不显示、键鼠拦截失效，所以 `build.bat` 会输出到上面的目录。

## 配置

设置保存在 `%APPDATA%\OledGuard\config.ini`，日志在 `%APPDATA%\OledGuard\log.txt`。

## 限制

- 无法覆盖锁屏界面和以管理员身份运行的窗口
- 任务栏等常驻元素建议另外设置为自动隐藏、使用深色主题
- 万一卡在黑屏，`Ctrl+Alt+Del` 总能退出
