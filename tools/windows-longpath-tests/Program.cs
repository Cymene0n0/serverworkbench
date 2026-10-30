using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using ServerWorkbench.Core;

internal static class Program
{
 private static string root;
 private static int passed,failed;
 private static int Main(string[] args)
 {
  root=Path.Combine(Path.GetFullPath(args.Length>0?args[0]:Path.GetTempPath()),"longpath-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
  Directory.CreateDirectory(root);
  Console.WriteLine("Framework 4.8 managed storage long-path regression; no game/runtime compatibility claim.");
  Console.WriteLine("OS="+Environment.OSVersion+"; CLR="+Environment.Version+"; Target="+AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName);
  using(var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem"))Console.WriteLine("LongPathsEnabled="+key?.GetValue("LongPathsEnabled","missing")+" (read only; no policy changed)");
  foreach(var name in new[]{"Switch.System.IO.UseLegacyPathHandling","Switch.System.IO.BlockLongPaths"}){bool value;Console.WriteLine(name+" known="+AppContext.TryGetSwitch(name,out value)+" value="+value);}
  Console.WriteLine("Artifacts="+root);
  Test("Atomic JSON create and replace at MAX_PATH boundary",AtomicBoundary);
  Test("Atomic JSON beyond 330 characters with no temporary residue",AtomicExtended);
  Test("Repository, deep library full backup and rotating world restore",()=>RepositoryBackupRestore().GetAwaiter().GetResult());
  Console.WriteLine("RESULT passed="+passed+" failed="+failed);return failed==0?0:1;
 }
 private static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e.GetType().FullName+" "+e.Message);}}
 private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
 private static string Lengthen(string start,int minimum)
 {
  string current=start;while(current.Length<minimum)current=Path.Combine(current,"中文 long-path-segment-0123456789");return current;
 }
 private static void AtomicBoundary()
 {
  string parent=Path.Combine(root,"boundary");if(parent.Length<239)parent=Path.Combine(parent,new string('b',239-parent.Length-1));
  string path=Path.Combine(parent,"instance.json");Require(path.Length>=250,"Boundary path too short to reproduce original temporary suffix bug.");
  AtomicJson.Write(path,new{Name="first"});AtomicJson.Write(path,new{Name="second"});Require(File.ReadAllText(path).Contains("second"),"Atomic replace did not commit.");Require(!Directory.GetFiles(parent,"*.tmp").Any(),"Temporary file was left behind.");Console.WriteLine("boundary config length="+path.Length+"; former temporary length="+(path.Length+37));
 }
 private static void AtomicExtended()
 {
  string parent=Lengthen(Path.Combine(root,"extended"),325);string path=Path.Combine(parent,"instance.json");
  AtomicJson.Write(path,new{Name="first"});AtomicJson.Write(path,new{Name="second"});Require(File.ReadAllText(path).Contains("second"),"Long JSON replace did not commit.");Require(!Directory.GetFiles(parent,"*.tmp").Any(),"Long-path temporary file was left behind.");Console.WriteLine("extended config length="+path.Length);
 }
 private static async Task RepositoryBackupRestore()
 {
  var paths=new AppPaths(Lengthen(Path.Combine(root,"managed Data 中文 空格"),235));var repository=new InstanceRepository(paths);var server=new ServerInstance{Name="长路径测试世界",EntryPoint="server.exe",AutoSaveMinutes=0,Save=new SaveProfile{Paths=new List<string>{"world"}}};repository.Save(server);server.Name="更新后的长路径测试世界";repository.Save(server);Require(repository.List().Single().Name==server.Name,"Long-path instance repository read failed.");
  string instance=paths.InstanceDirectory(server),world=Path.Combine(instance,"world"),state=Path.Combine(world,"state.txt"),settings=Path.Combine(instance,"settings.txt");Directory.CreateDirectory(world);File.WriteAllText(state,"save-0");File.WriteAllText(settings,"keep-config");
  string library=Path.Combine(Lengthen(Path.Combine(instance,"libraries","io","netty","netty-transport-native-epoll","4.1.97.Final"),355),"netty-transport-native-epoll-4.1.97.Final-linux-aarch_64.jar");Directory.CreateDirectory(Path.GetDirectoryName(library));File.WriteAllText(library,"synthetic library file; not executable");
  using(var processes=new StoppedProcesses()) {
   var transfers=new TransferService();var backups=new BackupService(paths,transfers,processes);var original=await backups.CreateAsync(server,null,CancellationToken.None);
   using(var saves=new SaveService(paths,repository,processes,transfers,backups,false)){
    for(int i=0;i<3;i++){File.WriteAllText(state,"save-"+i);await saves.CaptureStoppedAsync(server,null,CancellationToken.None);}Require(saves.List(server).Count==2,"World snapshots did not retain exactly two valid entries.");await saves.RestoreAsync(server,saves.List(server)[1],null,CancellationToken.None);Require(File.ReadAllText(state)=="save-1","Earlier world snapshot was not restored.");Require(File.ReadAllText(settings)=="keep-config","World restore changed instance settings.");Require(backups.List(server).Count>=2,"World restore omitted its full pre-backup.");
   }
   File.WriteAllText(library,"newer-library");await backups.RestoreAsync(server,original,null,CancellationToken.None);Require(File.ReadAllText(state)=="save-0","Full backup world was not restored.");Require(File.ReadAllText(library)=="synthetic library file; not executable","Deep library file was not restored.");Require(repository.List().Single().Name==server.Name,"Restored instance metadata is invalid.");
  }
  Console.WriteLine("instance config length="+Path.Combine(instance,"instance.json").Length+"; library length="+library.Length);
 }
 private sealed class StoppedProcesses : IProcessManager
 {
  public event EventHandler<ServerLogEventArgs> LogReceived{add{}remove{}}public event EventHandler StatusChanged{add{}remove{}}
  public ServerStatus GetStatus(ServerInstance instance)=>new ServerStatus();public ResourceSample GetResources(ServerInstance instance)=>new ResourceSample();public IReadOnlyList<string> ReadLog(ServerInstance instance)=>new List<string>();
  public Task StartAsync(ServerInstance instance,IProgress<WorkProgress> progress,CancellationToken ct)=>throw new NotSupportedException();public Task<bool> StopAsync(ServerInstance instance,int timeout,CancellationToken ct)=>Task.FromResult(true);public void Kill(ServerInstance instance){}public void Send(ServerInstance instance,string command)=>throw new NotSupportedException();public void Dispose(){}
 }
}
