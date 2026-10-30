@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 开服工坊启动检查
echo.
echo 需要：64位Windows，.NET Framework 4.8或兼容的4.8.1
echo .NET Framework Release注册信息：
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release
echo.
echo Release值应不低于528040。若不存在或更低，请通过微软官方安装.NET Framework 4.8。
echo 官方说明：https://learn.microsoft.com/dotnet/framework/install/
echo Windows 7需要SP1及运行时安装所需的系统补丁；游戏可能要求更高系统版本。
echo.
echo 请完整解压便携包，不能只复制EXE。
echo 启动失败详情请查看程序旁的error.log。
echo.
pause
