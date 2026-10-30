using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerWorkbench.Core;

internal static class Program
{
    private static readonly string Executable = Assembly.GetExecutingAssembly().Location;
    private static string runRoot;
    private static int passed;
    private static int failed;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--fixture") return Fixture(args.Skip(1).ToArray());
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task<int> Run(string[] args)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Run on Windows.");
        var parent = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "ServerWorkbench-windows-tests");
        runRoot = Path.Combine(parent, "windows-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(runRoot);
        Console.WriteLine("Windows process integration tests; synthetic server, not a game readiness test.");
        Console.WriteLine("OS=" + Environment.OSVersion + "; CLR=" + Environment.Version + "; x64=" + Environment.Is64BitProcess);
        Console.WriteLine("Windows registry build=" + Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", "unknown") + "; DisplayVersion=" + Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "unknown"));
        Console.WriteLine("Artifacts=" + runRoot);
        await Test("BAT child readiness, process-group CPU/memory, console and graceful stop", BatLifecycle);
        await Test("Real process confirmed saves, two snapshots, restore with pre-backup", SaveRotation);
        await Test("Emergency kill sends no save/stop and kills BAT child", Emergency);
        await Test("Terraria-style exit-nosave path sends only exit-nosave", EmergencyCommand);
        await Test("Exited BAT launcher keeps child running, monitored and killable", DetachedChild);
        await Test("Terraria UTF16LE stdin and UTF8 Chinese stdout/stderr", ()=>UnicodeConsole(ServerKind.Terraria));
        await Test("tModLoader UTF16LE stdin and UTF8 Chinese stdout/stderr", ()=>UnicodeConsole(ServerKind.TModLoader));
        Console.WriteLine("RESULT passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }

    private static async Task Test(string name, Func<Task> test)
    {
        try { await test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }

    private sealed class Rig : IDisposable
    {
        public AppPaths Paths;
        public InstanceRepository Repository;
        public ServerInstance Server;
        public ProcessManager Processes;
        public SaveService Saves;
        public BackupService Backups;
        public string Directory;
        public int ChildPid => int.Parse(File.ReadAllText(Path.Combine(Directory, "fixture.pid")));
        public string Commands
        {
            get
            {
                var path = Path.Combine(Directory, "commands.log");
                if (!File.Exists(path)) return "";
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
        public string World => File.ReadAllText(Path.Combine(Directory, "world", "state.txt"));
        public void Dispose()
        {
            Saves.Dispose();
            try { Processes.Kill(Server); } catch { }
            // Cleanup only the exact fixture PID recorded inside this newly created rig.
            // Never enumerate or kill other user processes. Check the executable before fallback kill.
            try
            {
                if (File.Exists(Path.Combine(Directory, "fixture.pid")))
                    using (var child = Process.GetProcessById(ChildPid))
                        if (!child.HasExited && string.Equals(child.MainModule.FileName, Executable, StringComparison.OrdinalIgnoreCase)) child.Kill();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            Processes.Dispose();
        }
    }

    private static Rig Create(string name, bool detached = false, string emergency = "")
    {
        var paths = new AppPaths(Path.Combine(runRoot, name + " 中文 空格"));
        var repository = new InstanceRepository(paths);
        var server = new ServerInstance
        {
            Name = name, EntryPoint = "launch server.bat", Runtime = new RuntimeRequirement { Kind = RuntimeKind.Script },
            AutoSaveMinutes = 0, StopCommand = "stop", ReadyPattern = "^FIXTURE_READY ",
            Save = new SaveProfile { Command = "save", CompletedPattern = "^FIXTURE_SAVED ", PauseCommand = "pause", ResumeCommand = "resume", EmergencyCommand = emergency, Paths = new List<string> { "world" } }
        };
        repository.Save(server);
        var directory = paths.InstanceDirectory(server);
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "world"));
        File.WriteAllText(Path.Combine(directory, "world", "state.txt"), "initial");
        File.WriteAllText(Path.Combine(directory, "settings.txt"), "keep-settings");
        var command = "\"" + Executable + "\" --fixture";
        var script = "@echo off\r\n" + (detached ? "start \"\" /b " + command + "\r\nexit /b 0\r\n" : command + "\r\nexit /b %errorlevel%\r\n");
        File.WriteAllText(Path.Combine(directory, server.EntryPoint), script, Encoding.Default);
        var processes = new ProcessManager(paths, new NoRuntime());
        processes.LogReceived += (sender, line) => Console.WriteLine(name + ": " + line.Line);
        var transfers = new TransferService();
        var backups = new BackupService(paths, transfers, processes);
        return new Rig { Paths = paths, Repository = repository, Server = server, Processes = processes, Directory = directory, Backups = backups, Saves = new SaveService(paths, repository, processes, transfers, backups, false) };
    }

    private static async Task Start(Rig rig)
    {
        await rig.Processes.StartAsync(rig.Server, null, CancellationToken.None);
        await Until(() => rig.Processes.GetStatus(rig.Server).Ready && File.Exists(Path.Combine(rig.Directory, "fixture.pid")), "ready log and fixture PID", 15);
        Require(rig.World == "initial", "Fixture world should exist before any saves.");
    }

    private static async Task BatLifecycle()
    {
        using (var rig = Create("bat-lifecycle"))
        {
            await Start(rig);
            var sample = rig.Processes.GetResources(rig.Server);
            Require(string.IsNullOrEmpty(sample.Error), "Process group resource error: " + sample.Error);
            Require(sample.ProcessCount >= 2, "BAT and actual child must both be counted; count=" + sample.ProcessCount);
            Require(sample.MemoryBytes >= 32L * 1024 * 1024, "Child's allocated memory is absent; bytes=" + sample.MemoryBytes);
            bool sawCpu = false;
            for (int i = 0; i < 3; i++)
            {
                await Task.Delay(1000);
                sample = rig.Processes.GetResources(rig.Server);
                Console.WriteLine("sample count=" + sample.ProcessCount + " bytes=" + sample.MemoryBytes + " cpu=" + sample.CpuPercent.ToString("F4") + " error=" + sample.Error);
                sawCpu |= sample.CpuPercent > 0;
            }
            Require(sawCpu, "Expected CPU use from actual child, while cmd waits idle.");
            rig.Processes.Send(rig.Server, "echo-token");
            await Until(() => rig.Commands.Contains("echo-token"), "console input delivered to BAT child");
            var pid = rig.ChildPid;
            Require(await rig.Processes.StopAsync(rig.Server, 5, CancellationToken.None), "Graceful stop timed out.");
            await Until(() => !IsAlive(pid), "child exited after normal stop");
            Require(rig.World == "stop-1", "Normal stop did not persist fixture world.");
        }
    }

    private static async Task SaveRotation()
    {
        using (var rig = Create("save-rotation"))
        {
            await Start(rig);
            for (int i = 1; i <= 3; i++)
            {
                await rig.Saves.SaveNowAsync(rig.Server, null, CancellationToken.None);
                Require(rig.World == "save-" + i, "Save confirmation arrived without corresponding world contents.");
            }
            var snapshots = rig.Saves.List(rig.Server);
            Require(snapshots.Count == 2, "Exactly two confirmed snapshots must remain.");
            await Until(() => rig.Commands.Replace("\r", "").Trim().EndsWith("resume"), "final resume command received");
            Require(rig.Commands.Replace("\r", "").Trim() == "pause\nsave\nresume\npause\nsave\nresume\npause\nsave\nresume", "Unexpected save/pause/resume command sequence: " + rig.Commands);
            Require(await rig.Saves.SaveAndStopAsync(rig.Server, null, CancellationToken.None), "Save-and-stop timed out.");
            Require(rig.World == "stop-4", "Save-and-stop did not persist final state.");
            Require(rig.Saves.List(rig.Server).Count == 2, "Normal stop broke two-snapshot retention.");
            File.WriteAllText(Path.Combine(rig.Directory, "world", "newer-chunk.dat"), "must-disappear");
            await rig.Saves.RestoreAsync(rig.Server, rig.Saves.List(rig.Server)[1], null, CancellationToken.None);
            Require(rig.World == "save-3", "Older retained snapshot did not restore expected saved state.");
            Require(!File.Exists(Path.Combine(rig.Directory, "world", "newer-chunk.dat")), "Restore left newer world files behind.");
            Require(File.ReadAllText(Path.Combine(rig.Directory, "settings.txt")) == "keep-settings", "Restore changed non-world settings.");
            Require(rig.Backups.List(rig.Server).Count == 1, "Restore must create one full pre-restore backup.");
        }
    }

    private static async Task Emergency()
    {
        using (var rig = Create("emergency"))
        {
            await Start(rig);
            var pid = rig.ChildPid;
            await rig.Saves.EmergencyStopAsync(rig.Server);
            await Until(() => !IsAlive(pid) && !rig.Processes.GetStatus(rig.Server).Running, "entire BAT group terminated");
            Require(rig.Commands.Length == 0, "Emergency kill sent console commands: " + rig.Commands);
            Require(rig.World == "initial", "Emergency path saved the fixture world.");
            Require(rig.Saves.List(rig.Server).Count == 0, "Emergency path created a save snapshot.");
        }
    }

    private static async Task EmergencyCommand()
    {
        using (var rig = Create("exit-nosave", false, "exit-nosave"))
        {
            await Start(rig);
            var pid = rig.ChildPid;
            await rig.Saves.EmergencyStopAsync(rig.Server);
            await Until(() => !IsAlive(pid), "exit-nosave child exited");
            Require(rig.Commands.Trim() == "exit-nosave", "Emergency command path sent save/stop or another command: " + rig.Commands);
            Require(rig.World == "initial", "exit-nosave saved the fixture world.");
        }
    }

    private static async Task DetachedChild()
    {
        using (var rig = Create("detached", true))
        {
            await rig.Processes.StartAsync(rig.Server, null, CancellationToken.None);
            await Until(() => File.Exists(Path.Combine(rig.Directory, "fixture.pid")), "detached fixture PID", 15);
            var pid = rig.ChildPid;
            await Task.Delay(500);
            Require(IsAlive(pid), "Detached child unexpectedly exited.");
            Require(rig.Processes.GetStatus(rig.Server).Running, "Manager reported stopped while actual child is alive.");
            var sample = rig.Processes.GetResources(rig.Server);
            Require(sample.ProcessCount >= 1 && sample.MemoryBytes >= 32L * 1024 * 1024, "Detached child missing from resource group.");
            rig.Processes.Kill(rig.Server);
            await Until(() => !IsAlive(pid), "detached child killed via job");
        }
    }

    private static async Task UnicodeConsole(ServerKind kind)
    {
        using (var rig = Create("unicode-console"))
        {
            rig.Server.Kind = kind;
            rig.Repository.Save(rig.Server);
            var script = Path.Combine(rig.Directory, rig.Server.EntryPoint);
            File.WriteAllText(script, File.ReadAllText(script, Encoding.Default).Replace("--fixture", "--fixture --unicodeinput"), Encoding.Default);
            await Start(rig);
            await Until(() => rig.Processes.ReadLog(rig.Server).Any(line => line.Contains("FIXTURE_UTF8 中文输出")) && rig.Processes.ReadLog(rig.Server).Any(line => line.Contains("FIXTURE_ERROR 中文错误")), "UTF8 Chinese stdout and stderr decoded correctly");
            rig.Processes.Send(rig.Server, "中文保存测试");
            await Until(() => rig.Commands.Contains("中文保存测试"), "UTF16LE Chinese command delivered without BOM");
            await rig.Saves.SaveNowAsync(rig.Server, null, CancellationToken.None);
            Require(rig.World == "save-1", "UTF16 save command did not persist fixture world.");
            Require(await rig.Saves.SaveAndStopAsync(rig.Server, null, CancellationToken.None), "UTF16 normal stop timed out.");
            Require(rig.World == "stop-3", "UTF16 pre-save plus normal stop did not persist the final world.");
        }
    }
    private static bool IsAlive(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static async Task Until(Func<bool> condition, string message, int seconds = 8)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(seconds)) { if (condition()) return; await Task.Delay(50); }
        throw new TimeoutException(message);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class NoRuntime : IRuntimeProvider
    {
        public Task<RuntimeResolution> EnsureAsync(RuntimeRequirement requirement, string existingPath, IProgress<WorkProgress> progress, CancellationToken ct)
        { throw new InvalidOperationException("Synthetic native fixture should not request a runtime."); }
    }

    private static int Fixture(string[] args)
    {
        if (args.Contains("--unicodeinput")) { Console.InputEncoding = new UnicodeEncoding(false, false); Console.OutputEncoding = new UTF8Encoding(false); Console.WriteLine("FIXTURE_UTF8 中文输出"); Console.Error.WriteLine("FIXTURE_ERROR 中文错误"); }
        var memory = new byte[48 * 1024 * 1024];
        for (int i = 0; i < memory.Length; i += 4096) memory[i] = 1;
        var cancellation = new CancellationTokenSource();
        Task.Run(() =>
        {
            long result = 1;
            while (!cancellation.IsCancellationRequested)
            {
                var busy = Stopwatch.StartNew();
                while (busy.ElapsedMilliseconds < 15) result = unchecked(result * 1664525 + 1013904223);
                Thread.Sleep(15);
            }
            GC.KeepAlive(result);
        });
        File.WriteAllText("fixture.pid", Process.GetCurrentProcess().Id.ToString());
        Console.WriteLine("FIXTURE_READY " + Process.GetCurrentProcess().Id);
        Console.Out.Flush();
        var input = Task.Run(() =>
        {
            int saves = 0;
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                File.AppendAllText("commands.log", line + "\n", new UTF8Encoding(false));
                if (line == "save" || line == "stop")
                {
                    File.WriteAllText(Path.Combine("world", "state.txt"), line + "-" + ++saves);
                    Console.WriteLine("FIXTURE_SAVED " + saves);
                    Console.Out.Flush();
                    if (line == "stop") break;
                }
                else if (line == "exit-nosave") break;
            }
        });
        // Detached launches can lose stdin. Keep their worker alive to test job lifetime,
        // while every fixture has a bounded safety lifetime if a test aborts.
        if (input.Wait(TimeSpan.FromSeconds(60)))
        {
            if (!File.Exists("commands.log")) Thread.Sleep(TimeSpan.FromSeconds(60));
        }
        cancellation.Cancel();
        GC.KeepAlive(memory);
        return 0;
    }
}
