using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServerWorkbench.Core;
internal static class SaveTests
{
 public static async Task Run(Func<string,Func<Task>,Task> test)
 {
  await test("Terraria 无完成行时以顺序应答和新世界哈希确认保存",async()=>{
   var p=Program.Paths("terraria-barrier");var repo=new InstanceRepository(p);var s=Program.Instance();s.Kind=ServerKind.Terraria;s.WorkingDirectory="server";repo.Save(s);var folder=Path.Combine(p.InstanceDirectory(s),"server","Worlds");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"test.wld");File.WriteAllText(file,"old");var process=new Program.FakeProcesses{Running=true};int revision=0;var calls=new List<string>();process.OnSend=command=>{calls.Add(command);if(command=="save")File.WriteAllText(file,"fresh-"+(++revision));if(command=="version")process.Emit(s,"Terraria Server v1.4.5.8");};var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){await service.SaveNowAsync(s,null,default);Program.Equal("save,version",string.Join(",",calls));Program.Equal(1,service.List(s).Count);Program.Assert(await service.SaveAndStopAsync(s,null,default),"顺序确认后正常退出");Program.Equal(2,service.List(s).Count);}
  });
  await test("Terraria 顺序应答不能掩盖未写入或保存错误",async()=>{
   var p=Program.Paths("terraria-barrier-error");var repo=new InstanceRepository(p);var s=Program.Instance();s.Kind=ServerKind.Terraria;s.WorkingDirectory="server";repo.Save(s);var folder=Path.Combine(p.InstanceDirectory(s),"server","Worlds");Directory.CreateDirectory(folder);var file=Path.Combine(folder,"test.wld");File.WriteAllText(file,"old");var process=new Program.FakeProcesses();var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){var previous=await service.CaptureStoppedAsync(s,null,default);process.Running=true;process.OnSend=command=>{if(command=="version")process.Emit(s,"Terraria Server v1.4.5.8");};await Program.ThrowsAsync(()=>service.SaveNowAsync(s,null,default));Program.Equal(previous.Path,service.List(s).Single().Path);process.OnSend=command=>{if(command=="save"){File.WriteAllText(file,"new");process.Emit(s,"System.IO.IOException: disk full");}if(command=="version")process.Emit(s,"Terraria Server v1.4.5.8");};await Program.ThrowsAsync(()=>service.SaveAndStopAsync(s,null,default));Program.Assert(process.Running,"保存错误时不能继续退出");Program.Equal(previous.Path,service.List(s).Single().Path);}
  });
  await test("Terraria 系列先确认 save 再正常 exit（退出不重复打印完成日志）",async()=>{
   foreach(var kind in new[]{ServerKind.Terraria,ServerKind.TModLoader}) {
    var p=Program.Paths("terraria-confirmed-stop-"+kind);var repo=new InstanceRepository(p);var s=Program.Instance();s.Kind=kind;s.Save=new SaveProfile{Command="save",CompletedPattern="World saved",Paths=new List<string>{"world"}};repo.Save(s);var folder=Path.Combine(p.InstanceDirectory(s),"world");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"test.wld"),"world");
    var process=new Program.FakeProcesses{Running=true};var sent=new List<string>();process.OnSend=command=>{sent.Add(command);if(command=="save")process.Emit(s,"World saved");};var transfer=new TransferService();
    using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){Program.Assert(await service.SaveAndStopAsync(s,null,default),"先收到 save 完成后，退出无需再次打印完成日志");Program.Equal("save",string.Join(",",sent));Program.Assert(!process.Running,"应正常退出");Program.Equal(1,service.List(s).Count);}
   }
  });
  await test("Terraria 仅开始保存不能确认成功，取消时不退出或轮换",async()=>{
   var p=Program.Paths("terraria-cancel-stop");var repo=new InstanceRepository(p);var s=Program.Instance();s.Kind=ServerKind.Terraria;s.Save=new SaveProfile{Command="save",CompletedPattern="World saved",Paths=new List<string>{"world"}};repo.Save(s);Directory.CreateDirectory(Path.Combine(p.InstanceDirectory(s),"world"));File.WriteAllText(Path.Combine(p.InstanceDirectory(s),"world","test.wld"),"old");var process=new Program.FakeProcesses();var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false))using(var cancel=new CancellationTokenSource()){
    var before=await service.CaptureStoppedAsync(s,null,default);process.Running=true;process.OnSend=command=>{process.Emit(s,"Saving before exit...");cancel.Cancel();};await Program.ThrowsAsync(()=>service.SaveAndStopAsync(s,null,cancel.Token));Program.Assert(process.Running,"未确认保存时不发送 exit");Program.Equal(before.Path,service.List(s).Single().Path);
   }
  });
  await test("保存完成确认、命令顺序及最近两次回档",async()=>{
   var p=Program.Paths("saves");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{Command="save",CompletedPattern="SAVED_OK",PauseCommand="pause",ResumeCommand="resume",Paths=new List<string>{"world"}};repo.Save(s);var world=Path.Combine(p.InstanceDirectory(s),"world");Directory.CreateDirectory(world);var file=Path.Combine(world,"level.dat");File.WriteAllText(file,"initial");
   var process=new Program.FakeProcesses{Running=true};var calls=new List<string>();int saves=0;process.OnSend=command=>{calls.Add(command);if(command=="save"){File.WriteAllText(file,"save-"+(++saves));process.Emit(s,"SAVED_OK");}};
   var transfer=new TransferService();var backups=new BackupService(p,transfer,process);using(var service=new SaveService(p,repo,process,transfer,backups,false)){
    for(int i=0;i<3;i++)await service.SaveNowAsync(s,null,default);
    Program.Equal("pause,save,resume,pause,save,resume,pause,save,resume",string.Join(",",calls));Program.Equal(2,service.List(s).Count);
    process.Running=false;var previous=service.List(s)[1];await service.RestoreAsync(s,previous,null,default);Program.Equal("save-2",File.ReadAllText(file));Program.Equal(1,backups.List(s).Count);
   }
  });
  await test("保存取消恢复自动保存且不轮换回档点",async()=>{
   var p=Program.Paths("cancel-save");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{Command="save",CompletedPattern="CONFIRMED",PauseCommand="pause",ResumeCommand="resume",Paths=new List<string>{"world"}};repo.Save(s);Directory.CreateDirectory(Path.Combine(p.InstanceDirectory(s),"world"));File.WriteAllText(Path.Combine(p.InstanceDirectory(s),"world","x"),"old");var process=new Program.FakeProcesses();var transfer=new TransferService();var backup=new BackupService(p,transfer,process);using(var service=new SaveService(p,repo,process,transfer,backup,false)){
    await service.CaptureStoppedAsync(s,null,default);var before=service.List(s).Single().Path;process.Running=true;var commands=new List<string>();process.OnSend=x=>commands.Add(x);using(var cts=new CancellationTokenSource(60))await Program.ThrowsAsync(()=>service.SaveNowAsync(s,null,cts.Token));Program.Equal(before,service.List(s).Single().Path);Program.Equal("resume",commands.Last());
   }
  });
  await test("紧急关服跳过保存，正常关服创建回档点",async()=>{
   var p=Program.Paths("stop-save");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{Paths=new List<string>{"world"}};repo.Save(s);Directory.CreateDirectory(Path.Combine(p.InstanceDirectory(s),"world"));File.WriteAllText(Path.Combine(p.InstanceDirectory(s),"world","x"),"persisted");var process=new Program.FakeProcesses{Running=true};var calls=new List<string>();process.OnSend=x=>calls.Add(x);var transfer=new TransferService();using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){
    await service.EmergencyStopAsync(s);Program.Assert(!process.Running,"紧急结束应停服");Program.Equal(0,calls.Count);Program.Equal(0,service.List(s).Count);process.Running=true;Program.Assert(await service.SaveAndStopAsync(s,null,default),"正常关服应完成");Program.Equal(1,service.List(s).Count);
   }
  });
  await test("未配置/空存档/运行中回档被拒绝",async()=>{
   var p=Program.Paths("save-invalid");var repo=new InstanceRepository(p);var s=Program.Instance();repo.Save(s);var process=new Program.FakeProcesses();var transfer=new TransferService();using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){
    await Program.ThrowsAsync(()=>service.CaptureStoppedAsync(s,null,default));s.Save=new SaveProfile{Paths=new List<string>{"world"}};await Program.ThrowsAsync(()=>service.CaptureStoppedAsync(s,null,default));s.Save.Paths=new List<string>{"../world"};await Program.ThrowsAsync(()=>service.CaptureStoppedAsync(s,null,default));Program.Equal(0,service.List(s).Count);
   }
  });
  await test("残缺回档点不能移走当前世界",async()=>{
   var p=Program.Paths("incomplete-save");var repo=new InstanceRepository(p);var s=Program.Instance();s.Save=new SaveProfile{Paths=new List<string>{"world"}};s.AutoSaveMinutes=0;repo.Save(s);var world=Path.Combine(p.InstanceDirectory(s),"world");Directory.CreateDirectory(world);File.WriteAllText(Path.Combine(world,"level.dat"),"keep-current");var process=new Program.FakeProcesses();var transfers=new TransferService();using(var service=new SaveService(p,repo,process,transfers,new BackupService(p,transfers,process),false)){
    var snapshot=await service.CaptureStoppedAsync(s,null,default);using(var zip=System.IO.Compression.ZipFile.Open(snapshot.Path,System.IO.Compression.ZipArchiveMode.Update))foreach(var entry in zip.Entries.Where(e=>e.FullName.StartsWith("world")).ToList())entry.Delete();await Program.ThrowsAsync(()=>service.RestoreAsync(s,snapshot,null,default));Program.Equal("keep-current",File.ReadAllText(Path.Combine(world,"level.dat")));
   }
  });
  await test("损坏或缺失世界的回档点不阻断有效轮换",async()=>{
   var p=Program.Paths("damaged-snapshots");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{Paths=new List<string>{"world"}};repo.Save(s);var world=Path.Combine(p.InstanceDirectory(s),"world");Directory.CreateDirectory(world);var file=Path.Combine(world,"state");File.WriteAllText(file,"one");var process=new Program.FakeProcesses();var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){
    var first=await service.CaptureStoppedAsync(s,null,default);File.WriteAllText(file,"two");await service.CaptureStoppedAsync(s,null,default);var folder=Path.GetDirectoryName(first.Path);File.WriteAllText(Path.Combine(folder,"corrupt.zip"),"truncated zip");
    using(var zip=System.IO.Compression.ZipFile.Open(Path.Combine(folder,"missing-world.zip"),System.IO.Compression.ZipArchiveMode.Create)){var e=zip.CreateEntry(".workbench-save.json");using(var writer=new StreamWriter(e.Open()))writer.Write(Newtonsoft.Json.JsonConvert.SerializeObject(new{SchemaVersion=1,InstanceId=s.Id,CreatedUtc=DateTime.UtcNow,Roots=new[]{"world"}}));}
    Program.Equal(2,service.List(s).Count);File.WriteAllText(file,"three");await service.CaptureStoppedAsync(s,null,default);Program.Equal(2,service.List(s).Count);Program.Assert(!File.Exists(first.Path),"只轮换最早的有效回档点");Program.Assert(File.Exists(Path.Combine(folder,"corrupt.zip")),"损坏文件应保留供诊断");
   }
  });
  await test("关服缺少保存完成日志时保留原回档点",async()=>{
   var p=Program.Paths("unconfirmed-stop");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{CompletedPattern="SAVED_OK",Paths=new List<string>{"world"}};repo.Save(s);Directory.CreateDirectory(Path.Combine(p.InstanceDirectory(s),"world"));File.WriteAllText(Path.Combine(p.InstanceDirectory(s),"world","x"),"old");var process=new ControlledProcesses();var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){
    var previous=await service.CaptureStoppedAsync(s,null,default);process.Running=true;await Program.ThrowsAsync(()=>service.SaveAndStopAsync(s,null,default));Program.Equal(previous.Path,service.List(s).Single().Path);
    process.Running=true;process.OnStop=ct=>{process.Emit(s,"SAVED_OK");process.Running=false;return Task.FromResult(true);};Program.Assert(await service.SaveAndStopAsync(s,null,default),"有完成日志可正常关服");Program.Equal(2,service.List(s).Count);
   }
  });
  await test("紧急关服取消进行中的正常关服且不生成回档",async()=>{
   var p=Program.Paths("emergency-during-stop");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{Paths=new List<string>{"world"}};repo.Save(s);Directory.CreateDirectory(Path.Combine(p.InstanceDirectory(s),"world"));File.WriteAllText(Path.Combine(p.InstanceDirectory(s),"world","x"),"old");var process=new ControlledProcesses{Running=true};var began=new TaskCompletionSource<bool>();process.OnStop=async ct=>{began.SetResult(true);await Task.Delay(30000,ct);process.Running=false;return true;};var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){
    var stopping=service.SaveAndStopAsync(s,null,default);await began.Task;await service.EmergencyStopAsync(s);await Program.ThrowsAsync(()=>stopping);Program.Assert(!process.Running,"紧急停止已结束进程");Program.Equal(0,service.List(s).Count);
   }
  });
  await test("保存请求前的完成日志不能作为本次确认",async()=>{
   var p=Program.Paths("stale-save-confirmation");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{Command="save",CompletedPattern="SAVED_OK",PauseCommand="pause",ResumeCommand="resume",Paths=new List<string>{"world"}};repo.Save(s);Directory.CreateDirectory(Path.Combine(p.InstanceDirectory(s),"world"));File.WriteAllText(Path.Combine(p.InstanceDirectory(s),"world","x"),"old");var process=new Program.FakeProcesses{Running=true};process.OnSend=command=>{if(command=="pause")process.Emit(s,"SAVED_OK");};var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false))using(var cancel=new CancellationTokenSource(80)){await Program.ThrowsAsync(()=>service.SaveNowAsync(s,null,cancel.Token));Program.Equal(0,service.List(s).Count);}
  });
  await test("复制中新建世界文件不能轮换有效回档点",async()=>{
   var p=Program.Paths("world-mutated-copy");var repo=new InstanceRepository(p);var s=Program.Instance();s.AutoSaveMinutes=0;s.Save=new SaveProfile{Paths=new List<string>{"world"}};repo.Save(s);var world=Path.Combine(p.InstanceDirectory(s),"world");Directory.CreateDirectory(world);File.WriteAllText(Path.Combine(world,"original"),"old");var process=new Program.FakeProcesses();var transfer=new TransferService();
   using(var service=new SaveService(p,repo,process,transfer,new BackupService(p,transfer,process),false)){
    var previous=await service.CaptureStoppedAsync(s,null,default);await Program.ThrowsAsync(()=>service.CaptureStoppedAsync(s,new ImmediateProgress(()=>File.WriteAllText(Path.Combine(world,"new-chunk"),"new")),default));Program.Equal(previous.Path,service.List(s).Single().Path);s.Save.Paths=new List<string>{"INSTANCE.JSON"};await Program.ThrowsAsync(()=>service.CaptureStoppedAsync(s,null,default));
   }
  });

  if(PathSafety.IsWindows)await test("准备运行时期间紧急结束取消待启动实例",async()=>{
   var p=Program.Paths("cancel-preparing-start");var repo=new InstanceRepository(p);var s=Program.Instance();s.Runtime=new RuntimeRequirement{Kind=RuntimeKind.Java,JavaMajor=21};repo.Save(s);File.WriteAllText(Path.Combine(p.InstanceDirectory(s),s.EntryPoint),"must never be executed");
   var began=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);bool runtimeCancelled=false;
   var runtime=new ControlledRuntime{Prepare=async ct=>{began.SetResult(true);await release.Task;runtimeCancelled=ct.IsCancellationRequested;return new RuntimeResolution{Executable="C:\\missing-workbench-test-java\\java.exe"};}};
   using(var process=new ProcessManager(p,runtime)){
    var preparing=process.StartAsync(s,null,default);await began.Task;Program.Assert(process.GetStatus(s).Running,"准备运行时应可被紧急结束识别");process.Kill(s);release.SetResult(true);await Program.ThrowsAsync(()=>preparing);Program.Assert(runtimeCancelled,"紧急结束必须取消传入运行时准备的令牌");Program.Assert(!process.GetStatus(s).Running,"取消完成后不再处于启动或运行状态");Program.Equal(0,process.ReadLog(s).Count);
   }
  });

  await test("外部实例中央索引、独立标记、同步配置与安全删除",()=>{
   var p=Program.Paths("external-index");var repo=new InstanceRepository(p);var parent=p.Root+"-servers";Directory.CreateDirectory(parent);File.WriteAllText(Path.Combine(parent,"keep-parent.txt"),"parent");var s=Program.Instance();s.StorageDirectory=Path.Combine(parent,"chosen-server");repo.Save(s);var target=p.InstanceDirectory(s);File.WriteAllText(Path.Combine(target,"world.txt"),"world");
   Program.Assert(File.Exists(Path.Combine(p.InstanceIndexDirectory(s),"instance.json")),"外部实例必须保留中央索引");Program.Assert(File.Exists(Path.Combine(target,InstanceRepository.StorageMarker)),"外部实例必须有身份标记");Program.Equal(target,repo.List().Single().StorageDirectory);s.Name="外部更新";repo.Save(s);Program.Assert(File.ReadAllText(Path.Combine(target,"instance.json")).Contains("外部更新"),"外部配置必须同步更新");
   File.WriteAllText(Path.Combine(target,InstanceRepository.StorageMarker),"{\"SchemaVersion\":1,\"InstanceId\":\"other\"}");Program.Throws(()=>repo.Delete(s.Id));Program.Equal("world",File.ReadAllText(Path.Combine(target,"world.txt")));InstanceRepository.WriteStorageIdentity(target,s);repo.Delete(s.Id);Program.Assert(!Directory.Exists(target),"应删除匹配标记的实例目录");Program.Assert(!Directory.Exists(p.InstanceIndexDirectory(s)),"应删除中央索引");Program.Equal("parent",File.ReadAllText(Path.Combine(parent,"keep-parent.txt")));return Task.CompletedTask;
  });
  await test("外部路径拒绝根、保留目录、已有内容与实例嵌套",()=>{
   var p=Program.Paths("external-protection");var repo=new InstanceRepository(p);foreach(var directory in new[]{"relative-folder",Path.GetPathRoot(p.Root),p.Root,Path.GetDirectoryName(p.Root),Path.Combine(p.Instances,"child"),Path.Combine(p.Temp,"child"),Path.Combine(p.Backups,"child"),Path.Combine(p.Runtimes,"child"),Path.Combine(p.Root,"SaveSnapshots","child")}){var invalid=Program.Instance();invalid.StorageDirectory=directory;Program.Throws(()=>repo.Save(invalid));}
   var occupied=p.Root+"-occupied";Directory.CreateDirectory(occupied);File.WriteAllText(Path.Combine(occupied,"keep.txt"),"user-data");var s=Program.Instance();s.StorageDirectory=occupied;Program.Throws(()=>repo.Save(s));Program.Equal("user-data",File.ReadAllText(Path.Combine(occupied,"keep.txt")));Program.Equal(0,repo.List().Count);
   var own=Program.Instance();own.StorageDirectory=p.Root+"-managed";repo.Save(own);var nested=Program.Instance();nested.StorageDirectory=Path.Combine(own.StorageDirectory,"nested");Program.Throws(()=>repo.Save(nested));var old=own.StorageDirectory;own.StorageDirectory=p.Root+"-different";Program.Throws(()=>repo.Save(own));Program.Equal(old,repo.List().Single().StorageDirectory);return Task.CompletedTask;
  });
  await test("外部完整恢复保持当前路径且同步中央索引",async()=>{
   var p=Program.Paths("external-restore");var repo=new InstanceRepository(p);var s=Program.Instance();s.StorageDirectory=p.Root+"-server";repo.Save(s);var current=s.StorageDirectory;var world=Path.Combine(current,"world.txt");File.WriteAllText(world,"before");var process=new Program.FakeProcesses();var backups=new BackupService(p,new TransferService(),process);var original=await backups.CreateAsync(s,null,default);var foreign=p.Root+"-old-location";Directory.CreateDirectory(foreign);File.WriteAllText(Path.Combine(foreign,"keep.txt"),"do-not-touch");
   using(var zip=System.IO.Compression.ZipFile.Open(original.Path,System.IO.Compression.ZipArchiveMode.Update)){var entry=zip.GetEntry("instance.json");string text;using(var reader=new StreamReader(entry.Open()))text=reader.ReadToEnd();var config=Newtonsoft.Json.Linq.JObject.Parse(text);config["StorageDirectory"]=foreign;entry.Delete();using(var writer=new StreamWriter(zip.CreateEntry("instance.json").Open()))writer.Write(config.ToString());}
   File.WriteAllText(world,"after");await backups.RestoreAsync(s,original,null,default);Program.Equal("before",File.ReadAllText(world));Program.Equal(current,repo.List().Single().StorageDirectory);Program.Equal(current,(string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(current,"instance.json")))["StorageDirectory"]);Program.Equal("do-not-touch",File.ReadAllText(Path.Combine(foreign,"keep.txt")));Program.Equal(2,backups.List(s).Count);
  });
  await test("外部世界两份回档在目标同卷事务中恢复",async()=>{
   var p=Program.Paths("external-world-restore");var repo=new InstanceRepository(p);var s=Program.Instance();s.StorageDirectory=p.Root+"-server";s.AutoSaveMinutes=0;s.Save=new SaveProfile{Paths=new List<string>{"world"}};repo.Save(s);var world=Path.Combine(s.StorageDirectory,"world");Directory.CreateDirectory(world);var file=Path.Combine(world,"state");File.WriteAllText(file,"first");File.WriteAllText(Path.Combine(s.StorageDirectory,"settings.txt"),"keep");var process=new Program.FakeProcesses();var transfers=new TransferService();var backups=new BackupService(p,transfers,process);
   using(var saves=new SaveService(p,repo,process,transfers,backups,false)){await saves.CaptureStoppedAsync(s,null,default);File.WriteAllText(file,"second");await saves.CaptureStoppedAsync(s,null,default);await saves.RestoreAsync(s,saves.List(s)[1],null,default);Program.Equal("first",File.ReadAllText(file));Program.Equal("keep",File.ReadAllText(Path.Combine(s.StorageDirectory,"settings.txt")));Program.Equal(1,backups.List(s).Count);Program.Equal(2,saves.List(s).Count);}
   Program.Assert(!Directory.GetDirectories(Path.GetDirectoryName(s.StorageDirectory),".workbench-world-restore-*").Any(),"成功恢复后应清理自己的临时事务目录");
  });

 }
 private sealed class ControlledRuntime : IRuntimeProvider {public Func<CancellationToken,Task<RuntimeResolution>> Prepare;public Task<RuntimeResolution> EnsureAsync(RuntimeRequirement requirement,string path,IProgress<WorkProgress> progress,CancellationToken ct)=>Prepare(ct);}

 private sealed class ImmediateProgress : IProgress<WorkProgress>{private readonly Action action;public ImmediateProgress(Action action){this.action=action;}public void Report(WorkProgress value){action();}}
 private sealed class ControlledProcesses : IProcessManager
 {
  public bool Running;public Func<CancellationToken,Task<bool>> OnStop;public event EventHandler<ServerLogEventArgs> LogReceived;public event EventHandler StatusChanged{add{}remove{}}
  public void Emit(ServerInstance s,string line)=>LogReceived?.Invoke(this,new ServerLogEventArgs{InstanceId=s.Id,Line=line});
  public ServerStatus GetStatus(ServerInstance s)=>new ServerStatus{Running=Running,Ready=Running};public Task StartAsync(ServerInstance s,IProgress<WorkProgress> p,CancellationToken ct){Running=true;return Task.CompletedTask;}
  public Task<bool> StopAsync(ServerInstance s,int seconds,CancellationToken ct){if(OnStop!=null)return OnStop(ct);Running=false;return Task.FromResult(true);}public void Kill(ServerInstance s){Running=false;}public void Send(ServerInstance s,string command){}public ResourceSample GetResources(ServerInstance s)=>new ResourceSample();public IReadOnlyList<string> ReadLog(ServerInstance s)=>new List<string>();public void Dispose(){}
 }

}
