@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem 输出到 %LOCALAPPDATA%\Programs\OledGuard: 本文件夹带有低完整性标签, 放在这里的 exe 会以低权限运行, 托盘图标和键鼠拦截都会失效
set "OUT=%LOCALAPPDATA%\Programs\OledGuard"
if not exist "%OUT%" mkdir "%OUT%"
taskkill /im OledGuard.exe /f >nul 2>&1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize /target:winexe /out:"%OUT%\OledGuard.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll OledGuard.cs
if errorlevel 1 (echo 编译失败 & pause & exit /b 1)
echo 编译成功: %OUT%\OledGuard.exe
