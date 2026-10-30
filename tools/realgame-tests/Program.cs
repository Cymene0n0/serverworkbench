using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerWorkbench.Core;

internal static class Program
{
    private static readonly object logGate = new object();
    private static string logPath;
    private static readonly List<string> checks = new List<string>();
    private static int Main(string[] args)
    {
        // Match the desktop entry point, including older Windows' TLS default.
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        try { return Run(args).GetAwaiter().GetResult(); }
        catch (Exception error) { Log("FAIL " + error); return 1; }
    }

    private static async Task<int> Run(string[] args)
    {
        var options = Options.Parse(args);
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Requires Windows x64, .NET Framework 4.8.");
        var root = Path.GetFullPath(options.Data);
        var marker = Path.Combine(root, ".serverworkbench-realgame-tests");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !File.Exists(marker))
            throw new InvalidOperationException("Refusing existing non-test directory. Choose an empty test-only --data directory.");
        Directory.CreateDirectory(root);
        if (!File.Exists(marker)) File.WriteAllText(marker, "Isolated disposable game test data. Never point this tool at user Data.");
        logPath = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + options.Game + ".log");
        var reportPath = Path.ChangeExtension(logPath, ".json");
        Log("OS=" + Environment.OSVersion + "; CLR=" + Environment.Version + "; x64=" + Environment.Is64BitProcess);
        Log("Windows registry build=" + Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild", "unknown") + "; DisplayVersion=" + Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "unknown"));
        Log("Game=" + options.Game + "; requested version=" + options.Version + "; phase=" + options.Phase + "; data=" + root);
        Log("Scope: local test world only. No authenticated player, LAN, public Internet, mods, or old-Windows certification.");
        var paths = new AppPaths(root);
        var repository = new InstanceRepository(paths);
        var transfers = new TransferService();
        var runtimes = new RuntimeProvider(paths, transfers);
        var presets = new PresetService(paths, repository, transfers, runtimes);
        var processes = new AuditedProcesses(new ProcessManager(paths, runtimes));
        var backups = new BackupService(paths, transfers, processes);
        ServerInstance server = null;
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.Timeout)))
        using (var saves = new SaveService(paths, repository, processes, transfers, backups, false))
        {
            var ct = deadline.Token;
            processes.LogReceived += (sender, line) => Log("GAME " + line.Line);
            try
            {
                var version = options.Version;
                if (version == "latest") version = (await presets.GetVersionsAsync(options.Game, ct)).First().Id;
                var name = "TEST " + options.Game + " " + version;
                server = repository.List().SingleOrDefault(s => s.Name == name && s.Kind == options.Game && s.GameVersion == version);
                if (server == null)
                {
                    if (options.Phase == "verify") throw new InvalidOperationException("No previously installed test instance; run --phase install first.");
                    var loader = options.Loader;
                    if ((options.Game == ServerKind.Fabric || options.Game == ServerKind.Forge) && string.IsNullOrEmpty(loader))
                        loader = (await presets.GetLoaderVersionsAsync(options.Game, version, ct)).First().Id;
                    server = await presets.InstallAsync(new CreateServerRequest
                    {
                        Name = name, StorageDirectory = options.StorageDirectory, Kind = options.Game, GameVersion = version, LoaderVersion = loader,
                        AcceptEula = options.AcceptEula, RuntimePath = options.Java, Port = options.Port,
                        MemoryMb = options.Memory, MaxPlayers = 2, WorldName = "WorkbenchTest"
                    }, new ProgressLog(), ct);
                    Passed("Official preset install completed: " + server.Kind + " " + server.GameVersion + " loader=" + server.LoaderVersion + "; entry=" + server.EntryPoint + "; runtime=" + server.RuntimePath);
                }
                else Log("Reusing isolated test instance " + server.Id + " entry=" + server.EntryPoint + " loader=" + server.LoaderVersion + "; runtime=" + server.RuntimePath);
                if (!string.IsNullOrWhiteSpace(options.Java) && IsMinecraft(server)) server.RuntimePath = options.Java;
                server.Port = options.Port;
                server.AutoSaveMinutes = 0;
                repository.Save(server);
                if (options.Phase != "install")
                {
                    await Backup(backups, server, "before test-world configuration and normal-stop validation", ct);
                    ConfigureTestWorld(paths, server);
                    await StartAndCheck(processes, server, options.ReadyTimeout, root, ct);
                    if (!await saves.SaveAndStopAsync(server, new ProgressLog(), ct)) throw new TimeoutException("Normal save-and-stop exceeded manager's 30 second limit.");
                    Passed("Real normal save-and-stop received save completion and exited; snapshot count=" + saves.List(server).Count);
                    await Backup(backups, server, "before confirmed-save rotation and emergency-stop validation", ct);
                    await StartAndCheck(processes, server, options.ReadyTimeout, root, ct);
                    if (IsMinecraft(server)) await MinecraftWorldSequence(paths, server, processes, saves, backups, options, root, ct);
                    else await TerrariaSequence(paths, server, processes, saves, backups, ct);
                }
                File.WriteAllText(reportPath, JsonConvert.SerializeObject(new
                {
                    passed = true, options.Phase, os = Environment.OSVersion.ToString(), clr = Environment.Version.ToString(),
                    game = server.Kind.ToString(), version = server.GameVersion, loader = server.LoaderVersion,
                    entry = server.EntryPoint, storageDirectory = paths.InstanceDirectory(server), runtime = server.RuntimePath, instance = server.Id, checks,
                    limitations = new[] { "No authenticated player joined", "No LAN/public connectivity test", "No gameplay/mod compatibility certification", "Only this OS was tested", "Install-only phase does not establish successful game startup" }
                }, Formatting.Indented));
                Log("PASS requested phase; report=" + reportPath);
                return 0;
            }
            catch (Exception error)
            {
                Log("FAIL " + error);
                File.WriteAllText(reportPath, JsonConvert.SerializeObject(new { passed = false, options.Phase, game = options.Game.ToString(), requestedVersion = options.Version, checks, error = error.ToString() }, Formatting.Indented));
                return 1;
            }
            finally
            {
                if (server != null && processes.GetStatus(server).Running)
                {
                    Log("CLEANUP: killing only this isolated test instance; no further save command is sent.");
                    try { processes.Kill(server); } catch (Exception error) { Log("Cleanup error: " + error.Message); }
                }
                processes.Dispose();
            }
        }
    }

    private static bool IsMinecraft(ServerInstance server) => server.Kind == ServerKind.Vanilla || server.Kind == ServerKind.Fabric || server.Kind == ServerKind.Forge;

    private static void ConfigureTestWorld(AppPaths paths, ServerInstance server)
    {
        if (!IsMinecraft(server)) return;
        var file = Path.Combine(paths.InstanceDirectory(server), "server.properties");
        var values = new Dictionary<string, string>
        {
            { "server-ip", "127.0.0.1" }, { "server-port", server.Port.ToString() }, { "level-name", "world" },
            { "view-distance", "3" }, { "simulation-distance", "3" }, { "max-players", "2" },
            { "online-mode", "true" }, { "enable-status", "true" }, { "level-type", "minecraft:flat" },
            { "generator-settings", "{\"biome\":\"minecraft:plains\",\"layers\":[{\"block\":\"minecraft:bedrock\",\"height\":1},{\"block\":\"minecraft:dirt\",\"height\":2},{\"block\":\"minecraft:grass_block\",\"height\":1}],\"lakes\":false,\"features\":false}" },
            { "spawn-protection", "0" }, { "motd", "ServerWorkbench isolated real-game test" }
        };
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        lines.RemoveAll(line => values.Keys.Any(key => line.StartsWith(key + "=", StringComparison.Ordinal)));
        lines.AddRange(values.Select(pair => pair.Key + "=" + pair.Value));
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    private static async Task Backup(BackupService backups, ServerInstance server, string reason, CancellationToken ct)
    {
        Log("BACKUP " + reason);
        var backup = await backups.CreateAsync(server, new ProgressLog(), ct);
        Passed("Full stopped-instance backup: " + backup.Path + " bytes=" + backup.Size);
    }

    private static async Task StartAndCheck(AuditedProcesses processes, ServerInstance server, int timeout, string root, CancellationToken ct)
    {
        await processes.StartAsync(server, new ProgressLog(), ct);
        var started = Stopwatch.StartNew();
        while (!processes.GetStatus(server).Ready)
        {
            ct.ThrowIfCancellationRequested();
            if (!processes.GetStatus(server).Running) throw new InvalidOperationException("Game exited before its ready log; inspect GAME log lines.");
            if (started.Elapsed.TotalSeconds > timeout) throw new TimeoutException("No game-ready log within " + timeout + " seconds.");
            await Task.Delay(300, ct);
        }
        Passed("Recognized actual game ready log: " + server.ReadyPattern);
        using (var client = new TcpClient())
        {
            var connection = client.ConnectAsync(IPAddress.Loopback, server.Port);
            if (await Task.WhenAny(connection, Task.Delay(5000, ct)) != connection) throw new TimeoutException("Local TCP listener did not accept a connection.");
            await connection;
        }
        Passed("Local TCP connection accepted on 127.0.0.1:" + server.Port + "; this does not establish public connectivity.");
        if (IsMinecraft(server))
        {
            var status = MinecraftStatus(server.Port);
            if (!((string)status["version"]?["name"] ?? "").Contains(server.GameVersion)) throw new InvalidDataException("Status response version differs from requested game: " + status);
            File.WriteAllText(Path.Combine(root, server.Kind + "-" + server.GameVersion + "-status.json"), status.ToString(Formatting.Indented));
            Passed("Minecraft status protocol JSON returned version=" + status["version"]?["name"] + "; no player login was attempted.");
        }
        var first = processes.GetResources(server);
        await Task.Delay(1500, ct);
        var resources = processes.GetResources(server);
        if (!string.IsNullOrEmpty(resources.Error) || resources.ProcessCount < 1 || resources.MemoryBytes <= 0) throw new InvalidOperationException("Resource group unavailable: " + resources.Error);
        if (server.Runtime.Kind == RuntimeKind.Script && resources.ProcessCount < 2) throw new InvalidOperationException("Script server did not include game child in resource count.");
        Passed("Actual group resources processes=" + resources.ProcessCount + " memory=" + resources.MemoryBytes + " cpu=" + resources.CpuPercent.ToString("F4"));
    }

    private static async Task MinecraftWorldSequence(AppPaths paths, ServerInstance server, AuditedProcesses processes, SaveService saves, BackupService backups, Options options, string root, CancellationToken ct)
    {
        var directory = paths.InstanceDirectory(server);
        processes.Send(server, "gamerule doDaylightCycle false");
        for (int i = 1; i <= 3; i++)
        {
            processes.Send(server, "time set " + i * 1000);
            var snapshot = await saves.SaveNowAsync(server, new ProgressLog(), ct, "real-game time=" + i * 1000);
            var actual = DayTime(File.ReadAllBytes(Path.Combine(directory, "world", "level.dat")));
            Require(actual == i * 1000, "Persisted level.dat DayTime=" + actual + " expected=" + i * 1000);
            Passed("Confirmed real save " + i + ": persisted level.dat Data.DayTime=" + actual + "; snapshot=" + snapshot.Path);
        }
        var before = saves.List(server);
        Require(before.Count == 2, "Three saves must leave exactly two valid snapshots.");
        Require(SnapshotDayTime(before[0].Path) == 3000 && SnapshotDayTime(before[1].Path) == 2000, "Retained snapshots do not contain expected real world states.");
        processes.Send(server, "time set 4000");
        await WaitForLog(processes, server, "Set the time to 4000", ct);
        var auditStart = processes.AuditCount;
        await saves.EmergencyStopAsync(server);
        await WaitStopped(processes, server, ct);
        var emergency = processes.AuditAfter(auditStart);
        Require(emergency.All(line => line == "KILL"), "Minecraft emergency path sent a save/stop command: " + string.Join(",", emergency));
        Require(saves.List(server).Select(s => s.Path).SequenceEqual(before.Select(s => s.Path)), "Emergency path changed the two snapshots.");
        Require(DayTime(File.ReadAllBytes(Path.Combine(directory, "world", "level.dat"))) == 3000, "Unsaved time=4000 was unexpectedly persisted; inspect game autosave history.");
        Passed("Emergency kill sent no save/stop; disk state remains time=3000 and both retained snapshots are unchanged.");
        await Backup(backups, server, "before world rollback", ct);
        File.WriteAllText(Path.Combine(directory, "harness-retain.txt"), "keep-non-world-settings");
        File.WriteAllText(Path.Combine(directory, "world", "harness-newer-chunk.dat"), "newer-world-sentinel");
        await saves.RestoreAsync(server, before[1], new ProgressLog(), ct);
        Require(DayTime(File.ReadAllBytes(Path.Combine(directory, "world", "level.dat"))) == 2000, "Rollback did not restore the older actual world state.");
        Require(!File.Exists(Path.Combine(directory, "world", "harness-newer-chunk.dat")), "Rollback left newer world files behind.");
        Require(File.ReadAllText(Path.Combine(directory, "harness-retain.txt")) == "keep-non-world-settings", "Rollback changed non-world settings.");
        Passed("Restored older retained world to time=2000, removed newer world file, retained non-world settings; automatic pre-restore backup also completed.");
        await Backup(backups, server, "before starting restored world and final normal stop", ct);
        await StartAndCheck(processes, server, options.ReadyTimeout, root, ct);
        var logCount = processes.ReadLog(server).Count;
        processes.Send(server, "time query daytime");
        await WaitForLog(processes, server, "The time is 2000", ct, logCount);
        Passed("Restarted restored real world; game console confirms daytime=2000.");
        Require(await saves.SaveAndStopAsync(server, new ProgressLog(), ct), "Final normal save-and-stop timed out.");
        Passed("Restored world normal save-and-stop completed.");
    }

    private static async Task TerrariaSequence(AppPaths paths, ServerInstance server, AuditedProcesses processes, SaveService saves, BackupService backups, CancellationToken ct)
    {
        for (int i = 1; i <= 3; i++) await saves.SaveNowAsync(server, new ProgressLog(), ct, "real-game confirmed save " + i);
        var snapshots = saves.List(server);
        Require(snapshots.Count == 2, "Three real saves did not leave exactly two snapshots.");
        using (var zip = ZipFile.OpenRead(snapshots[1].Path))
        {
            Require(zip.Entries.Any(e => e.FullName.EndsWith(".wld", StringComparison.OrdinalIgnoreCase) && e.Length > 0), "Snapshot lacks actual .wld world.");
            if (server.Kind == ServerKind.TModLoader) Require(zip.Entries.Any(e => e.FullName.EndsWith(".twld", StringComparison.OrdinalIgnoreCase) && e.Length > 0), "tModLoader snapshot lacks .twld companion.");
        }
        Passed("Three game-confirmed saves left two snapshots containing real world files; in-game state differences were not inspected.");
        var start = processes.AuditCount;
        await saves.EmergencyStopAsync(server);
        await WaitStopped(processes, server, ct);
        Require(processes.AuditAfter(start).All(line => line == "SEND:exit-nosave" || line == "KILL"), "Emergency path sent a save/normal-exit command.");
        Require(saves.List(server).Select(s => s.Path).SequenceEqual(snapshots.Select(s => s.Path)), "Emergency path rotated snapshots.");
        Passed("Emergency path used only exit-nosave/kill and left both snapshots unchanged.");
        await Backup(backups, server, "before real world-file rollback", ct);
        await saves.RestoreAsync(server, snapshots[1], new ProgressLog(), ct);
        using (var zip = ZipFile.OpenRead(snapshots[1].Path))
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".wld", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".twld", StringComparison.OrdinalIgnoreCase)))
                using (var content = entry.Open()) using (var bytes = new MemoryStream())
                {
                    content.CopyTo(bytes);
                    Require(bytes.ToArray().SequenceEqual(File.ReadAllBytes(PathSafety.ResolveUnder(paths.InstanceDirectory(server), entry.FullName))), "Restored world differs from selected snapshot: " + entry.FullName);
                }
        Passed("Actual .wld/.twld restored byte-for-byte with pre-restore full backup. Gameplay and Terraria protocol handshake remain unverified.");
    }

    private static async Task WaitForLog(AuditedProcesses processes, ServerInstance server, string text, CancellationToken ct, int skip = 0)
    {
        var started = Stopwatch.StartNew();
        while (!processes.ReadLog(server).Skip(skip).Any(line => line.Contains(text)))
        {
            ct.ThrowIfCancellationRequested();
            if (started.Elapsed.TotalSeconds > 20) throw new TimeoutException("No console confirmation: " + text);
            await Task.Delay(100, ct);
        }
    }
    private static async Task WaitStopped(AuditedProcesses processes, ServerInstance server, CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        while (processes.GetStatus(server).Running)
        {
            if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Process group did not terminate.");
            await Task.Delay(100, ct);
        }
    }

    private static JObject MinecraftStatus(int port)
    {
        using (var client = new TcpClient())
        {
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            if (!connect.Wait(5000)) throw new TimeoutException("Minecraft status connect timeout.");
            connect.GetAwaiter().GetResult();
            using (var stream = client.GetStream())
            {
                stream.ReadTimeout = 5000; stream.WriteTimeout = 5000;
                using (var packet = new MemoryStream())
                {
                    WriteVarInt(packet, 0); WriteVarInt(packet, -1);
                    var host = Encoding.UTF8.GetBytes("localhost"); WriteVarInt(packet, host.Length); packet.Write(host, 0, host.Length);
                    packet.WriteByte((byte)(port >> 8)); packet.WriteByte((byte)port); WriteVarInt(packet, 1);
                    WritePacket(stream, packet.ToArray());
                }
                WritePacket(stream, new byte[] { 0 });
                int length = ReadVarInt(stream); Require(length > 0 && length <= 1024 * 1024, "Invalid Minecraft status packet length.");
                var response = ReadExact(stream, length);
                using (var packet = new MemoryStream(response))
                {
                    Require(ReadVarInt(packet) == 0, "Unexpected Minecraft status packet id.");
                    int jsonLength = ReadVarInt(packet); Require(jsonLength >= 0 && jsonLength <= length, "Invalid Minecraft status JSON length.");
                    return JObject.Parse(Encoding.UTF8.GetString(ReadExact(packet, jsonLength)));
                }
            }
        }
    }
    private static void WritePacket(Stream stream, byte[] bytes) { WriteVarInt(stream, bytes.Length); stream.Write(bytes, 0, bytes.Length); stream.Flush(); }
    private static void WriteVarInt(Stream stream, int value)
    {
        uint remaining = unchecked((uint)value);
        do { byte part = (byte)(remaining & 127); remaining >>= 7; if (remaining != 0) part |= 128; stream.WriteByte(part); } while (remaining != 0);
    }
    private static int ReadVarInt(Stream stream)
    {
        int result = 0;
        for (int i = 0; i < 5; i++) { int part = stream.ReadByte(); if (part < 0) throw new EndOfStreamException(); result |= (part & 127) << (i * 7); if ((part & 128) == 0) return result; }
        throw new InvalidDataException("VarInt exceeds five bytes.");
    }
    private static byte[] ReadExact(Stream stream, int count)
    {
        var bytes = new byte[count]; int offset = 0;
        while (offset < count) { int read = stream.Read(bytes, offset, count - offset); if (read == 0) throw new EndOfStreamException(); offset += read; }
        return bytes;
    }
    private static long SnapshotDayTime(string path)
    {
        using (var zip = ZipFile.OpenRead(path)) using (var stream = zip.GetEntry("world/level.dat").Open()) using (var bytes = new MemoryStream()) { stream.CopyTo(bytes); return DayTime(bytes.ToArray()); }
    }
    private static long DayTime(byte[] compressed)
    {
        using (var input = new MemoryStream(compressed)) using (var gzip = new GZipStream(input, CompressionMode.Decompress)) using (var raw = new MemoryStream())
        {
            var buffer = new byte[8192]; int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0) { raw.Write(buffer, 0, read); if (raw.Length > 32 * 1024 * 1024) throw new InvalidDataException("level.dat decompressed size limit exceeded."); }
            raw.Position = 0;
            Require(raw.ReadByte() == 10, "level.dat NBT root is not a compound.");
            NbtString(raw); long? value = null; NbtPayload(raw, 10, "", 0, ref value);
            if (!value.HasValue) throw new InvalidDataException("No Data.DayTime in level.dat."); return value.Value;
        }
    }
    private static int NbtInt(Stream stream) { var bytes = ReadExact(stream, 4); if (BitConverter.IsLittleEndian) Array.Reverse(bytes); return BitConverter.ToInt32(bytes, 0); }
    private static string NbtString(Stream stream) { var length = ReadExact(stream, 2); return Encoding.UTF8.GetString(ReadExact(stream, length[0] * 256 + length[1])); }
    private static void NbtPayload(Stream stream, int tag, string path, int depth, ref long? daytime)
    {
        Require(depth < 64, "NBT nesting limit exceeded.");
        switch (tag)
        {
            case 1: ReadExact(stream, 1); break;
            case 2: ReadExact(stream, 2); break;
            case 3: case 5: ReadExact(stream, 4); break;
            case 4:
                var bytes = ReadExact(stream, 8); if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                if (path == "Data/DayTime") daytime = BitConverter.ToInt64(bytes, 0); break;
            case 6: ReadExact(stream, 8); break;
            case 7: NbtArray(stream, 1); break;
            case 8: NbtString(stream); break;
            case 9:
                int child = stream.ReadByte(), count = NbtInt(stream); Require(count >= 0 && count <= 1000000, "NBT list limit exceeded.");
                for (int i = 0; i < count; i++) NbtPayload(stream, child, path, depth + 1, ref daytime); break;
            case 10:
                for (;;) { int type = stream.ReadByte(); if (type == 0) break; if (type < 0) throw new EndOfStreamException(); var name = NbtString(stream); NbtPayload(stream, type, path.Length == 0 ? name : path + "/" + name, depth + 1, ref daytime); } break;
            case 11: NbtArray(stream, 4); break;
            case 12: NbtArray(stream, 8); break;
            default: throw new InvalidDataException("Unsupported NBT tag " + tag);
        }
    }
    private static void NbtArray(Stream stream, int width) { int count = NbtInt(stream); Require(count >= 0 && count <= 32 * 1024 * 1024 / width, "NBT array limit exceeded."); ReadExact(stream, count * width); }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Passed(string message) { checks.Add(message); Log("PASS " + message); }
    private static void Log(string message)
    {
        lock (logGate)
        {
            string line = DateTime.UtcNow.ToString("o") + " " + message;
            Console.WriteLine(line);
            if (logPath != null) File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
    }
    private sealed class ProgressLog : IProgress<WorkProgress>
    {
        private string previous; private int bucket = -1;
        public void Report(WorkProgress progress)
        {
            string message = progress.Message;
            int next = progress.Percent.HasValue ? (int)progress.Percent.Value / 10 : -1;
            string key = message.StartsWith("下载 ", StringComparison.Ordinal) && progress.Percent.HasValue ? "下载" : message;
            if (key != previous || next != bucket) { Log("WORK " + message + (progress.Percent.HasValue ? " " + progress.Percent.Value.ToString("F1") + "%" : "")); previous = key; bucket = next; }
        }
    }
    private sealed class AuditedProcesses : IProcessManager
    {
        private readonly ProcessManager actual;
        private readonly List<string> audit = new List<string>();
        public AuditedProcesses(ProcessManager actual) { this.actual = actual; }
        public int AuditCount { get { lock (audit) return audit.Count; } }
        public List<string> AuditAfter(int offset) { lock (audit) return audit.Skip(offset).ToList(); }
        private void Record(string value) { lock (audit) audit.Add(value); Log("CONTROL " + value); }
        public event EventHandler<ServerLogEventArgs> LogReceived { add { actual.LogReceived += value; } remove { actual.LogReceived -= value; } }
        public event EventHandler StatusChanged { add { actual.StatusChanged += value; } remove { actual.StatusChanged -= value; } }
        public ServerStatus GetStatus(ServerInstance s) => actual.GetStatus(s);
        public Task StartAsync(ServerInstance s, IProgress<WorkProgress> p, CancellationToken ct) => actual.StartAsync(s, p, ct);
        public Task<bool> StopAsync(ServerInstance s, int seconds, CancellationToken ct) { Record("STOP:" + s.StopCommand); return actual.StopAsync(s, seconds, ct); }
        public void Kill(ServerInstance s) { Record("KILL"); actual.Kill(s); }
        public void Send(ServerInstance s, string command) { Record("SEND:" + command); actual.Send(s, command); }
        public IReadOnlyList<string> ReadLog(ServerInstance s) => actual.ReadLog(s);
        public ResourceSample GetResources(ServerInstance s) => actual.GetResources(s);
        public void Dispose() => actual.Dispose();
    }
    private sealed class Options
    {
        public string Data, Version = "1.21.1", Loader = "", Java = "", StorageDirectory = "", Phase = "all";
        public ServerKind Game = ServerKind.Vanilla;
        public bool AcceptEula;
        public int Port = 25586, Memory = 1536, Timeout = 2400, ReadyTimeout = 600;
        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "--accept-eula") { result.AcceptEula = true; continue; }
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + option);
                string value = args[i];
                switch (option)
                {
                    case "--data": result.Data = value; break;
                    case "--game": if (!Enum.TryParse(value, true, out result.Game) || result.Game == ServerKind.Custom) throw new ArgumentException("Unknown preset game: " + value); break;
                    case "--version": result.Version = value; break;
                    case "--loader": result.Loader = value; break;
                    case "--java": result.Java = value; break;
                    case "--storage": result.StorageDirectory = Path.GetFullPath(value); break;
                    case "--phase": result.Phase = value.ToLowerInvariant(); break;
                    case "--port": result.Port = int.Parse(value); break;
                    case "--memory": result.Memory = int.Parse(value); break;
                    case "--timeout": result.Timeout = int.Parse(value); break;
                    case "--ready-timeout": result.ReadyTimeout = int.Parse(value); break;
                    default: throw new ArgumentException("Unknown option: " + option);
                }
            }
            Require(!string.IsNullOrWhiteSpace(result.Data), "Required --data: empty or previously marked test-only directory.");
            Require(new[] { "all", "install", "verify" }.Contains(result.Phase), "--phase must be all, install or verify.");
            Require(result.Port > 0 && result.Port <= 65535 && result.Timeout > 0 && result.ReadyTimeout > 0, "Invalid port/timeout.");
            return result;
        }
    }
}
