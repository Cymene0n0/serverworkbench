using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ServerWorkbench.Core
{
 /// <summary>Modrinth and local-file installs share the same stopped-server, backup and file transaction path.</summary>
 public sealed class ModProvider : IModProvider
 {
  private const string Api = "https://api.modrinth.com/v2/";
  private static readonly HttpClient SharedClient = CreateClient();
  private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new ConcurrentDictionary<string, SemaphoreSlim>();
  private readonly AppPaths paths;
  private readonly IInstanceRepository repository;
  private readonly ITransferService transfers;
  private readonly IProcessManager processes;
  private readonly IBackupService backups;
  private readonly HttpClient client;

  public ModProvider(AppPaths paths, IInstanceRepository repository, ITransferService transfers,
                     IProcessManager processes, IBackupService backups)
   : this(paths, repository, transfers, processes, backups, SharedClient) { }

  public ModProvider(AppPaths paths, IInstanceRepository repository, ITransferService transfers,
                     IProcessManager processes, IBackupService backups, HttpClient client)
  {
   this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
   this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
   this.transfers = transfers ?? throw new ArgumentNullException(nameof(transfers));
   this.processes = processes ?? throw new ArgumentNullException(nameof(processes));
   this.backups = backups ?? throw new ArgumentNullException(nameof(backups));
   this.client = client ?? throw new ArgumentNullException(nameof(client));
  }

  private static HttpClient CreateClient()
  {
   var value = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
   value.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "ServerWorkbench/0.1 (Windows dedicated server manager)");
   return value;
  }

  public async Task<IReadOnlyList<ModSearchResult>> SearchAsync(ServerInstance instance, string query, CancellationToken ct)
  {
   string loader = Loader(instance);
   var facets = new JArray(new JArray("project_type:mod"), new JArray("categories:" + loader),
                          new JArray("versions:" + instance.GameVersion),
                          new JArray("server_side:required", "server_side:optional"));
   var result = (JObject)await JsonAsync("search?limit=40&query=" + Escape(query ?? "") + "&facets=" + Escape(facets.ToString(Formatting.None)), ct).ConfigureAwait(false);
   return (result["hits"] as JArray ?? new JArray()).OfType<JObject>()
    .Where(ServerCompatible).Select(hit => new ModSearchResult
    {
     Id = Text(hit, "project_id"), Name = Text(hit, "title"), Description = Text(hit, "description"),
     ServerSupport = SupportLabel(hit)
    }).ToList();
  }

  public async Task<IReadOnlyList<VersionOption>> GetVersionsAsync(ServerInstance instance, string projectId, CancellationToken ct)
  {
   Loader(instance);
   var project = (JObject)await JsonAsync("project/" + EscapeId(projectId), ct).ConfigureAwait(false);
   if (!ServerCompatible(project)) throw new InvalidOperationException("这个项目只适用于客户端，不能安装到专用服务器。");
   var versions = await VersionsAsync(instance, projectId, ct).ConfigureAwait(false);
   return versions.Select(v => new VersionOption
   {
    Id = Text(v, "id"), Label = Text(v, "version_number") + " · " + Text(v, "name") +
     (Text(v, "version_type") == "release" ? "" : " [" + Text(v, "version_type") + "]")
   }).ToList();
  }

  public async Task InstallAsync(ServerInstance instance, string versionId, IProgress<WorkProgress> progress, CancellationToken ct)
  {
   Loader(instance);
   var gate = Gate(instance);
   await gate.WaitAsync(ct).ConfigureAwait(false);
   string stage = null;
   try
   {
    EnsureStopped(instance);
    progress?.Report(new WorkProgress("正在解析 Mod 和必需依赖…"));
    var context = new ResolveContext(instance);
    var root = (JObject)await JsonAsync("version/" + EscapeId(versionId), ct).ConfigureAwait(false);
    await ResolveAsync(context, root, ct).ConfigureAwait(false);
    await CheckIncompatibilitiesAsync(context, ct).ConfigureAwait(false);
    stage = NewStage();
    var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var records = CloneMods(instance.Mods);
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    int completed = 0;
    foreach (var node in context.Ordered)
    {
     ct.ThrowIfCancellationRequested();
     string fileName = SafeFileName(Text(node.File, "filename"));
     if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Modrinth 返回的主文件不是 JAR：" + fileName);
     if (!names.Add(fileName)) throw new InvalidOperationException("不同 Mod 使用了相同文件名，无法安全安装：" + fileName);
     string hash = Text(node.File["hashes"] as JObject, "sha512");
     if (hash.Length != 128 || !Regex.IsMatch(hash, "^[0-9a-fA-F]+$")) throw new InvalidDataException("Mod 文件缺少有效的 SHA-512 校验值。");
     string destination = ActivePath(instance, fileName);
     CheckOwnership(instance, destination, node.ProjectId, hash);
     string stagedFile = Path.Combine(stage, "download-" + completed + ".jar");
     progress?.Report(new WorkProgress("下载 " + node.Name + "（含依赖）", completed * 100.0 / context.Ordered.Count));
     await transfers.DownloadAsync(new DownloadRequest
     {
      Url = Text(node.File, "url"), Destination = stagedFile, Hash = hash, HashAlgorithm = "SHA512"
     }, progress, ct).ConfigureAwait(false);
     // The provider verifies independently so an injected or future transfer implementation cannot bypass integrity.
     if (!HashFile(stagedFile, "SHA512").Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mod 文件校验失败：" + fileName);
     foreach (var old in records.Where(m => m.ProjectId == node.ProjectId).ToList())
     {
      string oldPath = ModPath(instance, old);
      if (!oldPath.Equals(destination, StringComparison.OrdinalIgnoreCase)) changes[oldPath] = null;
      records.Remove(old);
     }
     changes[destination] = stagedFile;
     records.Add(new InstalledMod
     {
      ProjectId = node.ProjectId, VersionId = node.VersionId, Name = node.Name, FileName = fileName,
      Hash = hash.ToLowerInvariant(), Enabled = true, RequiredProjects = node.Required.ToList()
     });
     completed++;
    }
    await ApplyAsync(instance, records, changes, stage, progress, ct).ConfigureAwait(false);
    progress?.Report(new WorkProgress("Mod 与必需依赖已安装，启动服务器后生效。", 100));
   }
   finally { DeleteStage(stage); gate.Release(); }
  }

  public async Task ImportLocalAsync(ServerInstance instance, string source, string internalName, IProgress<WorkProgress> progress, CancellationToken ct)
  {
   if (instance.Kind == ServerKind.Vanilla) throw new InvalidOperationException("原版 Minecraft 不加载 Mod，请创建 Fabric 或 Forge 实例。");
   if (instance.Kind == ServerKind.Terraria) throw new InvalidOperationException("原版泰拉瑞亚不加载 .tmod，请使用 tModLoader 实例。");
   if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("请选择本地 Mod 文件或目录。");
   source = Path.GetFullPath(source);
   var gate = Gate(instance);
   await gate.WaitAsync(ct).ConfigureAwait(false);
   string stage = null;
   try
   {
    EnsureStopped(instance);
    bool directory = Directory.Exists(source);
    if (!directory && !File.Exists(source)) throw new FileNotFoundException("Mod 来源不存在。", source);
    var files = directory ? EnumerateLocal(source, ct).ToList() : new List<string> { source };
    if (files.Count > 10000) throw new InvalidDataException("一次最多导入 10000 个 Mod 文件。");
    if (files.Sum(f => new FileInfo(f).Length) > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("一次导入的 Mod 总大小不能超过 8GB。");
    if (!directory && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不支持通过链接导入 Mod。");
    stage = NewStage();
    var records = CloneMods(instance.Mods);
    var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    HashSet<string> desiredEnabled = null;
    bool tml = instance.Kind == ServerKind.TModLoader;
    var enabledFiles = tml ? files.Where(f => Path.GetFileName(f).Equals("enabled.json", StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
    if (enabledFiles.Count > 1) throw new InvalidDataException("目录中包含多个 enabled.json，请单独选择所需模组目录。");
    if (enabledFiles.Count == 1) desiredEnabled = ReadEnabled(enabledFiles[0]);
    int index = 0;
    foreach (string file in files)
    {
     ct.ThrowIfCancellationRequested();
     string extension = Path.GetExtension(file);
     if (tml && !extension.Equals(".tmod", StringComparison.OrdinalIgnoreCase)) continue;
     if ((instance.Kind == ServerKind.Fabric || instance.Kind == ServerKind.Forge) && !extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)) continue;
     string relative = instance.Kind == ServerKind.Custom && directory
      ? file.Substring(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1).Replace('\\', '/')
      : SafeFileName(Path.GetFileName(file));
     relative = SafeRelative(relative);
     if (!added.Add(relative)) throw new InvalidOperationException("多个导入文件使用相同名称：" + relative);
     string name = Path.GetFileNameWithoutExtension(file), modVersion = "local", loaderVersion = "";
     if (tml)
     {
      var information = ReadTmod(file, directory ? null : internalName);
      name = information.Name; modVersion = information.Version; loaderVersion = information.LoaderVersion;
      if (!string.IsNullOrWhiteSpace(internalName) && !directory && !name.Equals(internalName, StringComparison.Ordinal))
       throw new InvalidDataException("填写的内部名称与 .tmod 元数据不同：" + name);
     }
     string project = tml ? "local-tmod:" + name.ToLowerInvariant() : "local:" + relative.ToLowerInvariant();
     var oldByName = tml ? records.FirstOrDefault(m => m.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
     if (oldByName != null) project = oldByName.ProjectId;
     string hash = HashFile(file, "SHA512");
     bool enabled = !tml || desiredEnabled == null || desiredEnabled.Contains(name);
     var record = new InstalledMod
     {
      ProjectId = project, VersionId = modVersion, FileName = relative, Name = name,
      InternalName = tml ? name : "", Hash = hash, Enabled = enabled
     };
     string target = ModPath(instance, record);
     CheckOwnership(instance, target, project, hash);
     foreach (var old in records.Where(m => m.ProjectId == project).ToList())
     {
      string oldPath = ModPath(instance, old);
      if (!oldPath.Equals(target, StringComparison.OrdinalIgnoreCase)) changes[oldPath] = null;
      records.Remove(old);
     }
     string stagedFile = Path.Combine(stage, "local-" + index++);
     File.Copy(file, stagedFile, false);
     changes[target] = stagedFile;
     records.Add(record);
     progress?.Report(new WorkProgress("准备本地 Mod：" + name + (string.IsNullOrEmpty(loaderVersion) ? "" : "（tML " + loaderVersion + "）")));
    }
    if (tml && desiredEnabled != null)
    {
     foreach (string name in desiredEnabled)
      if (!records.Any(m => m.InternalName.Equals(name, StringComparison.OrdinalIgnoreCase)))
       throw new InvalidOperationException("启用清单中的 Mod 尚未导入：" + name + "。请同时导入对应的 .tmod 文件。");
     foreach (var mod in records)
     {
      bool enabled = desiredEnabled.Contains(mod.InternalName);
      if (enabled == mod.Enabled) continue;
      var old = instance.Mods.FirstOrDefault(m => m.ProjectId == mod.ProjectId);
      if (old != null)
      {
       string from = ModPath(instance, old);
       if (File.Exists(from))
       {
        string copy = Path.Combine(stage, "toggle-" + index++); File.Copy(from, copy);
        changes[from] = null;
        mod.Enabled = enabled;
        changes[ModPath(instance, mod)] = copy;
       }
       else throw new FileNotFoundException("已记录的 Mod 文件丢失，请重新导入。", from);
      }
      else mod.Enabled = enabled;
     }
    }
    if (added.Count == 0 && desiredEnabled == null) throw new InvalidOperationException(tml ? "没有找到 .tmod 文件。" : "没有找到可导入的 Mod 文件。");
    if (tml) AddEnabledChange(instance, records, stage, changes, desiredEnabled);
    await ApplyAsync(instance, records, changes, stage, progress, ct).ConfigureAwait(false);
    progress?.Report(new WorkProgress("本地 Mod 已导入。", 100));
   }
   finally { DeleteStage(stage); gate.Release(); }
  }

  public async Task SetEnabledAsync(ServerInstance instance, InstalledMod mod, bool enabled, CancellationToken ct)
  {
   var gate = Gate(instance); await gate.WaitAsync(ct).ConfigureAwait(false); string stage = null;
   try
   {
    EnsureStopped(instance); var original = FindRecord(instance, mod);
    if (original.Enabled == enabled) return;
    if (!enabled) CheckDependants(instance, original);
    else foreach (string required in original.RequiredProjects ?? new List<string>())
     if (!instance.Mods.Any(m => m.ProjectId == required && m.Enabled)) throw new InvalidOperationException("请先启用必需依赖：" + required);
    string from = ModPath(instance, original);
    if (!File.Exists(from)) throw new FileNotFoundException("Mod 文件已丢失，请重新导入。", from);
    VerifyStoredHash(from, original.Hash);
    stage = NewStage(); string stagedFile = Path.Combine(stage, "mod"); File.Copy(from, stagedFile);
    var records = CloneMods(instance.Mods); var changed = records.First(m => m.ProjectId == original.ProjectId && m.FileName == original.FileName);
    changed.Enabled = enabled;
    string to = ModPath(instance, changed);
    if (File.Exists(to)) throw new IOException("目标位置已有文件，请先处理重复 Mod：" + changed.FileName);
    var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [from] = null, [to] = stagedFile };
    if (instance.Kind == ServerKind.TModLoader) AddEnabledChange(instance, records, stage, changes, null);
    await ApplyAsync(instance, records, changes, stage, null, ct).ConfigureAwait(false);
   }
   finally { DeleteStage(stage); gate.Release(); }
  }

  public async Task RemoveAsync(ServerInstance instance, InstalledMod mod, CancellationToken ct)
  {
   var gate = Gate(instance); await gate.WaitAsync(ct).ConfigureAwait(false); string stage = null;
   try
   {
    EnsureStopped(instance); var original = FindRecord(instance, mod); CheckDependants(instance, original);
    stage = NewStage(); var records = CloneMods(instance.Mods);
    records.RemoveAll(m => m.ProjectId == original.ProjectId && m.FileName == original.FileName);
    var changes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ModPath(instance, original)] = null };
    if (instance.Kind == ServerKind.TModLoader) AddEnabledChange(instance, records, stage, changes, null);
    await ApplyAsync(instance, records, changes, stage, null, ct).ConfigureAwait(false);
   }
   finally { DeleteStage(stage); gate.Release(); }
  }

  private async Task ResolveAsync(ResolveContext context, JObject version, CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();
   string projectId = Text(version, "project_id"), versionId = Text(version, "id");
   if (string.IsNullOrEmpty(projectId) || string.IsNullOrEmpty(versionId)) throw new InvalidDataException("Modrinth 版本缺少项目或版本 ID。");
   if (context.Visiting.Contains(projectId)) throw new InvalidOperationException("发现循环必需依赖：" + projectId + "。请手动核对兼容组合。");
   if (context.Nodes.TryGetValue(projectId, out PlanNode found))
   {
    if (found.VersionId != versionId) throw new InvalidOperationException("两个 Mod 要求同一前置的不同版本：" + projectId);
    return;
   }
   if (context.Nodes.Count >= 256) throw new InvalidOperationException("必需依赖超过 256 个，首版暂不自动安装该组合。");
   if (!CompatibleVersion(context.Instance, version)) throw new InvalidOperationException("所选 Mod 或依赖不支持当前游戏版本、加载器或服务端：" + Text(version, "name"));
   var project = (JObject)await JsonAsync("project/" + EscapeId(projectId), ct).ConfigureAwait(false);
   if (!ServerCompatible(project)) throw new InvalidOperationException("必需依赖只适用于客户端：" + Text(project, "title"));
   var files = (version["files"] as JArray ?? new JArray()).OfType<JObject>()
    .Where(f => Text(f, "filename").EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToList();
   var file = files.FirstOrDefault(f => (bool?)f["primary"] == true) ?? files.FirstOrDefault();
   if (file == null) throw new InvalidDataException("找不到适用于服务端的 JAR 文件：" + Text(project, "title"));
   var node = new PlanNode { ProjectId = projectId, VersionId = versionId, Name = Text(project, "title"), File = file, Version = version };
   context.Nodes.Add(projectId, node); context.Visiting.Add(projectId);
   foreach (var dependency in Dependencies(version).Where(d => Text(d, "dependency_type") == "required"))
   {
    JObject child;
    string childVersion = Text(dependency, "version_id"), childProject = Text(dependency, "project_id");
    if (!string.IsNullOrEmpty(childVersion)) child = (JObject)await JsonAsync("version/" + EscapeId(childVersion), ct).ConfigureAwait(false);
    else if (!string.IsNullOrEmpty(childProject))
    {
     if (context.Nodes.TryGetValue(childProject, out PlanNode planned)) child = planned.Version;
     else
     {
      var existing = context.Instance.Mods.FirstOrDefault(m => m.ProjectId == childProject && m.Enabled && !string.IsNullOrEmpty(m.VersionId));
      child = existing == null ? null : (JObject)await JsonAsync("version/" + EscapeId(existing.VersionId), ct).ConfigureAwait(false);
      if (child == null || !CompatibleVersion(context.Instance, child))
       child = (await VersionsAsync(context.Instance, childProject, ct).ConfigureAwait(false)).FirstOrDefault();
      if (child == null) throw new InvalidOperationException("找不到匹配当前版本的必需依赖：" + childProject);
     }
    }
    else throw new InvalidOperationException("依赖没有可下载的项目/版本 ID，需要手动安装：" + Text(dependency, "file_name"));
    string actualProject = Text(child, "project_id");
    if (!string.IsNullOrEmpty(childProject) && actualProject != childProject) throw new InvalidDataException("依赖项目与指定版本不一致。");
    node.Required.Add(actualProject);
    await ResolveAsync(context, child, ct).ConfigureAwait(false);
   }
   context.Visiting.Remove(projectId); context.Ordered.Add(node);
  }

  private async Task CheckIncompatibilitiesAsync(ResolveContext context, CancellationToken ct)
  {
   var versions = new Dictionary<string, string>(StringComparer.Ordinal);
   foreach (var old in context.Instance.Mods.Where(m => m.Enabled && !m.ProjectId.StartsWith("local", StringComparison.Ordinal))) versions[old.ProjectId] = old.VersionId;
   foreach (var node in context.Ordered) versions[node.ProjectId] = node.VersionId;
   var metadata = context.Ordered.Select(n => n.Version).ToList();
   foreach (var old in context.Instance.Mods.Where(m => m.Enabled && !m.ProjectId.StartsWith("local", StringComparison.Ordinal) && !context.Nodes.ContainsKey(m.ProjectId)))
    if (!string.IsNullOrWhiteSpace(old.VersionId)) metadata.Add((JObject)await JsonAsync("version/" + EscapeId(old.VersionId), ct).ConfigureAwait(false));
   foreach (var version in metadata)
   {
    foreach (var dependency in Dependencies(version).Where(d => Text(d, "dependency_type") == "incompatible"))
    {
     string project = Text(dependency, "project_id"), specificVersion = Text(dependency, "version_id");
     if (string.IsNullOrEmpty(project) && !string.IsNullOrEmpty(specificVersion))
      project = Text((JObject)await JsonAsync("version/" + EscapeId(specificVersion), ct).ConfigureAwait(false), "project_id");
     if (!string.IsNullOrEmpty(project) && versions.TryGetValue(project, out string actual) &&
         (string.IsNullOrEmpty(specificVersion) || actual == specificVersion))
      throw new InvalidOperationException("检测到声明的 Mod 冲突：" + Text(version, "name") + " 与 " + project + "。请先移除不兼容项目。");
    }
   }
   // A pinned required dependency of an unchanged installed mod cannot be silently replaced by this install.
   foreach (var version in metadata.Where(v => !context.Nodes.ContainsKey(Text(v, "project_id"))))
    foreach (var dependency in Dependencies(version).Where(d => Text(d, "dependency_type") == "required" && !string.IsNullOrEmpty(Text(d, "version_id"))))
    {
     string specific = Text(dependency, "version_id"), project = Text(dependency, "project_id");
     if (string.IsNullOrEmpty(project)) project = Text((JObject)await JsonAsync("version/" + EscapeId(specific), ct).ConfigureAwait(false), "project_id");
     if (versions.TryGetValue(project, out string actual) && actual != specific)
      throw new InvalidOperationException("更新会破坏已安装 Mod 的固定版本依赖：" + Text(version, "name"));
    }
  }

  private async Task<List<JObject>> VersionsAsync(ServerInstance instance, string project, CancellationToken ct)
  {
   string query = "project/" + EscapeId(project) + "/version?game_versions=" + Escape(new JArray(instance.GameVersion).ToString(Formatting.None)) +
    "&loaders=" + Escape(new JArray(Loader(instance)).ToString(Formatting.None));
   return ((JArray)await JsonAsync(query, ct).ConfigureAwait(false)).OfType<JObject>()
    .Where(v => CompatibleVersion(instance, v)).OrderByDescending(v => Text(v, "date_published"), StringComparer.Ordinal).ToList();
  }

  private async Task<JToken> JsonAsync(string relative, CancellationToken ct)
  {
   for (int attempt = 0; ; attempt++)
   {
    using (var request = new HttpRequestMessage(HttpMethod.Get, Api + relative))
    {
     request.Headers.TryAddWithoutValidation("User-Agent", "ServerWorkbench/0.1 (Windows dedicated server manager)");
     using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
     {
      if (((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) && attempt < 2)
      {
       var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
       if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
       if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
       await Task.Delay(delay, ct).ConfigureAwait(false); continue;
      }
      if (!response.IsSuccessStatusCode) throw new HttpRequestException("Modrinth 请求失败（HTTP " + (int)response.StatusCode + "）。请稍后重试。");
      if (response.Content.Headers.ContentLength > 16L * 1024 * 1024) throw new InvalidDataException("Modrinth 响应过大。");
      string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
      ct.ThrowIfCancellationRequested();
      if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("Modrinth 响应过大。");
      return JToken.Parse(text);
     }
    }
   }
  }

  private async Task ApplyAsync(ServerInstance instance, List<InstalledMod> records, Dictionary<string, string> changes,
                                string stage, IProgress<WorkProgress> progress, CancellationToken ct)
  {
   EnsureStopped(instance);
   if (Directory.Exists(paths.InstanceDirectory(instance)) && Directory.EnumerateFileSystemEntries(paths.InstanceDirectory(instance)).Any())
   {
    progress?.Report(new WorkProgress("应用 Mod 变更前备份实例…"));
    await backups.CreateAsync(instance, progress, ct).ConfigureAwait(false);
   }
   EnsureStopped(instance); ct.ThrowIfCancellationRequested();
   await Task.Run(() =>
   {
    var oldRecords = instance.Mods;
    string rollback = Path.Combine(stage, "rollback"); Directory.CreateDirectory(rollback);
    var saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var touched = new List<string>();
    try
    {
     int index = 0;
     foreach (var change in changes)
     {
      ct.ThrowIfCancellationRequested(); EnsureStopped(instance);
      string target = change.Key;
      ValidateTarget(instance, target);
      if (Directory.Exists(target)) throw new IOException("Mod 目标是目录，不能覆盖：" + target);
      if (File.Exists(target))
      {
       string original = Path.Combine(rollback, (index++).ToString());
       File.Move(target, original); saved.Add(target, original);
      }
      touched.Add(target);
      if (change.Value != null)
      {
       Directory.CreateDirectory(Path.GetDirectoryName(target));
       File.Copy(change.Value, target, false);
      }
     }
     ct.ThrowIfCancellationRequested(); EnsureStopped(instance);
     instance.Mods = records; repository.Save(instance);
    }
    catch (Exception error)
    {
     instance.Mods = oldRecords;
     var rollbackErrors = new List<Exception>();
     foreach (string target in touched.AsEnumerable().Reverse())
     {
      try
      {
       if (File.Exists(target)) File.Delete(target);
       if (saved.TryGetValue(target, out string original))
       {
        Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(original, target);
       }
      }
      catch (Exception restoreError) { rollbackErrors.Add(restoreError); }
     }
     try { repository.Save(instance); } catch (Exception restoreError) { rollbackErrors.Add(restoreError); }
     if (rollbackErrors.Count != 0)
     {
      // Keep rollback data available for recovery rather than deleting the only remaining copies.
      string recovery = stage + "-recovery";
      try { Directory.Move(stage, recovery); } catch { recovery = stage; }
      throw new IOException("Mod 变更失败且部分文件无法回滚。请从实例备份恢复；回滚资料位于 " + recovery,
                            new AggregateException(new[] { error }.Concat(rollbackErrors)));
     }
     throw;
    }
   }, ct).ConfigureAwait(false);
  }

  private void AddEnabledChange(ServerInstance instance, List<InstalledMod> records, string stage,
                                Dictionary<string, string> changes, HashSet<string> replacement)
  {
   string enabledPath = ActivePath(instance, "enabled.json");
   var names = replacement ?? (File.Exists(enabledPath) ? ReadEnabled(enabledPath) : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
   foreach (var old in instance.Mods) if (!string.IsNullOrEmpty(old.InternalName)) names.Remove(old.InternalName);
   foreach (var mod in records.Where(m => m.Enabled))
   {
    if (string.IsNullOrWhiteSpace(mod.InternalName)) throw new InvalidOperationException("tModLoader Mod 缺少内部名称，请重新导入：" + mod.FileName);
    names.Add(ValidInternalName(mod.InternalName));
   }
   string staged = Path.Combine(stage, "enabled.json");
   File.WriteAllText(staged, JsonConvert.SerializeObject(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase), Formatting.Indented), new UTF8Encoding(false));
   changes[enabledPath] = staged;
  }

  private void CheckOwnership(ServerInstance instance, string target, string project, string expectedHash)
  {
   var owner = instance.Mods.FirstOrDefault(m => ModPath(instance, m).Equals(target, StringComparison.OrdinalIgnoreCase));
   if (owner != null && owner.ProjectId != project) throw new InvalidOperationException("文件名与另一个已安装 Mod 冲突：" + Path.GetFileName(target));
   if (File.Exists(target) && owner == null && !HashFile(target, "SHA512").Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
    throw new IOException("目标已有未纳入管理的同名文件，请先移走或导入：" + Path.GetFileName(target));
  }

  private void ValidateTarget(ServerInstance instance, string target)
  {
   string root = Path.GetFullPath(paths.InstanceDirectory(instance));
   string full = Path.GetFullPath(target);
   string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
   if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mod 路径越出了实例目录。");
   string parent = Path.GetDirectoryName(full);
   while (parent != null && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase))
   {
    if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Mod 目录不能包含符号链接或目录联接。");
    if (parent.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
    parent = Path.GetDirectoryName(parent);
   }
   if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Mod 文件不能是符号链接。");
  }

  private string ActivePath(ServerInstance instance, string relative)
  {
   string modDirectory = SafeRelative(instance.ModDirectory);
   if (modDirectory.Split('/').Any(p => p.Equals(".disabled-mods", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Mod 目录不能是禁用文件目录。");
   string result = PathSafety.ResolveUnder(paths.InstanceDirectory(instance), modDirectory + "/" + SafeRelative(relative));
   ValidateTarget(instance, result); return result;
  }

  private string ModPath(ServerInstance instance, InstalledMod mod)
  {
   if (mod.Enabled) return ActivePath(instance, mod.FileName);
   string result = PathSafety.ResolveUnder(paths.InstanceDirectory(instance), ".disabled-mods/" + SafeRelative(mod.FileName));
   ValidateTarget(instance, result); return result;
  }

  private static IEnumerable<string> EnumerateLocal(string directory, CancellationToken ct)
  {
   var pending = new Stack<string>(); pending.Push(directory);
   while (pending.Count > 0)
   {
    ct.ThrowIfCancellationRequested(); string current = pending.Pop();
    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Mod 目录中不支持链接或目录联接。");
    foreach (string file in Directory.EnumerateFiles(current))
    {
     if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Mod 目录中不支持链接文件。");
     yield return file;
    }
    foreach (string child in Directory.EnumerateDirectories(current)) pending.Push(child);
   }
  }

  private static HashSet<string> ReadEnabled(string path)
  {
   if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("enabled.json 过大。");
   JArray array;
   try { array = JArray.Parse(File.ReadAllText(path)); }
   catch (JsonException error) { throw new InvalidDataException("enabled.json 必须是内部 Mod 名称组成的 JSON 数组。", error); }
   if (array.Any(v => v.Type != JTokenType.String)) throw new InvalidDataException("enabled.json 只能包含字符串。");
   return new HashSet<string>(array.Select(v => ValidInternalName((string)v)), StringComparer.OrdinalIgnoreCase);
  }

  private static TmodInformation ReadTmod(string path, string fallbackName)
  {
   using (var stream = File.OpenRead(path))
   using (var reader = new BinaryReader(stream, Encoding.UTF8))
   {
    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "TMOD") throw new InvalidDataException("文件不是有效的 .tmod：" + Path.GetFileName(path));
    string loader = ReadShortString(reader, 128);
    byte[] expected = reader.ReadBytes(20), signature = reader.ReadBytes(256);
    if (expected.Length != 20 || signature.Length != 256) throw new InvalidDataException(".tmod 文件头不完整。");
    int length = reader.ReadInt32(); long dataStart = stream.Position;
    if (length < 0 || length != stream.Length - dataStart) throw new InvalidDataException(".tmod 数据长度不匹配，文件可能已损坏。");
    using (var sha = SHA1.Create()) if (!sha.ComputeHash(stream).SequenceEqual(expected)) throw new InvalidDataException(".tmod 内容校验失败，文件可能已损坏。");
    stream.Position = dataStart;
    if (Version.TryParse(loader, out Version parsed) && parsed >= new Version(0, 11))
     return new TmodInformation { LoaderVersion = loader, Name = ValidInternalName(ReadShortString(reader, 256)), Version = ReadShortString(reader, 128) };
    if (!string.IsNullOrWhiteSpace(fallbackName)) return new TmodInformation { LoaderVersion = loader, Name = ValidInternalName(fallbackName), Version = "local" };
    throw new InvalidOperationException("此 .tmod 使用较旧或未知格式，请在内部名称框填写该 Mod 的真实内部名称，或使用新版 .tmod。");
   }
  }

  private static string ReadShortString(BinaryReader reader, int maximumBytes)
  {
   int length = 0, shift = 0;
   for (int i = 0; i < 5; i++)
   {
    byte next = reader.ReadByte(); length |= (next & 0x7f) << shift;
    if ((next & 0x80) == 0)
    {
     if (length < 0 || length > maximumBytes) throw new InvalidDataException(".tmod 元数据字符串过长。");
     byte[] bytes = reader.ReadBytes(length);
     if (bytes.Length != length) throw new EndOfStreamException(".tmod 元数据不完整。");
     return new UTF8Encoding(false, true).GetString(bytes);
    }
    shift += 7;
   }
   throw new InvalidDataException(".tmod 元数据字符串长度无效。");
  }

  private static string ValidInternalName(string value)
  {
   if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || !Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$"))
    throw new InvalidDataException("Mod 内部名称无效，请填写 .tmod 元数据或原 enabled.json 中的内部名称。");
   return value;
  }

  private static string SafeRelative(string relative)
  {
   if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("Mod 相对路径不能为空。");
   relative = relative.Replace('\\', '/');
   if (relative.StartsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("Mod 路径必须位于实例目录内。");
   foreach (string part in relative.Split('/')) SafeFileName(part);
   return relative;
  }

  private static string SafeFileName(string value)
  {
   if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." || value.Length > 240 ||
       value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal) ||
       value.Any(c => c < 32 || "<>:\"/\\|?*".IndexOf(c) >= 0) ||
       Regex.IsMatch(value, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase))
    throw new InvalidDataException("不安全或不兼容 Windows 的 Mod 文件名：" + value);
   return value;
  }

  private static string Loader(ServerInstance instance)
  {
   if (instance == null) throw new ArgumentNullException(nameof(instance));
   if (string.IsNullOrWhiteSpace(instance.GameVersion)) throw new InvalidOperationException("请先设置实例的 Minecraft 版本。");
   if (instance.Kind == ServerKind.Fabric) return "fabric";
   if (instance.Kind == ServerKind.Forge) return "forge";
   throw new InvalidOperationException("在线 Modrinth 搜索仅适用于 Fabric 和 Forge 实例；其他游戏请使用本地 Mod 导入。");
  }

  private static bool CompatibleVersion(ServerInstance instance, JObject version)
  {
   return (version["game_versions"] as JArray)?.Values<string>().Contains(instance.GameVersion) == true &&
          (version["loaders"] as JArray)?.Values<string>().Contains(Loader(instance)) == true && ServerCompatible(version);
  }

  private static bool ServerCompatible(JObject value)
  {
   var environments=EnvironmentValues(value);
   if(environments.Count>0&&environments.All(x=>x=="client_only"||x=="singleplayer_only"))return false;
   return Text(value, "server_side") != "unsupported";
  }

  private static List<string> EnvironmentValues(JObject value)
  {
   var token=value?["environment"];if(token is JArray array)return array.Values<string>().Where(x=>!string.IsNullOrEmpty(x)).ToList();
   return token==null||token.Type==JTokenType.Null?new List<string>():new List<string>{token.ToString()};
  }

  private static string SupportLabel(JObject value)
  {
   var environments=EnvironmentValues(value);string support=Text(value,"server_side");
   if (environments.Contains("client_and_server")) return "客户端与服务端均需安装";
   if (environments.Contains("server_only") || environments.Contains("dedicated_server_only")) return "服务端 Mod";
   if (support == "required") return "支持服务端；请核对客户端安装要求";
   if (support == "optional") return "服务端可选";
   return "请核对项目的服务端与客户端要求";
  }

  private void EnsureStopped(ServerInstance instance)
  {
   if (processes.GetStatus(instance).Running) throw new InvalidOperationException("请先停止服务器，再更改 Mod。");
  }

  private static void CheckDependants(ServerInstance instance, InstalledMod target)
  {
   var dependants = instance.Mods.Where(m => m.Enabled && m.ProjectId != target.ProjectId &&
    (m.RequiredProjects ?? new List<string>()).Contains(target.ProjectId)).Select(m => m.Name).ToList();
   if (dependants.Count != 0) throw new InvalidOperationException("以下已启用 Mod 依赖此项目，请先禁用或移除它们：" + string.Join("、", dependants));
  }

  private static InstalledMod FindRecord(ServerInstance instance, InstalledMod mod)
  {
   if (mod == null) throw new ArgumentNullException(nameof(mod));
   return instance.Mods.FirstOrDefault(m => m.ProjectId == mod.ProjectId && m.FileName == mod.FileName)
    ?? throw new InvalidOperationException("Mod 记录已变化，请刷新列表。");
  }

  private SemaphoreSlim Gate(ServerInstance instance) => Gates.GetOrAdd(paths.Root + "|" + instance.Id, _ => new SemaphoreSlim(1, 1));
  private string NewStage() { string path = Path.Combine(paths.Temp, "mods-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
  private static void DeleteStage(string path) { if (path != null && Directory.Exists(path)) try { Directory.Delete(path, true); } catch { } }
  private static List<InstalledMod> CloneMods(List<InstalledMod> mods) => JsonConvert.DeserializeObject<List<InstalledMod>>(JsonConvert.SerializeObject(mods ?? new List<InstalledMod>()));
  private static IEnumerable<JObject> Dependencies(JObject version) => (version["dependencies"] as JArray ?? new JArray()).OfType<JObject>();
  private static string Text(JObject value, string key) => value?[key] is JValue scalar ? scalar.ToString() : "";
  private static string Escape(string value) => Uri.EscapeDataString(value);
  private static string EscapeId(string value)
  {
   if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
    throw new ArgumentException("Modrinth 项目或版本 ID 无效。");
   return Escape(value);
  }
  private static string HashFile(string file, string algorithm)
  {
   using (HashAlgorithm hash = algorithm == "SHA1" ? (HashAlgorithm)SHA1.Create() : algorithm == "SHA256" ? SHA256.Create() : SHA512.Create())
   using (var stream = File.OpenRead(file)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
  }
  private static void VerifyStoredHash(string file, string hash)
  {
   if (string.IsNullOrEmpty(hash)) return;
   string algorithm = hash.Length == 40 ? "SHA1" : hash.Length == 64 ? "SHA256" : "SHA512";
   if (!HashFile(file, algorithm).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mod 文件已被修改，请重新导入后再启用。");
  }

  private sealed class TmodInformation { public string Name; public string Version; public string LoaderVersion; }
  private sealed class PlanNode
  {
   public string ProjectId; public string VersionId; public string Name; public JObject Version; public JObject File;
   public readonly List<string> Required = new List<string>();
  }
  private sealed class ResolveContext
  {
   public readonly ServerInstance Instance;
   public readonly Dictionary<string, PlanNode> Nodes = new Dictionary<string, PlanNode>(StringComparer.Ordinal);
   public readonly HashSet<string> Visiting = new HashSet<string>(StringComparer.Ordinal);
   public readonly List<PlanNode> Ordered = new List<PlanNode>();
   public ResolveContext(ServerInstance instance) { Instance = instance; }
  }
 }
}
