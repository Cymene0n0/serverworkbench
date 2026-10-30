using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServerWorkbench.Core;

internal static class Program
{
 static readonly string Root=Path.Combine(Path.GetTempPath(),"ServerWorkbench-tests-"+Guid.NewGuid().ToString("N"));
 static int passed,failed;
 static async Task<int> Main(string[] args)
 {
  var forgeFixture=Environment.GetEnvironmentVariable("SWB_FORGE_SCRIPT_TEST");
  if(!string.IsNullOrEmpty(forgeFixture)){File.WriteAllLines(forgeFixture,args);if(Environment.GetEnvironmentVariable("SWB_SCRIPT_STDIN_TEST")=="1")Console.WriteLine("PIPE:"+Console.ReadLine());return 23;}
  Directory.CreateDirectory(Root);
  await Test("Windows 路径边界及保留名",()=>{
   foreach(var p in new[]{"../world","a/../../x","C:\\world","\\\\host\\share","/absolute","mods/a:stream","mods/NUL.jar","mods/x. ","mods/x."})Throws(()=>PathSafety.ResolveUnder(Root,p));
   Equal(Path.Combine(Root,"mods","中文 空格.jar"),PathSafety.ResolveUnder(Root,"mods/中文 空格.jar"));return Task.CompletedTask;
  });
  await Test("Windows 参数引用往返",()=>{
   var values=new[]{"","plain","中文 空格","C:\\a b\\","has\"quote","\\\\trailing\\","a\\\"b","& plain executable argument"};
   var parsed=WindowsCommandLine.Parse(WindowsCommandLine.Join(values));Equal(string.Join("|",values),string.Join("|",parsed));Throws(()=>WindowsCommandLine.Parse("\"unclosed"));return Task.CompletedTask;
  });
  await Test("实例持久化与 ID/配置校验",()=>{
   var p=Paths("repository");var repo=new InstanceRepository(p);var s=Instance();repo.Save(s);s.Name="更新后的中文实例";repo.Save(s);Equal(s.Name,repo.List()[0].Name);
   Throws(()=>repo.Save(new ServerInstance {Id="../oops"}));Throws(()=>repo.Save(new ServerInstance {EntryPoint="../server.exe"}));Throws(()=>repo.Save(new ServerInstance {SchemaVersion=2}));repo.Delete(s.Id);Equal(0,repo.List().Count);return Task.CompletedTask;
  });
  await Test("ZIP 安全解压与中文路径",()=>{
   var zip=Zip("good",("mods/中文 空格.txt","hello"),("Worlds/map.wld","world"));var dir=Path.Combine(Root,"unzip");new TransferService().ExtractZip(zip,dir,default);Equal("hello",File.ReadAllText(Path.Combine(dir,"mods","中文 空格.txt")));return Task.CompletedTask;
  });
  await Test("ZIP 越界/大小写冲突/符号链接拒绝",()=>{
   foreach(var item in new[]{("slip","../outside.txt"),("backslash","..\\outside.txt"),("ads","mods/x:payload"),("reserved","mods/CON")}) {var z=Zip(item.Item1,(item.Item2,"x"));Throws(()=>new TransferService().ExtractZip(z,Path.Combine(Root,item.Item1+"out"),default));}
   var collision=Zip("collision",("Mods/a.jar","a"),("mods/A.jar","b"));Throws(()=>new TransferService().ExtractZip(collision,Path.Combine(Root,"collisionout"),default));
   var link=Path.Combine(Root,"symlink.zip");using(var z=ZipFile.Open(link,ZipArchiveMode.Create)){var e=z.CreateEntry("link");e.ExternalAttributes=unchecked((int)0xA1FF0000);using(var w=new StreamWriter(e.Open()))w.Write("../../target");}
   Throws(()=>new TransferService().ExtractZip(link,Path.Combine(Root,"linkout"),default));Assert(!File.Exists(Path.Combine(Root,"outside.txt")),"不应写入根目录之外");return Task.CompletedTask;
  });
  await Test("损坏 ZIP 与取消解压",()=>{
   var bad=Path.Combine(Root,"broken.zip");File.WriteAllText(bad,"not a zip");Throws(()=>new TransferService().ExtractZip(bad,Path.Combine(Root,"brokenout"),default));var ct=new CancellationToken(true);Throws(()=>new TransferService().ExtractZip(Zip("cancelzip",("x","y")),Path.Combine(Root,"cancelout"),ct));return Task.CompletedTask;
  });
  await Test("下载校验和 HTTPS 链接校验",async()=>{
   var bytes=Encoding.UTF8.GetBytes("verified server file");var client=Client(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)});var service=new TransferService(client);var file=Path.Combine(Root,"download","server.bin");
   await service.DownloadAsync(new DownloadRequest {Url="https://official.example/server",Destination=file,Hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),HashAlgorithm="SHA256"},null,default);Equal("verified server file",File.ReadAllText(file));
   await ThrowsAsync(()=>service.DownloadAsync(new DownloadRequest{Url="http://example/file",Destination=file},null,default));
   await ThrowsAsync(()=>service.DownloadAsync(new DownloadRequest{Url="https://example/file",Destination=file,Hash="00"},null,default));Equal("verified server file",File.ReadAllText(file));Assert(!Directory.GetFiles(Path.GetDirectoryName(file),"*.part").Any(),"应清理临时下载");
  });
  await Test("网页响应、下载取消和不完整传输",async()=>{
   var file=Path.Combine(Root,"bad-download.bin");var html=new TransferService(Client(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("<html>sign in</html>",Encoding.UTF8,"text/html")}));await ThrowsAsync(()=>html.DownloadAsync(new DownloadRequest{Url="https://example/login",Destination=file},null,default));Assert(!File.Exists(file),"不能把网页保存为服务端");
   var good=new TransferService(Client(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[]{1,2,3})}));await ThrowsAsync(()=>good.DownloadAsync(new DownloadRequest{Url="https://example/file",Destination=file},null,new CancellationToken(true)));
   var incomplete=new TransferService(Client(_=>{var r=new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(new byte[]{1})};r.Content.Headers.ContentLength=20;return r;}));await ThrowsAsync(()=>incomplete.DownloadAsync(new DownloadRequest{Url="https://example/truncated",Destination=file},null,default));Assert(!File.Exists(file),"不完整文件不得提交");
  });
  await Test("导入分析 Java/.NET/Windows 架构与多入口",async()=>{
   var source=Path.Combine(Root,"分析目录 中文");Directory.CreateDirectory(source);MakeJar(Path.Combine(source,"server.jar"),61);
   File.WriteAllText(Path.Combine(source,"run.bat"),"@echo off\r\njava -jar server.jar");
   File.WriteAllText(Path.Combine(source,"game.dll"),"managed fixture");File.WriteAllText(Path.Combine(source,"game.runtimeconfig.json"),"{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.1\"},\"rollForward\":\"LatestPatch\"}}");
   var pe=new byte[128];pe[0]=0x4d;pe[1]=0x5a;pe[0x3c]=64;pe[64]=0x50;pe[65]=0x45;pe[68]=0x64;pe[69]=0x86;File.WriteAllBytes(Path.Combine(source,"native.exe"),pe);File.WriteAllBytes(Path.Combine(source,"linux-server"),new byte[]{0x7F,69,76,70});
   var draft=ImportService.AnalyzeDirectory(source);Equal(5,draft.Candidates.Count);Equal(17,draft.Candidates.Single(x=>x.RelativePath=="server.jar").Runtime.JavaMajor);Equal("8.0.1",draft.Candidates.Single(x=>x.RelativePath=="game.dll").Runtime.Version);Assert(!draft.Candidates.Single(x=>x.RelativePath=="linux-server").Compatible,"ELF 应拒绝");
   var paths=Paths("import");var repo=new InstanceRepository(paths);var importer=new ImportService(paths,repo,new TransferService(),new FakeRuntime());var staged=await importer.StageAsync(source,null,default);var instance=Instance();instance.EntryPoint="server.jar";instance.Runtime=staged.Candidates.Single(x=>x.RelativePath=="server.jar").Runtime;
   await importer.CommitAsync(staged,instance,null,default);Assert(File.Exists(Path.Combine(paths.InstanceDirectory(instance),"server.jar")),"导入后应有文件");Assert(File.Exists(Path.Combine(source,"server.jar")),"源目录不应改变");Equal(1,repo.List().Count);
  });
  await Test("导入取消与无效入口不提交实例",async()=>{
   var paths=Paths("cancelimport");var repo=new InstanceRepository(paths);var importer=new ImportService(paths,repo,new TransferService(),new FakeRuntime());await ThrowsAsync(()=>importer.StageAsync(Zip("importzip",("world","save")),null,new CancellationToken(true)));Equal(0,repo.List().Count);
   var draft=await importer.StageAsync(Zip("noentry",("readme.txt","x")),null,default);await ThrowsAsync(()=>importer.CommitAsync(draft,new ServerInstance{EntryPoint="missing.exe"},null,default));Equal(0,repo.List().Count);importer.Discard(draft);
  });
  await Test(".NET 多框架声明优先准备Desktop或ASP.NET",()=>{
   var dir=Path.Combine(Root,"desktop-runtime");Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,"server.dll"),"fixture");File.WriteAllText(Path.Combine(dir,"server.runtimeconfig.json"),"{\"runtimeOptions\":{\"frameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.0\"},{\"name\":\"Microsoft.WindowsDesktop.App\",\"version\":\"8.0.0\"}]}}");var candidate=ImportService.AnalyzeDirectory(dir).Candidates.Single();Equal("Microsoft.WindowsDesktop.App",candidate.Runtime.Framework);return Task.CompletedTask;
  });
  await Test("备份恢复与恢复前保护备份",async()=>{
   var paths=Paths("backups");var repo=new InstanceRepository(paths);var s=Instance();repo.Save(s);var world=Path.Combine(paths.InstanceDirectory(s),"world.txt");File.WriteAllText(world,"before");var process=new FakeProcesses();var backups=new BackupService(paths,new TransferService(),process);var first=await backups.CreateAsync(s,null,default);File.WriteAllText(world,"after");await backups.RestoreAsync(s,first,null,default);Equal("before",File.ReadAllText(world));Equal(2,backups.List(s).Count);
   process.Running=true;await ThrowsAsync(()=>backups.CreateAsync(s,null,default));await ThrowsAsync(()=>backups.RestoreAsync(s,first,null,default));
  });
  await Test("端口冲突检测",()=>{
   var listener=new TcpListener(IPAddress.Any,0);listener.Start();try{var port=((IPEndPoint)listener.LocalEndpoint).Port;Assert(!ProcessManager.IsPortAvailable(port),"监听中的端口必须标记占用");}finally{listener.Stop();}return Task.CompletedTask;
  });
  await ModTests.Run(Test,Root);
  await SaveTests.Run(Test);
  await InstallerTests.Run(Test);
  await DeploymentTests.Run(Test);
  if(args.Contains("--online"))await OnlineTests.Run(Test);
  if(args.Contains("--modrinth-only"))await OnlineTests.RunModrinth(Test);
  if(args.Contains("--runtimes-online"))await OnlineTests.RunRuntimes(Test);
  Console.WriteLine($"RESULT passed={passed} failed={failed}");
  try{Directory.Delete(Root,true);}catch{}
  return failed==0?0:1;
 }
 internal static async Task Test(string name,Func<Task> body){try{await body();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+"\n"+e);}}
 internal static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
 internal static void Equal<T>(T expected,T actual){if(!Equals(expected,actual))throw new Exception($"expected={expected}; actual={actual}");}
 internal static void Throws(Action body){try{body();}catch{return;}throw new Exception("Expected failure");}
 internal static async Task ThrowsAsync(Func<Task> body){try{await body();}catch{return;}throw new Exception("Expected failure");}
 internal static AppPaths Paths(string name){var p=new AppPaths(Path.Combine(Root,name));foreach(var dir in new[]{p.Root,p.Instances,p.Temp,p.Backups,p.Runtimes})Directory.CreateDirectory(dir);return p;}
 internal static ServerInstance Instance()=>new ServerInstance {Name="中文 测试服务器",EntryPoint="server.exe",StopCommand="exit"};
 internal static string Zip(string name,params (string path,string content)[] entries){var file=Path.Combine(Root,name+".zip");using(var z=ZipFile.Open(file,ZipArchiveMode.Create))foreach(var item in entries){var e=z.CreateEntry(item.path);using(var w=new StreamWriter(e.Open()))w.Write(item.content);}return file;}
 internal static void MakeJar(string file,int major){using(var z=ZipFile.Open(file,ZipArchiveMode.Create)){var m=z.CreateEntry("META-INF/MANIFEST.MF");using(var w=new StreamWriter(m.Open()))w.Write("Manifest-Version: 1.0\nMain-Class: example.Main\n");var c=z.CreateEntry("example/Main.class");using(var s=c.Open())s.Write(new byte[]{0xCA,0xFE,0xBA,0xBE,0,0,(byte)(major>>8),(byte)major});var multi=z.CreateEntry("META-INF/versions/25/example/Main.class");using(var s=multi.Open())s.Write(new byte[]{0xCA,0xFE,0xBA,0xBE,0,0,0,69});}}
 internal static HttpClient Client(Func<HttpRequestMessage,HttpResponseMessage> response)=>new HttpClient(new Handler(response));
 sealed class Handler : HttpMessageHandler {readonly Func<HttpRequestMessage,HttpResponseMessage> handler;public Handler(Func<HttpRequestMessage,HttpResponseMessage> handler){this.handler=handler;}protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){cancellationToken.ThrowIfCancellationRequested();var r=handler(request);r.RequestMessage=request;return Task.FromResult(r);}}
 internal sealed class FakeRuntime : IRuntimeProvider
 {
  public RuntimeRequirement LastRequirement;
  public Task<RuntimeResolution> EnsureAsync(RuntimeRequirement requirement,string existingPath,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   LastRequirement=requirement;
   if(requirement.Kind==RuntimeKind.DotNet){var runtime=Path.Combine(Root,"fake-dotnet",requirement.Version);Directory.CreateDirectory(Path.Combine(runtime,"shared","Microsoft.NETCore.App",requirement.Version));File.WriteAllText(Path.Combine(runtime,"dotnet.exe"),"mock runtime, never execute");File.WriteAllText(Path.Combine(runtime,"shared","Microsoft.NETCore.App",requirement.Version,"fixture.txt"),"exact version fixture");return Task.FromResult(new RuntimeResolution{Executable=Path.Combine(runtime,"dotnet.exe")});}
   return Task.FromResult(new RuntimeResolution{Executable="C:\\Java\\bin\\java.exe"});
  }
 }
 internal sealed class FakeProcesses : IProcessManager
 {
  public bool Running;public event EventHandler<ServerLogEventArgs> LogReceived;public event EventHandler StatusChanged {add{}remove{}}
  public ResourceSample GetResources(ServerInstance s)=>new ResourceSample();
  public Action<string> OnSend;
  public void Emit(ServerInstance s,string line)=>LogReceived?.Invoke(this,new ServerLogEventArgs{InstanceId=s.Id,Line=line});
  public ServerStatus GetStatus(ServerInstance s)=>new ServerStatus{Running=Running,Ready=Running};public Task StartAsync(ServerInstance s,IProgress<WorkProgress> p,CancellationToken ct){Running=true;return Task.CompletedTask;}public Task<bool> StopAsync(ServerInstance s,int seconds,CancellationToken ct){Running=false;return Task.FromResult(true);}public void Kill(ServerInstance s){Running=false;}public void Send(ServerInstance s,string command){OnSend?.Invoke(command);}public IReadOnlyList<string> ReadLog(ServerInstance s)=>new List<string>();public void Dispose(){}
 }
}
