using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWorkbench.Core
{
 public enum ServerKind { Custom, Vanilla, Fabric, Forge, Terraria, TModLoader }
 public enum RuntimeKind { Native, Java, DotNet, Script }
 public sealed class RuntimeRequirement
 {
  public RuntimeKind Kind { get; set; } = RuntimeKind.Native;
  public int JavaMajor { get; set; }
  public string Version { get; set; } = "";
  public string Framework { get; set; } = "Microsoft.NETCore.App";
  public string Architecture { get; set; } = "x64";
  public string RollForward { get; set; } = "Minor";
 }
 public sealed class InstalledMod
 {
  public string ProjectId { get; set; } = "";
  public string VersionId { get; set; } = "";
  public string Name { get; set; } = "";
  public string FileName { get; set; } = "";
  public string Hash { get; set; } = "";
  public string InternalName { get; set; } = "";
  public bool Enabled { get; set; } = true;
  public List<string> RequiredProjects { get; set; } = new List<string>();
 }
 public sealed class ServerInstance
 {
  public int SchemaVersion { get; set; } = 1;
  public string Id { get; set; } = Guid.NewGuid().ToString("N");
  public string Name { get; set; } = "新服务器";
  public string StorageDirectory { get; set; } = "";
  public ServerKind Kind { get; set; }
  public string GameVersion { get; set; } = "";
  public string LoaderVersion { get; set; } = "";
  public string EntryPoint { get; set; } = "";
  public string WorkingDirectory { get; set; } = ".";
  public List<string> Arguments { get; set; } = new List<string>();
  public Dictionary<string,string> Environment { get; set; } = new Dictionary<string,string>();
  public RuntimeRequirement Runtime { get; set; } = new RuntimeRequirement();
  public string RuntimePath { get; set; } = "";
  public int Port { get; set; }
  public bool Udp { get; set; }
  public int MemoryMb { get; set; } = 2048;
  public string StopCommand { get; set; } = "";
  public string ReadyPattern { get; set; } = "";
  public string ModDirectory { get; set; } = "mods";
  public string SourceUrl { get; set; } = "";
  public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
  public List<InstalledMod> Mods { get; set; } = new List<InstalledMod>();
  public SaveProfile Save { get; set; }
  public int AutoSaveMinutes { get; set; } = 5;
 }
 public sealed class WorkProgress
 {
  public string Message { get; set; }
  public double? Percent { get; set; }
  public WorkProgress(string message, double? percent = null) { Message=message; Percent=percent; }
 }
 public sealed class DownloadRequest
 {
  public string Url { get; set; }
  public string Destination { get; set; }
  public string Hash { get; set; } = "";
  public string HashAlgorithm { get; set; } = "SHA256";
 }
 public sealed class VersionOption
 {
  public string Id { get; set; } = "";
  public string Label { get; set; } = "";
  public string Url { get; set; } = "";
  public override string ToString() => string.IsNullOrEmpty(Label) ? Id : Label;
 }
 public sealed class CreateServerRequest
 {
  public string Name { get; set; } = "新服务器";
  public string StorageDirectory { get; set; } = "";
  public ServerKind Kind { get; set; }
  public string GameVersion { get; set; } = "";
  public string LoaderVersion { get; set; } = "";
  public bool AcceptEula { get; set; }
  public int Port { get; set; }
  public int MemoryMb { get; set; } = 2048;
  public string RuntimePath { get; set; } = "";
  public string WorldName { get; set; } = "World";
  public string Password { get; set; } = "";
  public int MaxPlayers { get; set; } = 8;
 }
 public sealed class EntryCandidate
 {
  public string RelativePath { get; set; }
  public string Description { get; set; }
  public bool Compatible { get; set; } = true;
  public RuntimeRequirement Runtime { get; set; } = new RuntimeRequirement();
  public override string ToString() => RelativePath + " — " + Description;
 }
 public sealed class ImportDraft
 {
  public string StagingDirectory { get; set; }
  public string Source { get; set; }
  public List<EntryCandidate> Candidates { get; set; } = new List<EntryCandidate>();
  public List<string> Notes { get; set; } = new List<string>();
 }
 public sealed class RuntimeResolution
 {
  public string Executable { get; set; }
  public string Description { get; set; }
 }
 public sealed class ModSearchResult
 {
  public string Id { get; set; }
  public string Name { get; set; }
  public string Description { get; set; }
  public string ServerSupport { get; set; }
  public override string ToString() => Name + " — " + Description;
 }
 public sealed class BackupInfo
 {
  public string Path { get; set; }
  public DateTime CreatedUtc { get; set; }
  public long Size { get; set; }
  public override string ToString() => CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  " + (Size/1024/1024.0).ToString("F1") + " MB";
 }
 public sealed class ServerStatus
 {
  public string InstanceId { get; set; }
  public bool Running { get; set; }
  public bool Ready { get; set; }
  public string Message { get; set; }
 }
 public sealed class ResourceSample
 {
  public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
  public double CpuPercent { get; set; }
  public long MemoryBytes { get; set; }
  public TimeSpan Uptime { get; set; }
  public int ProcessCount { get; set; }
  public string Error { get; set; }
 }
 public sealed class SaveProfile
 {
  public string Command { get; set; } = "";
  public string CompletedPattern { get; set; } = "";
  public string PauseCommand { get; set; } = "";
  public string ResumeCommand { get; set; } = "";
  public string EmergencyCommand { get; set; } = "";
  public List<string> Paths { get; set; } = new List<string>();
 }
 public sealed class SaveSnapshotInfo
 {
  public string Path { get; set; }
  public DateTime CreatedUtc { get; set; }
  public string Reason { get; set; }
  public long Size { get; set; }
  public override string ToString()=>CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+"  "+Reason+"  "+(Size/1048576.0).ToString("F1")+" MB";
 }
 public sealed class ServerLogEventArgs : EventArgs
 {
  public string InstanceId { get; set; }
  public string Line { get; set; }
 }
 public sealed class AppPaths
 {
  public string Root { get; }
  public string Instances => System.IO.Path.Combine(Root,"Instances");
  public string Runtimes => System.IO.Path.Combine(Root,"Runtimes");
  public string Temp => System.IO.Path.Combine(Root,"Temp");
  public string Backups => System.IO.Path.Combine(Root,"Backups");
  public AppPaths(string root) { Root=System.IO.Path.GetFullPath(root); }
  public string InstanceIndexDirectory(ServerInstance s) => System.IO.Path.Combine(Instances,s.Id);
  public string InstanceDirectory(ServerInstance s) => string.IsNullOrWhiteSpace(s.StorageDirectory)?InstanceIndexDirectory(s):InstanceRepository.NormalizeStorageDirectory(s.StorageDirectory);
 }
 public interface IInstanceRepository
 {
  IReadOnlyList<ServerInstance> List();
  void Save(ServerInstance instance);
  void Delete(string id);
 }
 public interface ITransferService
 {
  Task DownloadAsync(DownloadRequest request,IProgress<WorkProgress> progress,CancellationToken ct);
  void ExtractZip(string archive,string destination,CancellationToken ct);
  void CopyTree(string source,string destination,CancellationToken ct);
 }
 public interface IRuntimeProvider
 {
  Task<RuntimeResolution> EnsureAsync(RuntimeRequirement requirement,string existingPath,IProgress<WorkProgress> progress,CancellationToken ct);
 }
 public interface IGameAdapter
 {
  ServerKind Kind { get; }
  Task<IReadOnlyList<VersionOption>> GetVersionsAsync(CancellationToken ct);
  Task<IReadOnlyList<VersionOption>> GetLoaderVersionsAsync(string gameVersion,CancellationToken ct);
  Task<ServerInstance> InstallAsync(CreateServerRequest request,IProgress<WorkProgress> progress,CancellationToken ct);
 }
 public interface IPresetService
 {
  Task<IReadOnlyList<VersionOption>> GetVersionsAsync(ServerKind kind,CancellationToken ct);
  Task<IReadOnlyList<VersionOption>> GetLoaderVersionsAsync(ServerKind kind,string gameVersion,CancellationToken ct);
  Task<ServerInstance> InstallAsync(CreateServerRequest request,IProgress<WorkProgress> progress,CancellationToken ct);
 }
 public interface IImportService
 {
  Task<ImportDraft> StageAsync(string source,IProgress<WorkProgress> progress,CancellationToken ct);
  Task<ServerInstance> CommitAsync(ImportDraft draft,ServerInstance configuration,IProgress<WorkProgress> progress,CancellationToken ct);
  void Discard(ImportDraft draft);
 }
 public interface IProcessManager : IDisposable
 {
  event EventHandler<ServerLogEventArgs> LogReceived;
  event EventHandler StatusChanged;
  ServerStatus GetStatus(ServerInstance instance);
  Task StartAsync(ServerInstance instance,IProgress<WorkProgress> progress,CancellationToken ct);
  Task<bool> StopAsync(ServerInstance instance,int timeoutSeconds,CancellationToken ct);
  void Kill(ServerInstance instance);
  void Send(ServerInstance instance,string command);
  IReadOnlyList<string> ReadLog(ServerInstance instance);
  ResourceSample GetResources(ServerInstance instance);
 }
 public interface IBackupService
 {
  Task<BackupInfo> CreateAsync(ServerInstance instance,IProgress<WorkProgress> progress,CancellationToken ct);
  IReadOnlyList<BackupInfo> List(ServerInstance instance);
  Task RestoreAsync(ServerInstance instance,BackupInfo backup,IProgress<WorkProgress> progress,CancellationToken ct);
 }
 public interface IModProvider
 {
  Task<IReadOnlyList<ModSearchResult>> SearchAsync(ServerInstance instance,string query,CancellationToken ct);
  Task<IReadOnlyList<VersionOption>> GetVersionsAsync(ServerInstance instance,string projectId,CancellationToken ct);
  Task InstallAsync(ServerInstance instance,string versionId,IProgress<WorkProgress> progress,CancellationToken ct);
  Task ImportLocalAsync(ServerInstance instance,string source,string internalName,IProgress<WorkProgress> progress,CancellationToken ct);
  Task SetEnabledAsync(ServerInstance instance,InstalledMod mod,bool enabled,CancellationToken ct);
  Task RemoveAsync(ServerInstance instance,InstalledMod mod,CancellationToken ct);
 }
 public sealed class AppServices : IDisposable
 {
  public AppPaths Paths { get; set; }
  public IInstanceRepository Repository { get; set; }
  public ITransferService Transfers { get; set; }
  public IRuntimeProvider Runtimes { get; set; }
  public IPresetService Presets { get; set; }
  public IImportService Importer { get; set; }
  public IProcessManager Processes { get; set; }
  public IBackupService Backups { get; set; }
  public IModProvider Mods { get; set; }
  public SaveService Saves { get; set; }
  public FrpTunnelService Tunnels { get; set; }
  public void Dispose() { Tunnels?.Dispose();Saves?.Dispose();Processes?.Dispose(); }
 }
}
