using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ServerWorkbench.Core;
internal static class Program
{
 private static readonly object logGate=new object();private static string logFile;
 private static int Main(string[] args){try{return Run(args).GetAwaiter().GetResult();}catch(Exception e){Log("FAIL "+e);return 1;}}
 private static void Log(string text){lock(logGate){var line=DateTime.UtcNow.ToString("O")+" "+text;Console.WriteLine(line);if(logFile!=null)File.AppendAllText(logFile,line+Environment.NewLine,new System.Text.UTF8Encoding(false));}}
 private static async Task<int> Run(string[] args)
 {
  if(args.Length<1)throw new ArgumentException("Usage: AutoSaveTests.exe <existing isolated realgame Data>");
  var root=Path.GetFullPath(args[0]);if(!File.Exists(Path.Combine(root,".serverworkbench-realgame-tests")))throw new InvalidOperationException("Refusing Data without the isolated realgame-test marker.");
  logFile=Path.Combine(root,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-default-autosave.log");var report=Path.ChangeExtension(logFile,".json");
  var paths=new AppPaths(root);var repository=new InstanceRepository(paths);var server=repository.List().Single(s=>s.Kind==ServerKind.Vanilla&&s.GameVersion=="1.21.1"&&s.Name=="TEST Vanilla 1.21.1");var originalMinutes=server.AutoSaveMinutes;
  var transfer=new TransferService();var runtime=new RuntimeProvider(paths,transfer);var process=new ProcessManager(paths,runtime);var backup=new BackupService(paths,transfer,process);SaveSnapshotInfo timed=null;double elapsedSeconds=0;string beforeBackup=null;bool passed=false;
  using(var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(11)))try {
   var ct=deadline.Token;Log("OS="+Environment.OSVersion+" CLR="+Environment.Version+" instance="+server.Id+" game="+server.GameVersion+" runtime="+server.RuntimePath);Log("Only an existing marker-isolated test world; no player/network certification.");
   beforeBackup=(await backup.CreateAsync(server,null,ct)).Path;Log("PASS full backup before changing AutoSaveMinutes: "+beforeBackup);
   server.AutoSaveMinutes=5;repository.Save(server);
   using(var saves=new SaveService(paths,repository,process,transfer,backup,true)) {
    var existing=new HashSet<string>(saves.List(server).Select(s=>s.Path),StringComparer.OrdinalIgnoreCase);var began=DateTime.UtcNow;
    process.LogReceived+=(sender,line)=>Log("GAME "+line.Line);await process.StartAsync(server,null,ct);var readyWait=Stopwatch.StartNew();
    while(!process.GetStatus(server).Ready){ct.ThrowIfCancellationRequested();if(!process.GetStatus(server).Running)throw new InvalidOperationException("Minecraft exited before ready.");if(readyWait.Elapsed>TimeSpan.FromMinutes(3))throw new TimeoutException("No actual Minecraft ready log.");await Task.Delay(500,ct);}
    Log("PASS actual game ready; AutoSaveMinutes=5; SaveService schedule=true; default 30-second timer unchanged. No manual save will be sent while waiting.");var watch=Stopwatch.StartNew();int lastNotice=-1;
    while(timed==null){ct.ThrowIfCancellationRequested();if(!process.GetStatus(server).Running)throw new InvalidOperationException("Minecraft exited while waiting for its scheduled save.");timed=saves.List(server).FirstOrDefault(s=>s.CreatedUtc>=began&&!existing.Contains(s.Path)&&s.Reason=="定时保存");if(timed!=null)break;if(watch.Elapsed>TimeSpan.FromMinutes(7))throw new TimeoutException("No new confirmed scheduled snapshot within seven minutes after ready. SaveService error="+saves.LastError(server));int notice=(int)(watch.Elapsed.TotalSeconds/30);if(notice!=lastNotice){lastNotice=notice;Log("WAIT scheduled save elapsed="+watch.Elapsed.TotalSeconds.ToString("F1")+"s snapshots="+saves.List(server).Count+" error="+saves.LastError(server));}await Task.Delay(1000,ct);}
    elapsedSeconds=watch.Elapsed.TotalSeconds;Log("PASS new confirmed timed snapshot reason="+timed.Reason+" created="+timed.CreatedUtc.ToString("O")+" elapsedAfterReady="+elapsedSeconds.ToString("F1")+"s path="+timed.Path);
    if(!await saves.SaveAndStopAsync(server,null,ct))throw new TimeoutException("Normal save-and-stop exceeded its 30-second timeout.");Log("PASS normal save-and-stop confirmed and process group exited; remaining snapshots="+saves.List(server).Count);passed=true;
   }
   File.WriteAllText(report,JsonConvert.SerializeObject(new{passed,os=Environment.OSVersion.ToString(),clr=Environment.Version.ToString(),game=server.GameVersion,instance=server.Id,autoSaveMinutes=5,defaultTimerSeconds=30,elapsedAfterReadySeconds=elapsedSeconds,beforeBackup,timedSnapshot=timed.Path,timedSnapshotCreatedUtc=timed.CreatedUtc,timedSnapshotReason=timed.Reason,limitations=new[]{"No player joined","Only Windows 11 was tested","Actual elapsed includes default timer alignment"}},Formatting.Indented));Log("PASS report="+report);return 0;
  }finally {
   if(process.GetStatus(server).Running){Log("CLEANUP killing only isolated test process group after test failure.");try{process.Kill(server);}catch(Exception e){Log("Cleanup error "+e.Message);}}
   process.Dispose();server.AutoSaveMinutes=originalMinutes;repository.Save(server);Log("Restored original test-instance AutoSaveMinutes="+originalMinutes+"; all backups and snapshots retained.");
  }
 }
}
