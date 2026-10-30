# .NET Framework 4.8 长路径回归

该工具只使用新建测试目录，不读取或修改用户已有的 Data；测试产物保留在输出路径中。它验证管理器的托管文件存储，不表示任一游戏、Forge 安装器、Java 或 BAT 支持同样深度的路径。

```powershell
dotnet build tools/windows-longpath-tests/ServerWorkbench.LongPathTests.csproj -c Release
& tools/windows-longpath-tests/bin/Release/net48/ServerWorkbench.LongPathTests.exe C:\可写的测试目录
```

三组检查：

- 原子 JSON 首次创建和替换，配置路径接近 MAX_PATH，旧式 `instance.json.GUID.tmp` 会超长；不残留临时文件。
- 超过 330 字符的原子 JSON 创建和替换。
- 深 Data 下实例保存读取、包含深层游戏依赖文件的完整备份和恢复、两份世界存档轮换与恢复，以及恢复前完整备份。

可执行程序沿用桌面的 Framework4.8 App.config 与应用 manifest。工具打印实际系统、CLR、AppContext 开关和只读的 LongPathsEnabled，**不会修改 Windows 策略**。Win10/11 的常规 Win32 长路径行为需要应用 longPathAware 声明及系统已有支持；AtomicJson 内部另外使用扩展路径，避免其随机临时后缀触发旧系统的 MAX_PATH 限制。Win7/8.1 仍需各自实机验收，测试通过不能推广为旧系统或游戏运行时已经验证。

开发诊断的官方资料：

- [Microsoft Learn：.NET Framework 4.6.2 长路径行为](https://learn.microsoft.com/en-us/dotnet/framework/migration-guide/retargeting/4.6.x#long-path-support)
- [Microsoft Learn：Windows 路径长度限制、扩展路径与 longPathAware](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation)
- [Microsoft Learn：AppContextSwitchOverrides](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/runtime/appcontextswitchoverrides-element)
