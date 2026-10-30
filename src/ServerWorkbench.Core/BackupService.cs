using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ServerWorkbench.Core
{
 public sealed class BackupService : IBackupService
 {
  private readonly AppPaths paths;private readonly ITransferService transfers;private readonly IProcessManager processes;
  public BackupService(AppPaths paths,ITransferService transfers,IProcessManager processes){this.paths=paths;this.transfers=transfers;this.processes=processes;}
  private void Stopped(ServerInstance instance){if(processes.GetStatus(instance).Running)throw new InvalidOperationException("请先正常停服，再备份、恢复或修改文件。");}
  public async Task<BackupInfo> CreateAsync(ServerInstance instance,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   Stopped(instance);InstanceRepository.Validate(instance);InstanceRepository.RequireManagedStorage(paths,instance);var folder=Path.Combine(paths.Backups,instance.Id);Directory.CreateDirectory(folder);var file=Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".zip");
   var partial=file+".part";
   try {await Task.Run(()=> {
    var root=paths.InstanceDirectory(instance);var files=SafeFiles(root).ToList();
    using(var zip=ZipFile.Open(partial,ZipArchiveMode.Create))for(int i=0;i<files.Count;i++) {
     ct.ThrowIfCancellationRequested();Stopped(instance);var source=files[i];var relative=source.Substring(root.Length+1).Replace('\\','/');
     var entry=zip.CreateEntry(relative,CompressionLevel.Fastest);using(var input=File.OpenRead(source))using(var output=entry.Open()){var b=new byte[81920];int n;while((n=input.Read(b,0,b.Length))>0){ct.ThrowIfCancellationRequested();output.Write(b,0,n);}}
     progress?.Report(new WorkProgress("备份 "+relative,(i+1)*100.0/Math.Max(files.Count,1)));
    }
   },ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();File.Move(partial,file);return new BackupInfo {Path=file,CreatedUtc=File.GetLastWriteTimeUtc(file),Size=new FileInfo(file).Length};}
   finally{if(File.Exists(partial))File.Delete(partial);}
  }
  private static IEnumerable<string> SafeFiles(string root)
  {
   PathSafety.RejectReparse(root);foreach(var file in Directory.GetFiles(root)){PathSafety.RejectReparse(file);yield return file;}
   foreach(var dir in Directory.GetDirectories(root)){foreach(var f in SafeFiles(dir))yield return f;}
  }
  public IReadOnlyList<BackupInfo> List(ServerInstance instance)
  {
   InstanceRepository.Validate(instance);var folder=Path.Combine(paths.Backups,instance.Id);if(!Directory.Exists(folder))return new List<BackupInfo>();
   return Directory.GetFiles(folder,"*.zip").Select(f=>new BackupInfo {Path=f,CreatedUtc=File.GetLastWriteTimeUtc(f),Size=new FileInfo(f).Length}).OrderByDescending(x=>x.CreatedUtc).ToList();
  }
  public async Task RestoreAsync(ServerInstance instance,BackupInfo backup,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   Stopped(instance);var expected=Path.GetFullPath(Path.Combine(paths.Backups,instance.Id)).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!Path.GetFullPath(backup.Path).StartsWith(expected,StringComparison.OrdinalIgnoreCase)||!File.Exists(backup.Path))throw new InvalidDataException("只能恢复当前实例的备份。");
   InstanceRepository.RequireManagedStorage(paths,instance);var target=paths.InstanceDirectory(instance);
   var temporaryRoot=string.IsNullOrEmpty(instance.StorageDirectory)?paths.Temp:Path.GetDirectoryName(target);
   var parent=Path.Combine(temporaryRoot,".workbench-restore-"+Guid.NewGuid().ToString("N"));if(Directory.Exists(parent)||File.Exists(parent))throw new IOException("恢复临时目录已存在，请重试。");
   var stage=Path.Combine(parent,"restored");var old=Path.Combine(parent,"previous");Directory.CreateDirectory(parent);bool committed=false;
   try {
    await Task.Run(()=>transfers.ExtractZip(backup.Path,stage,ct),ct).ConfigureAwait(false);
    var config=JsonConvert.DeserializeObject<ServerInstance>(File.ReadAllText(Path.Combine(stage,"instance.json")));if(config==null)throw new InvalidDataException("备份缺少实例配置。");config.StorageDirectory=instance.StorageDirectory;InstanceRepository.Validate(config);if(config.Id!=instance.Id)throw new InvalidDataException("备份中的实例 ID 不匹配。");
    InstanceRepository.WriteStorageIdentity(stage,config);
    await CreateAsync(instance,progress,ct).ConfigureAwait(false);ct.ThrowIfCancellationRequested();Stopped(instance);InstanceRepository.RequireManagedStorage(paths,instance);
    Directory.Move(target,old);try{Directory.Move(stage,target);new InstanceRepository(paths).Save(config);committed=true;}
    catch(Exception error){try{if(Directory.Exists(target))Directory.Move(target,stage);Directory.Move(old,target);}catch(Exception rollback){throw new IOException("恢复未完成，原实例保留在 "+old,new AggregateException(error,rollback));}throw;}
   }finally{if(Directory.Exists(parent)&&(committed||!Directory.Exists(old)))Directory.Delete(parent,true);}

  }
 }
 public static class AppBootstrap
 {
  public static AppServices Create(string dataRoot,ISecretProtector secretProtector=null)
  {
   var paths=new AppPaths(dataRoot);foreach(var dir in new[]{paths.Root,paths.Instances,paths.Runtimes,paths.Temp,paths.Backups})Directory.CreateDirectory(dir);
   var repository=new InstanceRepository(paths);var transfers=new TransferService();var runtimes=new RuntimeProvider(paths,transfers);var processes=new ProcessManager(paths,runtimes);var backups=new BackupService(paths,transfers,processes);
   return new AppServices {Paths=paths,Repository=repository,Transfers=transfers,Runtimes=runtimes,Processes=processes,Backups=backups,Importer=new ImportService(paths,repository,transfers,runtimes),Presets=new PresetService(paths,repository,transfers,runtimes),Mods=new ModProvider(paths,repository,transfers,processes,backups),Saves=new SaveService(paths,repository,processes,transfers,backups),Tunnels=secretProtector==null?null:new FrpTunnelService(paths,transfers,repository,processes,secretProtector)};
  }
 }
}
