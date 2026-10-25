@echo off
chcp 65001 >nul
cd /d "%~dp0"
rem 用法: build.bat [输出文件]. 本地和 GitHub Actions 共用这一份编译命令 (Actions 里传 DisplayKit.exe)
rem 不带参数时输出到 %LOCALAPPDATA%\Programs\DisplayKit: 源码文件夹如果带有低完整性标签, 放在里面的 exe 会以低权限运行, 托盘图标和键鼠拦截都会失效
set "EXE=%~1"
if "%EXE%"=="" set "EXE=%LOCALAPPDATA%\Programs\DisplayKit\DisplayKit.exe"
for %%F in ("%EXE%") do if not exist "%%~dpF" mkdir "%%~dpF"
if "%~1"=="" taskkill /im DisplayKit.exe /f >nul 2>&1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /optimize /warn:4 /warnaserror /target:winexe /win32manifest:src\app.manifest /win32icon:src\app.ico /out:"%EXE%" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Device.dll src\*.cs
if errorlevel 1 (echo 编译失败 & (if "%~1"=="" pause) & exit /b 1)
echo 编译成功: %EXE%
