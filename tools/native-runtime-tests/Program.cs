using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ServerWorkbench.Core;

internal static class Program
{
 private static int passed,failed;
 private static int Main(string[] args){try{return Run(args).GetAwaiter().GetResult();}catch(Exception e){Console.WriteLine(e);return 1;}}
 private static async Task<int> Run(string[] args)
 {
  if(args.Length!=2)throw new ArgumentException("Usage: NativeRuntimeTests.exe <verified Microsoft XNA MSI> <test artifacts parent>");
  if(Environment.OSVersion.Platform!=PlatformID.Win32NT)throw new PlatformNotSupportedException("Windows MSI APIs and system expand.exe are required.");
  var msi=Path.GetFullPath(args[0]);if(!PathSafety.HashFile(msi,"SHA256").Equals(NativeGameRuntimeProvider.XnaSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Fixture is not the pinned Microsoft MSI.");
  var root=Path.Combine(Path.GetFullPath(args[1]),"nrt-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(root);Console.WriteLine("Artifacts="+root+"; OS="+Environment.OSVersion+"; CLR="+Environment.Version);
  await Test("Real MSI read-only stream/File-table and nested CAB extraction",async()=>{
   var p=new AppPaths(Path.Combine(root,"ok"));var target=Payload(p);var provider=new NativeGameRuntimeProvider(p,new FixtureTransfer(msi));await provider.PrepareTerrariaAsync(target,null,CancellationToken.None);
   var expected=new[]{"Microsoft.Xna.Framework.dll","Microsoft.Xna.Framework.Graphics.dll","Microsoft.Xna.Framework.Game.dll","Microsoft.Xna.Framework.GamerServices.dll","Microsoft.Xna.Framework.Input.Touch.dll","Microsoft.Xna.Framework.Xact.dll","XnaNative.dll","msvcr100.dll","msvcp100.dll","X3DAudio1_7.dll","d3dx9_41.dll"};
   Require(Directory.GetFiles(target,"*.dll").Select(Path.GetFileName).OrderBy(x=>x).SequenceEqual(expected.OrderBy(x=>x)),"Extraction copied unexpected or missing DLLs.");
   foreach(var file in expected)RuntimeProvider.ValidateArchitecture(Path.Combine(target,file),"x86");
   Require(!Directory.EnumerateFiles(target,"*.exe").Any(x=>Path.GetFileName(x)!="TerrariaServer.exe"),"An installer executable escaped the whitelist.");
   Require(!Directory.GetDirectories(p.Temp,"xna-private-*").Any(),"Temporary MSI extraction was not cleaned.");
   await provider.PrepareTerrariaAsync(target,null,CancellationToken.None);Require(Directory.GetFiles(target,"*.dll").Length==expected.Length,"Idempotent preparation changed file set.");
  });
  await Test("Tampered MSI is rejected even if a custom transfer skips hashing",async()=>{
   var p=new AppPaths(Path.Combine(root,"hash"));var target=Payload(p);await Reject(()=>new NativeGameRuntimeProvider(p,new FixtureTransfer(msi,true)).PrepareTerrariaAsync(target,null,CancellationToken.None));Require(Directory.GetFiles(target,"*.dll").Length==0,"Tampered payload wrote dependencies.");Require(!Directory.GetDirectories(p.Temp,"xna-private-*").Any(),"Tampered staging leaked.");
  });
  await Test("Conflicting package DLL is preserved without partial dependency copies",async()=>{
   var p=new AppPaths(Path.Combine(root,"conflict"));var target=Payload(p);var conflict=Path.Combine(target,"Microsoft.Xna.Framework.dll");File.WriteAllText(conflict,"preserve-this-file");await Reject(()=>new NativeGameRuntimeProvider(p,new FixtureTransfer(msi)).PrepareTerrariaAsync(target,null,CancellationToken.None));Require(File.ReadAllText(conflict)=="preserve-this-file","Existing runtime was overwritten.");Require(Directory.GetFiles(target,"*.dll").Length==1,"Conflict caused partial copies.");
  });
  await Test("Existing instance directory and pre-cancelled operations are rejected",async()=>{
   var p=new AppPaths(Path.Combine(root,"bounds"));var instance=Path.Combine(p.Instances,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(instance);File.WriteAllText(Path.Combine(instance,"TerrariaServer.exe"),"fixture");var transfer=new FixtureTransfer(msi);await Reject(()=>new NativeGameRuntimeProvider(p,transfer).PrepareTerrariaAsync(instance,null,CancellationToken.None));Require(transfer.Calls==0,"Existing instance triggered a download.");var target=Payload(p);using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();await Reject(()=>new NativeGameRuntimeProvider(p,transfer).PrepareTerrariaAsync(target,null,cancelled.Token));}Require(transfer.Calls==0,"Pre-cancelled operation downloaded runtime.");
  });
  Console.WriteLine("RESULT passed="+passed+" failed="+failed);return failed==0?0:1;
 }
 private static string Payload(AppPaths p){var path=Path.Combine(p.Temp,"payload","server");Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"TerrariaServer.exe"),"test marker, not executable");return path;}
 private static async Task Test(string name,Func<Task> test){try{await test();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
 private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
 private static async Task Reject(Func<Task> action){try{await action();}catch{return;}throw new InvalidOperationException("Expected operation to fail.");}
 private sealed class FixtureTransfer:ITransferService
 {
  private readonly string msi;private readonly bool corrupt;public int Calls;
  public FixtureTransfer(string msi,bool corrupt=false){this.msi=msi;this.corrupt=corrupt;}
  public Task DownloadAsync(DownloadRequest request,IProgress<WorkProgress> progress,CancellationToken ct){Calls++;Require(request.Url==NativeGameRuntimeProvider.XnaDownloadUrl&&request.Hash==NativeGameRuntimeProvider.XnaSha256&&request.HashAlgorithm=="SHA256","Production request must pin official URL and SHA256.");ct.ThrowIfCancellationRequested();File.Copy(msi,request.Destination);if(corrupt)using(var file=new FileStream(request.Destination,FileMode.Open,FileAccess.ReadWrite)){file.Position=100;int value=file.ReadByte();file.Position=100;file.WriteByte((byte)(value^1));}return Task.CompletedTask;}
  public void ExtractZip(string archive,string destination,CancellationToken ct){throw new InvalidOperationException("Native runtime preparation must not use ZIP/third-party extractors.");}
  public void CopyTree(string source,string destination,CancellationToken ct){throw new InvalidOperationException("Only explicit whitelisted DLL copies are expected.");}
 }
}
