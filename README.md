# DisplayKit

A Windows tray utility for OLED monitors that does two jobs:

- **Burn-in protection**: Windows' own "turn off the display" is easily woken by mouse jitter. When you're actually away, this tool covers every monitor with a pure black window (black pixels on an OLED are fully off), and the screen only wakes when you press the key you chose.
- **Display mode switching**: switch resolution, refresh rate and scale together with one hotkey (merged from [DisplayModeSwitch](https://github.com/czczccc/DisplayModeSwitch)).

Windows 下的 OLED 显示器托盘工具，两个功能合二为一：

- **防烧屏**：系统自带的息屏很容易被鼠标抖动唤醒；本工具在你真正离开时用纯黑窗口盖住所有显示器（OLED 的黑色像素完全不发光），只有按下你指定的按键才会唤醒。
- **显示模式切换**：一键同时切换分辨率、刷新率和缩放（由 [DisplayModeSwitch](https://github.com/czczccc/DisplayModeSwitch) 合并而来）。

界面为 Windows 11 风格，跟随系统深色/浅色模式。

> 本项目由 [OledGuard](https://github.com/czczccc/OledGuard) 和 [DisplayModeSwitch](https://github.com/czczccc/DisplayModeSwitch) 合并改名而来。从 OledGuard 升级时，设置和开机自启会自动迁移，记得先退出旧版。

## 下载

到 [Releases](https://github.com/czczccc/DisplayKit/releases/latest) 下载 `DisplayKit.exe`，直接运行即可（单文件、无需安装，Windows 10/11 自带所需的 .NET Framework 4.8）。

程序没有数字签名，首次运行时 SmartScreen 可能拦截：点"更多信息" → "仍要运行"。建议把 exe 放到固定位置（如 `%LOCALAPPDATA%\Programs\DisplayKit\`）后再打开"开机自启动"。

## 使用

- 运行后会打开设置窗口，关掉窗口程序会继续在托盘运行
- **左键**托盘图标打开设置，**右键**打开菜单（切换模式、立即黑屏、暂停等）
- 再次运行 exe 也会打开已运行实例的设置窗口

### 防烧屏

- **防抖的空闲检测**：自己监听键盘鼠标，鼠标轻微抖动不算操作（阈值可调）
- **黑屏覆盖所有显示器**，隐藏鼠标指针；黑屏期间的按键、点击都会被拦截，不会误操作到后面的程序
- **唤醒方式**：仅指定按键 / 组合键（默认空格）、键盘任意键、或任意操作
- **立即黑屏快捷键**（默认 `Ctrl+Alt+B`），可以和唤醒键设成同一个，一键切换
- **使用中不黑屏**：全屏应用（电影、游戏）、正在播放声音、手柄操作、白名单程序在前台
- **可选真正关闭显示器**：黑屏 N 分钟后发送关屏指令；被抖动唤醒也只会亮起黑屏，静止后再次关闭（很多 OLED 显示器只在待机时执行像素刷新）
- **多显示器时只黑指定的屏**（比如只黑 OLED 那台），其他屏幕上的点击和前台窗口在其他屏幕时的按键照常可用
- **锁屏后关闭显示器**（锁屏界面也是静态画面），解锁后自动去掉黑屏
- 暂停 30 分钟 / 1–2 小时 / 直到恢复

### 显示模式

- 内置两个模式：**办公** 3840×2160 · 60Hz · 200%（`Ctrl+Alt+F10`）、**游戏** 1920×1080 · 240Hz · 100%（`Ctrl+Alt+F11`），`Ctrl+Alt+F12` 循环切换；首次使用时自动添加，之后也可以在"添加模式"里一键加入
- 在设置里添加模式：分辨率和刷新率从显示器实际支持的列表里选，缩放可选 100%–500%
- 每个模式可以有自己的快捷键，另有"循环切换"快捷键；设置时会提示快捷键冲突
- **运行指定程序时自动切换**：比如启动某个游戏自动切到 1080p 240Hz，退出游戏后切回原来的模式
- **切换保护**：第一次切到某个模式时弹出确认，15 秒内不点"保留"就自动恢复，防止切到显示器不支持的模式后没信号
- 多显示器时可以选择切换哪一台
- 一键导入 DisplayModeSwitch 的 `DisplayModeSwitch.ini`
- 命令行（切完就退出，适合做桌面快捷方式或给游戏启动器用）：

  ```
  DisplayKit.exe 游戏
  DisplayKit.exe --toggle
  DisplayKit.exe --help
  ```

所有快捷键都由程序自己的键盘钩子处理，不会被其他程序抢占。

### 其他

- **跟随日出日落切换深浅色**（"通用 → 外观"，默认关闭）：像 iPhone 的"自动"外观，白天浅色、日落后深色，应用和任务栏一起切。中途手动改过主题会保持到下一次日出或日落。位置用 Windows 定位服务（需在系统设置里允许桌面应用访问位置），也可以手动填经纬度；日出日落在本地计算，不联网
- 界面跟随系统深色/浅色模式，切换缩放或拖到其他屏幕时自动按新缩放重新排版
- **自动更新**：每天检查 GitHub 上的新版本，发现后等你几十秒不操作键鼠时自动下载、替换 exe 并重启，设置和开机自启保持不变。黑屏、锁屏、暂停、游戏自动切换模式期间不会更新。也可以在"通用"里关掉自动安装，改成托盘提醒后手动点"立即更新"
- 下载的文件版本号必须和发布标签一致才会替换；exe 放在没有写权限的目录（如 Program Files）时无法自动更新，需要手动下载

## 自己编译

不需要安装任何开发工具，使用 Windows 自带的 .NET Framework 4 编译器：

```bat
build.bat
```

生成的程序在 `%LOCALAPPDATA%\Programs\DisplayKit\DisplayKit.exe`。

> 如果源码所在文件夹带有低完整性标签（某些沙箱工具会设置），直接放在那里的 exe 会以低权限运行，导致托盘图标不显示、键鼠拦截失效，所以 `build.bat` 会输出到上面的目录。

## 发布新版本

先把 `src/App.cs` 里的 `Version` 改成新版本号并提交（exe 的文件版本和检查更新都用它），再推送 `v` 开头的同名标签，GitHub Actions 会检查两者一致、编译并创建 Release：

```bat
git tag v1.1.0
git push origin v1.1.0
```

## 配置

设置保存在 `%APPDATA%\DisplayKit\config.ini`，日志在同目录的 `log.txt`。

## 原理

- 空闲检测与拦截：`WH_KEYBOARD_LL` / `WH_MOUSE_LL` 低级钩子；声音检测：Core Audio 峰值表；手柄：XInput
- 分辨率/刷新率：`ChangeDisplaySettingsEx`（先 `CDS_TEST` 校验再写入）
- 缩放：`DisplayConfigSetDeviceInfo`，与 Windows"设置 → 显示 → 缩放"相同的系统接口，立即生效、无需注销；切换时先改分辨率再改缩放
- 深浅色：写 `HKCU\...\Themes\Personalize` 的 `AppsUseLightTheme` / `SystemUsesLightTheme` 后广播 `WM_SETTINGCHANGE("ImmersiveColorSet")`；位置：`System.Device.Location`；日出日落：NOAA 算法

## 限制

- 无法覆盖锁屏界面和以管理员身份运行的窗口
- 任务栏等常驻元素建议另外设置为自动隐藏、使用深色主题
- 万一卡在黑屏，`Ctrl+Alt+Del` 总能退出
