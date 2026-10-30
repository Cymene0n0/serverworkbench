using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ServerWorkbench.Core
{
 /// <summary>Confirmed saves, two rotating world snapshots, and an independent emergency path.</summary>
 public sealed class SaveService : IDisposable
 {
  private readonly AppPaths paths;private readonly IInstanceRepository repository;private readonly IProcessManager processes;private readonly ITransferService transfers;private readonly IBackupService backups;
  private readonly ConcurrentDictionary<string,SemaphoreSlim> gates=new ConcurrentDictionary<string,SemaphoreSlim>();
  private readonly ConcurrentDictionary<string,DateTime> last=new ConcurrentDictionary<string,DateTime>();
  private readonly ConcurrentDictionary<string,CancellationTokenSource> emergencyOperations=new ConcurrentDictionary<string,CancellationTokenSource>();
  private readonly ConcurrentDictionary<string,CancellationTokenSource> active=new ConcurrentDictionary<string,CancellationTokenSource>();
  private readonly ConcurrentDictionary<string,byte> managed=new ConcurrentDictionary<string,byte>();
  private readonly ConcurrentDictionary<string,string> errors=new ConcurrentDictionary<string,string>();
  private readonly ConcurrentDictionary<string,ServerInstance> known=new ConcurrentDictionary<string,ServerInstance>();
  private readonly CancellationTokenSource lifetime=new CancellationTokenSource();private readonly Timer timer;private int ticking;
  public event EventHandler Changed;
  public SaveService(AppPaths paths,IInstanceRepository repository,IProcessManager processes,ITransferService transfers,IBackupService backups,bool schedule=true)
  {this.paths=paths;this.repository=repository;this.processes=processes;this.transfers=transfers;this.backups=backups;processes.LogReceived+=OnLog;if(schedule)timer=new Timer(Tick,null,30000,30000);}
  public string LastError(ServerInstance s)=>errors.TryGetValue(s.Id,out var error)?error:"";
  // These preset command handlers run save synchronously. The following read-only version
  // response is an ordered barrier; a changed .wld hash and no save errors are required too.
  private static bool TerrariaBarrier(ServerInstance s,SaveProfile profile)=>(s.Kind==ServerKind.Terraria||s.Kind==ServerKind.TModLoader)&&profile.Command=="save"&&profile.CompletedPattern==@"World saved\.?|World successfully saved";
  private static bool SaveError(string line)=>Regex.IsMatch(line,@"Exception|failed to save|save failed|error saving|保存失败|无法保存|not enough (?:space|storage)|disk full",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100));
  private Dictionary<string,string> WorldFingerprints(ServerInstance s,SaveProfile profile)
  {
   var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);foreach(var relative in ValidRoots(profile)){var path=PathSafety.ResolveUnder(paths.InstanceDirectory(s),relative);foreach(var file in Directory.Exists(path)?WorldFiles(path).Where(file=>Path.GetExtension(file).Equals(".wld",StringComparison.OrdinalIgnoreCase)).ToArray():File.Exists(path)&&Path.GetExtension(path).Equals(".wld",StringComparison.OrdinalIgnoreCase)?new[]{path}:new string[0]){PathSafety.RejectReparse(file);files[file]=PathSafety.HashFile(file,"SHA256");}}return files;
  }
  private void RequireFreshWorld(ServerInstance s,SaveProfile profile,Dictionary<string,string> before)
  {var after=WorldFingerprints(s,profile);if(after.Count==0||!after.Any(pair=>!before.TryGetValue(pair.Key,out var hash)||hash!=pair.Value))throw new IOException("游戏已应答，但没有检测到新的有效世界写入；未生成回档点，请检查存档和磁盘。");}
  public SaveProfile Profile(ServerInstance s)
  {
   if(s.Save!=null)return s.Save;
   if(s.Kind==ServerKind.Vanilla||s.Kind==ServerKind.Fabric||s.Kind==ServerKind.Forge) {
    string world="world";var properties=Path.Combine(paths.InstanceDirectory(s),"server.properties");if(File.Exists(properties))foreach(var line in File.ReadLines(properties))if(line.StartsWith("level-name="))world=line.Substring(11).Trim();
    return new SaveProfile {Command="save-all flush",CompletedPattern=@"Saved the game|Saved the world",PauseCommand="save-off",ResumeCommand="save-on",Paths=new List<string>{world}};
   }
   if(s.Kind==ServerKind.Terraria||s.Kind==ServerKind.TModLoader)return new SaveProfile {Command="save",CompletedPattern=@"World saved\.?|World successfully saved",EmergencyCommand="exit-nosave",Paths=new List<string>{s.WorkingDirectory.TrimEnd('/','\\')+"/Worlds"}};
   return new SaveProfile();
  }
  private static List<string> ValidRoots(SaveProfile profile)
  {
   if(profile.Paths==null||profile.Paths.Count==0)throw new InvalidOperationException("尚未配置存档路径。请在启动配置中设置保存命令、完成日志和实例内存档路径。");
   var result=profile.Paths.Select(x=>PathSafety.EnsureRelative(x).Replace('\\','/').Trim('/')).ToList();
   if(result.Any(x=>x=="."||x.StartsWith("./",StringComparison.Ordinal)||x.Equals("instance.json",StringComparison.OrdinalIgnoreCase)||x.Equals(".workbench-save.json",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("回档路径应是专门的世界目录或存档文件，不能是整个实例或管理器元数据。");
   for(int i=0;i<result.Count;i++)for(int j=i+1;j<result.Count;j++)if(result[i].Equals(result[j],StringComparison.OrdinalIgnoreCase)||result[i].StartsWith(result[j]+"/",StringComparison.OrdinalIgnoreCase)||result[j].StartsWith(result[i]+"/",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("存档路径不能重复或包含彼此。");
   return result;
  }
  public async Task<SaveSnapshotInfo> SaveNowAsync(ServerInstance s,IProgress<WorkProgress> progress,CancellationToken ct,string reason="手动保存")
  {
   var semaphore=gates.GetOrAdd(s.Id,_=>new SemaphoreSlim(1,1));await semaphore.WaitAsync(ct).ConfigureAwait(false);
   using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token))try {
    active[s.Id]=linked;var token=linked.Token;var profile=Profile(s);ValidRoots(profile);
    if(string.IsNullOrWhiteSpace(profile.Command)||string.IsNullOrWhiteSpace(profile.CompletedPattern))throw new InvalidOperationException("未配置可确认完成的保存命令和日志，不能创建有效回档点。");
    if(!processes.GetStatus(s).Running)throw new InvalidOperationException("服务端未运行。停服数据请使用“创建存档回档点”或完整备份。");
    if(!string.IsNullOrEmpty(s.ReadyPattern)&&!processes.GetStatus(s).Ready)throw new InvalidOperationException("服务端尚未就绪，请等待世界加载完成。");
    managed[s.Id]=0;var matcher=new Regex(profile.CompletedPattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(150));var confirmed=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);bool requested=false,probeRequested=false,failed=false;bool barrier=TerrariaBarrier(s,profile);var before=barrier?WorldFingerprints(s,profile):null;
    EventHandler<ServerLogEventArgs> handler=(sender,e)=>{if(e.InstanceId==s.Id&&requested)try{if(barrier&&SaveError(e.Line))failed=true;if(barrier?probeRequested&&Regex.IsMatch(e.Line,@"Terraria Server v\d+\.",RegexOptions.None,TimeSpan.FromMilliseconds(100)):matcher.IsMatch(e.Line))confirmed.TrySetResult(true);}catch(RegexMatchTimeoutException){}};
    processes.LogReceived+=handler;bool paused=false;
    try {
     if(!string.IsNullOrWhiteSpace(profile.PauseCommand)){processes.Send(s,profile.PauseCommand);paused=true;}
     progress?.Report(new WorkProgress("请求游戏保存，等待服务端完成确认…"));requested=true;processes.Send(s,profile.Command);if(barrier){probeRequested=true;processes.Send(s,"version");}
     var timeout=Task.Delay(TimeSpan.FromSeconds(90),token);if(await Task.WhenAny(confirmed.Task,timeout).ConfigureAwait(false)!=confirmed.Task){token.ThrowIfCancellationRequested();throw new TimeoutException("90 秒内没有收到保存完成信号；未生成回档点，已有两份回档点保留。");}
     token.ThrowIfCancellationRequested();if(failed)throw new IOException("游戏报告保存错误，未生成回档点。");if(barrier)RequireFreshWorld(s,profile,before);if(!processes.GetStatus(s).Running)throw new InvalidOperationException("保存期间服务端已退出，本次不标记为已确认保存。");
     return await CaptureAsync(s,profile,reason,progress,token).ConfigureAwait(false);
    }finally {processes.LogReceived-=handler;if(paused&&(!emergencyOperations.TryGetValue(s.Id,out var interrupted)||interrupted!=linked)&&processes.GetStatus(s).Running)try{processes.Send(s,profile.ResumeCommand);}catch(Exception e){errors[s.Id]="请检查自动保存已恢复："+e.Message;} }
   }catch(Exception e){errors[s.Id]=e.Message;throw;}finally {active.TryRemove(s.Id,out var cancelled);emergencyOperations.TryRemove(s.Id,out var emergencyOperation);managed.TryRemove(s.Id,out var ignored);semaphore.Release();Changed?.Invoke(this,EventArgs.Empty);}
  }
  public async Task<bool> SaveAndStopAsync(ServerInstance s,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   var semaphore=gates.GetOrAdd(s.Id,_=>new SemaphoreSlim(1,1));await semaphore.WaitAsync(ct).ConfigureAwait(false);
   using(var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token))try {
    active[s.Id]=linked;var token=linked.Token;if(!processes.GetStatus(s).Running)return true;
    managed[s.Id]=0;var profile=Profile(s);bool confirmed=false,probeRequested=false,failed=false;bool barrier=TerrariaBarrier(s,profile);var before=barrier?WorldFingerprints(s,profile):null;var saved=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    string pattern=profile.CompletedPattern;
    if(s.Kind==ServerKind.Vanilla||s.Kind==ServerKind.Fabric||s.Kind==ServerKind.Forge)pattern="(?:"+pattern+")|All dimensions are saved|All chunks are saved";
    var matcher=string.IsNullOrWhiteSpace(pattern)?null:new Regex(pattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(150));
    EventHandler<ServerLogEventArgs> handler=(sender,e)=>{if(e.InstanceId==s.Id&&matcher!=null)try{if(barrier&&SaveError(e.Line))failed=true;if(barrier?probeRequested&&Regex.IsMatch(e.Line,@"Terraria Server v\d+\.",RegexOptions.None,TimeSpan.FromMilliseconds(100)):matcher.IsMatch(e.Line)){confirmed=true;saved.TrySetResult(true);}}catch(RegexMatchTimeoutException){}};
    processes.LogReceived+=handler;
    try {
     // Terraria's normal exit prints "Saving before exit..." but no completion line.
     // Confirm an explicit save first; only a successful normal process exit permits capture.
     if((s.Kind==ServerKind.Terraria||s.Kind==ServerKind.TModLoader)&&matcher!=null&&!string.IsNullOrWhiteSpace(profile.Command)) {
      progress?.Report(new WorkProgress("先请求世界保存，确认完成后再正常退出…"));processes.Send(s,profile.Command);if(barrier){probeRequested=true;processes.Send(s,"version");}
      if(await Task.WhenAny(saved.Task,Task.Delay(TimeSpan.FromSeconds(90),token)).ConfigureAwait(false)!=saved.Task){token.ThrowIfCancellationRequested();throw new TimeoutException("没有收到世界保存完成日志；未发送正常退出命令，已有回档点保留。");}
      token.ThrowIfCancellationRequested();if(failed)throw new IOException("游戏报告保存错误，未发送正常退出命令。");if(barrier)RequireFreshWorld(s,profile,before);
     }
     progress?.Report(new WorkProgress("发送正常保存关服命令，等待进程退出及保存确认…"));
     bool stopped=await processes.StopAsync(s,30,token).ConfigureAwait(false);if(!stopped)return false;
     token.ThrowIfCancellationRequested();if(failed)throw new IOException("游戏报告保存错误，未生成回档点。");
     if(profile.Paths?.Count>0){if(matcher!=null&&!confirmed)throw new InvalidOperationException("服务端已退出，但未收到本次关服的保存完成日志；没有生成回档点，已有回档点保留。可核对游戏日志后手动创建停服存档。");await CaptureAsync(s,profile,matcher!=null?"保存并关服":"正常退出后存档（保存语义未验证）",progress,token).ConfigureAwait(false);}
     return true;
    }finally{processes.LogReceived-=handler;}
   }catch(Exception e){errors[s.Id]=e.Message;throw;}
   finally{active.TryRemove(s.Id,out var cancelled);emergencyOperations.TryRemove(s.Id,out var emergencyOperation);managed.TryRemove(s.Id,out var ignored);semaphore.Release();Changed?.Invoke(this,EventArgs.Empty);}
  }
  public async Task EmergencyStopAsync(ServerInstance s)
  {
   // Cancel any save/normal-stop copy before killing, without waiting on its gate.
   if(active.TryGetValue(s.Id,out var operation))try{emergencyOperations[s.Id]=operation;operation.Cancel();}catch(ObjectDisposedException){}
   // This path deliberately does not wait on the save/backup gate and never sends save/stop.
   var emergency=Profile(s).EmergencyCommand;
   if(!string.IsNullOrEmpty(emergency)&&processes.GetStatus(s).Running) {
    try{processes.Send(s,emergency);}catch{}var end=DateTime.UtcNow.AddSeconds(2);while(processes.GetStatus(s).Running&&DateTime.UtcNow<end)await Task.Delay(50).ConfigureAwait(false);
   }
   if(processes.GetStatus(s).Running)processes.Kill(s);
  }
  public async Task<SaveSnapshotInfo> CaptureStoppedAsync(ServerInstance s,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   var gate=gates.GetOrAdd(s.Id,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(ct).ConfigureAwait(false);try{if(processes.GetStatus(s).Running)throw new InvalidOperationException("此操作只用于停服存档；运行中请点击保存并生成回档点。");return await CaptureAsync(s,Profile(s),"停服存档",progress,ct).ConfigureAwait(false);}finally{gate.Release();}
  }
  private async Task<SaveSnapshotInfo> CaptureAsync(ServerInstance s,SaveProfile profile,string reason,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   var roots=ValidRoots(profile);var root=paths.InstanceDirectory(s);var folder=Path.Combine(paths.Root,"SaveSnapshots",s.Id);Directory.CreateDirectory(folder);var destination=Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff")+".zip");var partial=destination+".part";
   try {
    var created=DateTime.UtcNow;
    await Task.Run(()=> {
     var files=new List<string>();foreach(var relative in roots){var path=PathSafety.ResolveUnder(root,relative);if(Directory.Exists(path))files.AddRange(WorldFiles(path));else if(File.Exists(path)){PathSafety.RejectReparse(path);files.Add(path);}else throw new DirectoryNotFoundException("配置的存档路径不存在："+relative);}
     if(files.Count==0)throw new InvalidOperationException("配置的存档路径中没有可备份文件；未创建空回档点。");
     if(files.Count>100000)throw new InvalidDataException("存档文件数量超过限制。");var before=files.ToDictionary(f=>f,f=>new FileInfo(f).Length+"/"+File.GetLastWriteTimeUtc(f).Ticks);
     using(var zip=ZipFile.Open(partial,ZipArchiveMode.Create)) {
      var metadata=zip.CreateEntry(".workbench-save.json");using(var writer=new StreamWriter(metadata.Open(),new UTF8Encoding(false)))writer.Write(JsonConvert.SerializeObject(new SnapshotManifest{InstanceId=s.Id,CreatedUtc=created,Reason=reason,Roots=roots}));
      foreach(var relative in roots)if(Directory.Exists(PathSafety.ResolveUnder(root,relative)))zip.CreateEntry(relative.TrimEnd('/')+"/");
      int i=0;foreach(var file in files){ct.ThrowIfCancellationRequested();var relative=file.Substring(root.Length+1).Replace('\\','/');var entry=zip.CreateEntry(relative,CompressionLevel.Fastest);using(var source=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var output=entry.Open()){var buffer=new byte[81920];int n;while((n=source.Read(buffer,0,buffer.Length))>0){ct.ThrowIfCancellationRequested();output.Write(buffer,0,n);}}progress?.Report(new WorkProgress("生成存档回档点："+relative,++i*100.0/files.Count));}
     }
     var after=new List<string>();foreach(var relative in roots){var path=PathSafety.ResolveUnder(root,relative);if(Directory.Exists(path))after.AddRange(WorldFiles(path));else if(File.Exists(path))after.Add(path);}
     if(after.Count!=before.Count||after.Any(file=>!before.ContainsKey(file)))throw new IOException("存档文件列表在复制期间发生变化，已丢弃本次回档点，请重试。");
     foreach(var pair in before)if(!File.Exists(pair.Key)||pair.Value!=new FileInfo(pair.Key).Length+"/"+File.GetLastWriteTimeUtc(pair.Key).Ticks)throw new IOException("存档在复制期间发生变化，已丢弃本次回档点，请重试。");
    },ct).ConfigureAwait(false);
    ct.ThrowIfCancellationRequested();File.Move(partial,destination);last[s.Id]=DateTime.UtcNow;errors.TryRemove(s.Id,out var ignored);
    foreach(var old in List(s).Skip(2))File.Delete(old.Path);Changed?.Invoke(this,EventArgs.Empty);return new SaveSnapshotInfo {Path=destination,CreatedUtc=created,Reason=reason,Size=new FileInfo(destination).Length};
   }finally{if(File.Exists(partial))File.Delete(partial);}
  }
  private static IEnumerable<string> WorldFiles(string folder)
  {
   PathSafety.RejectReparse(folder);foreach(var file in Directory.GetFiles(folder)){PathSafety.RejectReparse(file);if(!Path.GetFileName(file).Equals("session.lock",StringComparison.OrdinalIgnoreCase))yield return file;}foreach(var directory in Directory.GetDirectories(folder))foreach(var file in WorldFiles(directory))yield return file;
  }
  public IReadOnlyList<SaveSnapshotInfo> List(ServerInstance s)
  {
   var folder=Path.Combine(paths.Root,"SaveSnapshots",s.Id);if(!Directory.Exists(folder))return new List<SaveSnapshotInfo>();var result=new List<SaveSnapshotInfo>();
   foreach(var file in Directory.GetFiles(folder,"*.zip"))try {
    using(var zip=ZipFile.OpenRead(file)) {
     var entry=zip.GetEntry(".workbench-save.json");if(entry==null)continue;SnapshotManifest manifest;
     using(var reader=new StreamReader(entry.Open()))manifest=JsonConvert.DeserializeObject<SnapshotManifest>(reader.ReadToEnd());
     if(manifest==null||manifest.SchemaVersion!=1||manifest.InstanceId!=s.Id)continue;
     var roots=ValidRoots(new SaveProfile{Paths=manifest.Roots});
     if(roots.Any(root=>!zip.Entries.Any(content=>content.FullName.Equals(root,StringComparison.OrdinalIgnoreCase)||content.FullName.StartsWith(root+"/",StringComparison.OrdinalIgnoreCase))))continue;
     result.Add(new SaveSnapshotInfo{Path=file,CreatedUtc=manifest.CreatedUtc,Reason=manifest.Reason,Size=new FileInfo(file).Length});
    }
   }catch(InvalidDataException){}catch(IOException){}catch(UnauthorizedAccessException){}catch(JsonException){}catch(ArgumentException){}catch(InvalidOperationException){}
   return result.OrderByDescending(x=>x.CreatedUtc).ToList();
  }
  public async Task RestoreAsync(ServerInstance s,SaveSnapshotInfo snapshot,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   var gate=gates.GetOrAdd(s.Id,_=>new SemaphoreSlim(1,1));await gate.WaitAsync(ct).ConfigureAwait(false);var temporaryRoot=string.IsNullOrEmpty(s.StorageDirectory)?paths.Temp:Path.GetDirectoryName(paths.InstanceDirectory(s));var temp=Path.Combine(temporaryRoot,".workbench-world-restore-"+Guid.NewGuid().ToString("N"));bool preserve=false,ownedTemp=false;
   try {
    if(processes.GetStatus(s).Running)throw new InvalidOperationException("回档前必须停止服务端；需要放弃当前进度时，请先使用紧急关服。");InstanceRepository.RequireManagedStorage(paths,s);if(Directory.Exists(temp)||File.Exists(temp))throw new IOException("回档临时目录已存在，请重试。");Directory.CreateDirectory(temp);ownedTemp=true;
    var own=List(s).FirstOrDefault(x=>Path.GetFullPath(x.Path)==Path.GetFullPath(snapshot.Path));if(own==null)throw new InvalidDataException("请选择当前实例的有效回档点。");
    var stage=Path.Combine(temp,"files");await Task.Run(()=>transfers.ExtractZip(own.Path,stage,ct),ct).ConfigureAwait(false);var manifest=JsonConvert.DeserializeObject<SnapshotManifest>(File.ReadAllText(Path.Combine(stage,".workbench-save.json")));if(manifest.InstanceId!=s.Id)throw new InvalidDataException("存档所属实例不匹配。");
    var roots=ValidRoots(new SaveProfile{Paths=manifest.Roots});foreach(var relative in roots){var content=PathSafety.ResolveUnder(stage,relative);if(!File.Exists(content)&&!Directory.Exists(content))throw new InvalidDataException("回档点缺少声明的存档内容："+relative+"；当前世界没有改动。");}await backups.CreateAsync(s,progress,ct).ConfigureAwait(false);var moved=new List<string>();var root=paths.InstanceDirectory(s);var previous=Path.Combine(temp,"previous");
    try {
     foreach(var relative in roots){ct.ThrowIfCancellationRequested();if(processes.GetStatus(s).Running)throw new InvalidOperationException("实例重新启动，已取消回档。");var target=PathSafety.ResolveUnder(root,relative);var old=PathSafety.ResolveUnder(previous,relative);Directory.CreateDirectory(Path.GetDirectoryName(old));if(Directory.Exists(target))Directory.Move(target,old);else if(File.Exists(target))File.Move(target,old);moved.Add(relative);var source=PathSafety.ResolveUnder(stage,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));if(Directory.Exists(source))Directory.Move(source,target);else if(File.Exists(source))File.Move(source,target);}
    }catch(Exception error){try{foreach(var relative in moved.AsEnumerable().Reverse()){var target=PathSafety.ResolveUnder(root,relative);if(Directory.Exists(target))Directory.Delete(target,true);else if(File.Exists(target))File.Delete(target);var old=PathSafety.ResolveUnder(previous,relative);if(Directory.Exists(old))Directory.Move(old,target);else if(File.Exists(old))File.Move(old,target);}}catch(Exception rollback){preserve=true;throw new IOException("回档回滚失败，请保留恢复资料："+temp,new AggregateException(error,rollback));}throw;}
   }finally{if(ownedTemp&&!preserve&&Directory.Exists(temp))Directory.Delete(temp,true);gate.Release();}
  }
  private async void Tick(object state)
  {
   if(Interlocked.Exchange(ref ticking,1)!=0)return;
   try {foreach(var s in repository.List()){known[s.Id]=s;var status=processes.GetStatus(s);if(s.AutoSaveMinutes>0&&status.Running&&(status.Ready||string.IsNullOrEmpty(s.ReadyPattern))&&!managed.ContainsKey(s.Id)&&Profile(s).Paths.Count>0){if(!last.TryGetValue(s.Id,out var time)){last[s.Id]=DateTime.UtcNow;continue;}if((DateTime.UtcNow-time).TotalMinutes>=s.AutoSaveMinutes)try{await SaveNowAsync(s,null,lifetime.Token,"定时保存").ConfigureAwait(false);}catch(Exception e){errors[s.Id]=e.Message;last[s.Id]=DateTime.UtcNow;}}}}
   catch(OperationCanceledException){}catch(Exception){}finally{Interlocked.Exchange(ref ticking,0);}
  }
  private void OnLog(object sender,ServerLogEventArgs e)
  {
   if(managed.ContainsKey(e.InstanceId)||lifetime.IsCancellationRequested)return;
   try{var s=known.GetOrAdd(e.InstanceId,id=>repository.List().FirstOrDefault(x=>x.Id==id));if(s==null||s.AutoSaveMinutes<=0)return;var profile=Profile(s);if(string.IsNullOrWhiteSpace(profile.CompletedPattern))return;if(!Regex.IsMatch(e.Line,profile.CompletedPattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100)))return;if(last.TryGetValue(s.Id,out var previous)&&(DateTime.UtcNow-previous).TotalSeconds<20)return;last[s.Id]=DateTime.UtcNow;ObserveExternal(s);}catch(Exception){}
  }
  private async void ObserveExternal(ServerInstance s){try{await SaveNowAsync(s,null,lifetime.Token,"检测到游戏保存").ConfigureAwait(false);}catch(Exception e){errors[s.Id]=e.Message;}}
  public void Dispose(){timer?.Dispose();lifetime.Cancel();processes.LogReceived-=OnLog;}
  private sealed class SnapshotManifest {public int SchemaVersion=1;public string InstanceId;public DateTime CreatedUtc;public string Reason;public List<string> Roots;}
 }
}
