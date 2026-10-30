# Windows 真进程测试

这个独立工具在 Windows x64 上以 **.NET Framework 4.8** 执行真实 BAT → EXE 子进程，调用与桌面程序相同的 Core。测试进程是受控模拟服务端，不代表 Minecraft、Terraria 等真实游戏已经开服成功。

在仓库根目录使用 .NET SDK 10 构建，然后直接执行生成的 net48 EXE（不要使用 `dotnet run` 代替 Framework 实际运行）：

```powershell
dotnet build tools/windows-tests/ServerWorkbench.WindowsTests.csproj -c Release --nologo
& ./tools/windows-tests/bin/Release/net48/ServerWorkbench.WindowsTests.exe
```

可给 EXE 传入一个路径较短的可写目录，测试始终在该目录下新建带时间与随机后缀的专用文件夹，不读取或修改桌面程序的 Data。默认保存在系统临时目录的 `ServerWorkbench-windows-tests` 中，避免测试目录超出旧版 Windows 的路径长度限制。测试结束保留日志、世界、回档点和恢复前备份供检查；请自行归档这些产物。退出码 0 表示所有用例通过，1 表示失败。

覆盖：中文与空格路径、BAT 子进程的就绪日志和控制台、进程组数量/内存/CPU、正常停止、真实 stdout 保存确认、暂停/恢复保存命令、最近两份回档轮换、恢复前完整备份和较新世界文件清除、紧急不保存、`exit-nosave`、脚本已退出时仍存活的子进程监视和终止。

每个 worker 的最长寿命约 60–120 秒。每个用例结束时只清理自身记录的 PID，并核对进程可执行文件路径，不枚举或结束用户的其他游戏进程。
