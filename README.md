# 开服工坊 ServerWorkbench 0.1

中文 Windows 游戏开服管理器。通用导入 + Minecraft 原版/Fabric/Forge + Terraria/tModLoader。

提供版本下载、独立运行时、日志控制台、Modrinth 与本地 Mod、CPU/内存图表、保存并关服、紧急关服、最近两份世界回档点和完整实例备份。

## 使用

便携包：完整解压 `ServerWorkbench-win-x64-v0.1.zip`，打开解压后的 `ServerWorkbench` 文件夹，运行其中的 `ServerWorkbench.exe`。EXE、DLL 和配置文件需要放在同一目录，不要只复制单独的 EXE。

源码包：`ServerWorkbench-source-v0.1.zip` 不包含 EXE。在支持 .NET SDK 10 的 Windows 开发机器上运行 `build.ps1` 后，程序位于 `dist/ServerWorkbench/ServerWorkbench.exe`。

管理器目标：Win7 SP1 / Win8.1 / Win10 / Win11 的64位系统，需要 .NET Framework 4.8 或兼容的4.8.1。游戏、运行时与Mod另有系统要求。旧系统不自动下载未经验证的Java/.NET，请选择已有环境或导入兼容ZIP。

完整操作见 [使用说明](docs/使用说明.md)，Windows实机检查见 [验收清单](docs/Windows验收.md)，验证状态见 [测试报告](docs/测试报告.md)。

## 构建

- macOS/Linux：`bash build.sh`，使用项目内SDK或已有.NET SDK 10；首次缺少SDK时从微软官方下载固定版本。
- Windows开发机器：安装.NET SDK 10，运行 `powershell -ExecutionPolicy Bypass -File build.ps1`。构建机器需要支持SDK10；目标电脑只需Framework4.8。
- 在线安装包校验：`bash build.sh --online`，会下载真实官方服务端包并在测试结束后清理。

`src/ServerWorkbench.Core` 是netstandard2.0核心；`src/ServerWorkbench.Desktop` 是net48 WinForms界面；`tests/ServerWorkbench.Tests` 是在当前系统可运行的核心与安装测试。

## 数据与扩展

默认使用EXE旁的Data目录；创建实例时也可选择独立服务器存放父目录。中央Data保留索引、备份和共享运行时。每个实例独立保存；共享运行时不修改系统PATH。`Instances`保存实例，`Backups`保存完整备份，`SaveSnapshots`保存最近两份世界回档点。设置可以选择其他数据目录，下次启动生效。

新游戏可直接用通用导入，并在表单保存入口、参数、环境、端口、停止及保存命令。需要在线版本安装时，实现IGameAdapter；运行时与在线Mod分别实现IRuntimeProvider和IModProvider。

已集成可选 frp TCP 内网穿透：官方组件固定版本与摘要校验、强制 CA/证书名称校验、当前 Windows 用户 DPAPI 保存 Token、独立启停和脱敏日志。需要自己的可信公网中转节点，具体步骤见 [内网穿透](docs/内网穿透.md)。新版 frp 组件仅支持 Win10/11 x64，管理器原有 Win7 SP1/8.1 目标保持不变。

当前不包含Docker、WSL、SteamCMD、Workshop在线下载、自动更新或后台系统服务。

## 验证边界

已在 Windows 11 25H2 x64 使用 SDK10 实际构建并验证多款游戏、进程组、保存和回档。已实际观察150% DPI修正布局，点击验证父目录输入、Terraria/tModLoader协议隐藏、启动/正常关服/备份/重启；tModLoader 已实际验证安装、保存、关服与回档。完整GUI安装、旧系统、实际玩家与LAN/公网仍有未验证项，详见测试报告。


## 配套 Linux 公网中转

游戏继续在自己的电脑开服，有公网 IPv4 的 Linux 主机可作为中转。[ServerWorkbench Relay](https://github.com/Cymene0n0/serverworkbench-relay) 提供一条指令部署；本软件负责 Windows 客户端配置。

## 自动构建

[GitHub Actions](https://github.com/Cymene0n0/serverworkbench/actions) 在 Windows 上使用 SDK 10 构建 net48 程序并执行回归测试。首个通过的 main 构建发布 v0.1.0 便携 ZIP、源码 ZIP 和 SHA256 校验值；后续构建保留为 Actions 工件，不覆盖已发布版本。自动测试不代替真实游戏、桌面视觉或旧系统验收。
