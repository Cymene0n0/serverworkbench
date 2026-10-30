# Windows 真实游戏验收工具

此工具以 net48 x64 直接运行，引用桌面程序同一 Core 的 `PresetService`、`RuntimeProvider`、`ProcessManager`、`SaveService`、`BackupService`。仅可指定空目录，或本工具已标记的独立测试目录；拒绝接管已有用户 Data。需要路径较短且空间足够的可写目录，所有安装、日志、世界、快照与备份均保留。

```powershell
dotnet build tools/realgame-tests/ServerWorkbench.RealGameTests.csproj -c Release --nologo
$testExe = './tools/realgame-tests/bin/Release/net48/ServerWorkbench.RealGameTests.exe'
& $testExe --data 'C:\path\to\short-test-root' --game Vanilla --version 1.21.1 --accept-eula --java 'C:\path\to\java21\bin\java.exe'
```

Minecraft 需要显式 `--accept-eula`。`--java` 可省略，届时使用真实 `RuntimeProvider` 下载、校验并准备私有 Java。已有 Java 按核心要求精确验证主版本：Vanilla 1.21.1 需 Java 21，Fabric/Forge 1.20.1 需 Java 17，不能用 Java 21 冒充。

可拆开长下载与实机启动，第二步复用首次安装的实例：

```powershell
& $testExe --data 'C:\path\to\short-test-root' --game Forge --version 1.20.1 --accept-eula --phase install
& $testExe --data 'C:\path\to\short-test-root' --game Forge --version 1.20.1 --phase verify
```

`--game` 支持 Vanilla/Fabric/Forge/Terraria/TModLoader。Fabric/Forge 未传 `--loader` 时读取官方列表第一项，并在日志记录真实版本；需要固定验收组合时显式指定。Terraria/tModLoader 可用 `--version latest`，会读取并记录当前官方实际版本，复现时使用日志中的固定 ID。

可调参数：`--port 25586`、`--memory 1536`、整个阶段 `--timeout 2400` 秒、首次世界生成 `--ready-timeout 600` 秒。Minecraft 绑定本机回环地址，保持正版认证配置；工具只查询本机 status 协议，不执行玩家登录。

验收顺序先创建停服完整备份，再配置测试世界、启动、等待实际就绪日志、连接本机监听端口，Minecraft 额外要求返回匹配版本的 status JSON。它实际读取进程组数量、内存和 CPU，脚本启动时要求统计包含游戏子进程。

Minecraft 会正常保存关服一次，停服完整备份后重启；关闭昼夜循环，将游戏时间依次设为 1000/2000/3000，每次通过真实保存日志确认后，解析磁盘 `level.dat` 的 gzip/NBT `Data.DayTime`，并核对两份快照内容。随后修改为未保存的 4000，紧急关服，检查没有发送保存/停止命令、磁盘仍为 3000、两份快照没有轮换。回档前再次完整备份，恢复时间 2000 的较早快照，检查较新世界文件清除、其他设置保留；重启后通过游戏控制台查询实际时间，再正常保存关服。

Terraria/tModLoader 验证真实就绪日志、TCP 监听、正常保存关服、三次真实保存确认和两份轮换、`exit-nosave`/强杀无新保存命令、恢复 `.wld`/`.twld` 与所选快照逐字节一致。此分支不验证 Terraria 协议握手或游戏内状态差异。

每次运行写时间戳日志和 JSON 报告。退出码 0 只表示请求的阶段通过；`--phase install` 成功不能记为开服成功。失败退出码 1，报告保留完成的步骤及错误；故障清理只强杀当前测试实例，不额外发送保存命令。任何结果均不证明真实玩家加入、局域网/公网连接、Mod 兼容或旧版 Windows 兼容。
