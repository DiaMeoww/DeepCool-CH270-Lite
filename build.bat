@echo off
chcp 65001 >nul
echo ========================================================
echo  九州风神 CH270 数显独立驱动 - 一键编译脚本
echo ========================================================
echo.

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo [错误] 未在系统中检测到 .NET Framework 4.0+ 编译器！
    pause
    exit /b 1
)

echo [1/2] 正在编译控制面板 UI (CH270.exe)...
"%CSC%" /target:winexe /optimize+ /out:CH270.exe CH270.cs
if %errorlevel% neq 0 (
    echo [错误] CH270.exe 编译失败！
    pause
    exit /b %errorlevel%
)
echo [完成] CH270.exe 编译成功！

echo [2/2] 正在编译后台数显推送服务 (CH270.Service.exe)...
"%CSC%" /target:winexe /optimize+ /out:CH270.Service.exe CH270.Service.cs
if %errorlevel% neq 0 (
    echo [错误] CH270.Service.exe 编译失败！
    pause
    exit /b %errorlevel%
)
echo [完成] CH270.Service.exe 编译成功！

echo.
echo ========================================================
echo  全部组件编译完成！可直接双击 CH270.exe 运行！
echo ========================================================
pause
