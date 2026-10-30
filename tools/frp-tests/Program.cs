using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ServerWorkbench.Core;
using ServerWorkbench.Desktop;
internal static class Program
{
 static int passed,failed;static string root,zip,frps,certs,token;static int controlPort,remotePort;
 static readonly List<string> checks=new List<string>();
 static void Need(bool value,string message){if(!value)throw new Exception(message);}
 static string Q(string value)=>JsonConvert.SerializeObject(value);
 static string Hash(byte[] value){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(value)).Replace("-","").ToLowerInvariant();}
 static async Task Test(string name,Func<Task> body){try{await body();passed++;checks.Add(name);Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+" "+e.ToString().Replace(token??"no-token","[REDACTED]"));}}
 static async Task Reject(Func<Task> body){try{await body();}catch{return;}throw new Exception("Expected rejection.");}
 static void Reject(Action body){try{body();}catch{return;}throw new Exception("Expected rejection.");}
 static ISecretProtector Protector()=>(ISecretProtector)Activator.CreateInstance(typeof(MainForm).Assembly.GetType("ServerWorkbench.Desktop.DpapiSecretProtector"),true);
 [STAThread] static int Main(string[] args){try{if(args.Length>0&&args[0]=="--job-host")return JobHost(args).GetAwaiter().GetResult();if(args.Length>0&&args[0]=="--install-regressions")return InstallRegressionsOnly(args).GetAwaiter().GetResult();return Run(args).GetAwaiter().GetResult();}catch(Exception e){Console.WriteLine(e);return 1;}}
 static async Task<int> InstallRegressionsOnly(string[] args)
 {
  if(args.Length!=3)throw new ArgumentException("Args: --install-regressions official ZIP, new test data directory.");
  zip=Path.GetFullPath(args[1]);root=Path.GetFullPath(args[2]);Need(!Directory.Exists(root)||!Directory.EnumerateFileSystemEntries(root).Any(),"Refusing nonempty test directory.");Directory.CreateDirectory(root);
  Need(Hash(File.ReadAllBytes(zip))==FrpTunnelService.ArchiveSha256,"Official ZIP mismatch.");await InstallRegressions();Console.WriteLine("RESULT passed="+passed+" failed="+failed);return failed==0?0:1;
 }
 static async Task<int> Run(string[] args)
 {
  ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
  if(args.Length!=4)throw new ArgumentException("Args: official ZIP, verified frps.exe, ephemeral cert directory, new test data directory.");
  zip=Path.GetFullPath(args[0]);frps=Path.GetFullPath(args[1]);certs=Path.GetFullPath(args[2]);root=Path.GetFullPath(args[3]);
  Need(!Directory.Exists(root)||!Directory.EnumerateFileSystemEntries(root).Any(),"Refusing nonempty test directory.");Directory.CreateDirectory(root);
  Need(File.Exists(Path.Combine(certs,".serverworkbench-local-test-only")),"Use local-test-only certificates.");
  Need(Hash(File.ReadAllBytes(zip))==FrpTunnelService.ArchiveSha256,"Official ZIP mismatch.");
  using(var archive=ZipFile.OpenRead(zip))using(var stream=archive.GetEntry("frp_0.71.1_windows_amd64/frps.exe").Open())using(var memory=new MemoryStream()){stream.CopyTo(memory);Need(Hash(memory.ToArray())==Hash(File.ReadAllBytes(frps)),"frps.exe is not the official fixture.");}
  token=Guid.NewGuid().ToString("N")+Guid.NewGuid().ToString("N");
  controlPort=FreePort();remotePort=FreePort();
  var paths=new AppPaths(Path.Combine(root,"Data"));var repo=new InstanceRepository(paths);var games=new Game();
  var transfer=new FixtureTransfer(zip);var protector=Protector();
  await InstallRegressions();
  using(var echo=new Echo())using(var tunnels=new FrpTunnelService(paths,transfer,repo,games,protector))
  {
   var instance=new ServerInstance{Name="LOCAL TEST ONLY",Port=echo.Port,Kind=ServerKind.Custom,EntryPoint="fixture.exe"};repo.Save(instance);
   Func<FrpTunnelSettings> settings=()=>new FrpTunnelSettings{ServerAddress="127.0.0.1",ServerPort=controlPort,ServerName="localhost",RemotePort=remotePort,TrustedCaFile=Path.Combine(certs,"ca.pem")};
   var host=StartRelay();
   try {
    await Test("Current-user DPAPI token roundtrip, tamper rejection, no plaintext profile/config",async()=>{
     var cipher=protector.Protect(token);Need(cipher!=token&&!cipher.Contains(token),"DPAPI returned plaintext.");Need(protector.Unprotect(cipher)==token,"DPAPI roundtrip failed.");
     var damaged=Convert.FromBase64String(cipher);damaged[damaged.Length/2]^=1;Reject(()=>protector.Unprotect(Convert.ToBase64String(damaged)));
     tunnels.Save(instance,settings(),token);tunnels.Save(instance,settings(),"");Need(tunnels.HasSavedToken(instance),"Blank token must preserve stored secret.");
     var profile=File.ReadAllText(Path.Combine(paths.Root,"Tunnels","Profiles",instance.Id,"tunnel.json"));Need(!profile.Contains(token),"Profile leaked token.");
     var rendered=FrpTunnelService.RenderConfiguration(tunnels.Load(instance),instance.Port,"swb-"+instance.Id);
     Need(rendered.Contains("trustedCaFile")&&rendered.Contains("serverName")&&rendered.Contains("127.0.0.1")&&rendered.Contains(".Envs.SWB_FRP_TOKEN"),"Missing required identity/loopback/env config.");
     Need(!rendered.Contains(token)&&!rendered.Contains("insecure"),"Rendered config contains secret/insecure switch.");await Task.CompletedTask;
    });
    await Test("Reject token template injection, invalid endpoints/ports and private/leaf certificates",async()=>{
     foreach(var bad in new[]{token+"\n",token+"\"",token+"\\",token+"{{",new string('a',31),new string('a',129)})Reject(()=>tunnels.Save(instance,settings(),bad));
     var badSettings=settings();badSettings.ServerAddress="https://localhost/path";Reject(()=>FrpTunnelService.ValidateSettings(badSettings));
     badSettings=settings();badSettings.RemotePort=badSettings.ServerPort;Reject(()=>FrpTunnelService.ValidateSettings(badSettings));
     badSettings=settings();badSettings.TrustedCaFile=Path.Combine(certs,"server-key.pem");Reject(()=>tunnels.Save(instance,badSettings,token));
     badSettings=settings();badSettings.TrustedCaFile=Path.Combine(certs,"server.pem");Reject(()=>tunnels.Save(instance,badSettings,token));
     badSettings=settings();badSettings.TrustedCaFile=Path.Combine(certs,"wrapped-private-container.pem");Reject(()=>tunnels.Save(instance,badSettings,token));
     var bundle=Path.Combine(root,"ca-bundle.pem");File.WriteAllText(bundle,File.ReadAllText(Path.Combine(certs,"ca.pem"))+"\n"+File.ReadAllText(Path.Combine(certs,"wrong-ca.pem")));badSettings=settings();badSettings.TrustedCaFile=bundle;Reject(()=>tunnels.Save(instance,badSettings,token));
     badSettings=settings();badSettings.TrustedCaFile=@"\\serverworkbench-no-network.invalid\share\ca.pem";Reject(()=>FrpTunnelService.ValidateSettings(badSettings));
     var udp=new ServerInstance{Port=echo.Port,Udp=true};Reject(()=>tunnels.Save(udp,settings(),token));await Task.CompletedTask;
    });
    await Test("Pinned official frpc installs and forwards 128KB binary payload over verified TLS",async()=>{
     await tunnels.InstallClientAsync(null,CancellationToken.None);Need(tunnels.ClientInstalled,"Pinned client unavailable.");
     await tunnels.StartAsync(instance,null,CancellationToken.None);Need(tunnels.GetStatus(instance).Ready,"Proxy not registered.");
     await Payload(remotePort);Need(games.Kills==0,"Tunnel touched game process.");
     tunnels.Stop(instance);await Until(()=>!tunnels.GetStatus(instance).Running,5000);await Until(()=>!CanConnect(remotePort),5000);await Payload(echo.Port);
    });
    await Test("Changing saved relay metadata or CA cannot reuse a protected token silently",async()=>{
     var file=Path.Combine(paths.Root,"Tunnels","Profiles",instance.Id,"tunnel.json");var original=File.ReadAllText(file);var changed=JObject.Parse(original);changed["Settings"]["ServerAddress"]="127.0.0.2";File.WriteAllText(file,changed.ToString());
     var watch=Stopwatch.StartNew();try{await Reject(()=>tunnels.StartAsync(instance,null,CancellationToken.None));Need(watch.ElapsedMilliseconds<2000,"Tampered relay metadata was not rejected before connecting.");}finally{File.WriteAllText(file,original);}
     changed=JObject.Parse(original);changed["Settings"]["TrustedCaFile"]=@"\\serverworkbench-no-network.invalid\share\ca.pem";File.WriteAllText(file,changed.ToString());watch.Restart();try{Reject(()=>tunnels.Load(instance));Need(watch.ElapsedMilliseconds<500,"Forged CA location caused filesystem network access.");}finally{File.WriteAllText(file,original);}
     var ca=tunnels.Load(instance).TrustedCaFile;var bytes=File.ReadAllBytes(ca);try{File.Copy(Path.Combine(certs,"wrong-ca.pem"),ca,true);await Reject(()=>tunnels.StartAsync(instance,null,CancellationToken.None));}finally{File.WriteAllBytes(ca,bytes);}
    });
    await Test("Wrong token is refused and creates no forwarding listener",async()=>{
     tunnels.Save(instance,settings(),new string('b',64));await Reject(()=>tunnels.StartAsync(instance,null,CancellationToken.None));
     Need(!tunnels.GetStatus(instance).Ready&&!CanConnect(remotePort),"Wrong token exposed endpoint.");
    });
    await Test("Wrong CA is refused before proxy registration",async()=>{
     var wrong=settings();wrong.TrustedCaFile=Path.Combine(certs,"wrong-ca.pem");tunnels.Save(instance,wrong,token);
     await Reject(()=>tunnels.StartAsync(instance,null,CancellationToken.None));Need(!tunnels.GetStatus(instance).Ready&&!CanConnect(remotePort),"Untrusted CA exposed endpoint.");
    });
    await Test("Certificate server-name mismatch is refused",async()=>{
     var wrong=settings();wrong.ServerName="wrong.invalid";tunnels.Save(instance,wrong,token);
     await Reject(()=>tunnels.StartAsync(instance,null,CancellationToken.None));Need(!tunnels.GetStatus(instance).Ready&&!CanConnect(remotePort),"Mismatched certificate exposed endpoint.");
    });
    await Test("Game stop automatically closes only the tunnel and clears endpoint",async()=>{
     tunnels.Save(instance,settings(),token);await tunnels.StartAsync(instance,null,CancellationToken.None);await Payload(remotePort);
     games.Running=false;games.Changed();await Until(()=>!tunnels.GetStatus(instance).Running,5000);await Until(()=>!CanConnect(remotePort),5000);
     Need(!tunnels.GetStatus(instance).Ready&&tunnels.GetStatus(instance).PublicEndpoint==""&&games.Kills==0,"Game-stop cleanup failed.");games.Running=true;games.Changed();
    });
    await Test("Repeated tunnel start/stop releases process handles and leaves game alive",async()=>{
     // Native pipe readers and async sockets warm up worker/IOCP threads. Measure
     // a second batch so runtime thread creation is not mistaken for a job leak.
     for(int batch=0;batch<2;batch++) {
      GC.Collect();GC.WaitForPendingFinalizers();int before=Handles(),threads=Threads();var nativeBefore=NativeHandles();
      for(int i=0;i<8;i++){await tunnels.StartAsync(instance,null,CancellationToken.None);await Payload(remotePort);tunnels.Stop(instance);await Until(()=>!tunnels.GetStatus(instance).Running&&!CanConnect(remotePort),5000);}
      await Task.Delay(800);GC.Collect();GC.WaitForPendingFinalizers();var nativeAfter=NativeHandles();int delta=Handles()-before;Console.WriteLine("Repeated-stop batch="+batch+" handle delta="+delta+" thread delta="+(Threads()-threads)+" native-before="+JsonConvert.SerializeObject(nativeBefore)+" native-after="+JsonConvert.SerializeObject(nativeAfter));
      if(batch==1)Need(Count(nativeAfter,"Job")<=Count(nativeBefore,"Job")&&Count(nativeAfter,"Process")<=Count(nativeBefore,"Process")&&Count(nativeAfter,"File")<=Count(nativeBefore,"File")+2&&games.Kills==0,"Stopped jobs/processes/pipe files accumulated or touched game.");
     }
    });
    await Test("Relay loss clears registered status without killing the game",async()=>{
     await tunnels.StartAsync(instance,null,CancellationToken.None);host.Kill();host.WaitForExit(5000);await Until(()=>!tunnels.GetStatus(instance).Ready,12000);
     Need(tunnels.GetStatus(instance).PublicEndpoint==""&&games.Kills==0,"Relay disconnect remained ready.");tunnels.Stop(instance);
    });
    await Test("Cancelled connection leaves no managed client running",async()=>{
     using(var cancelled=new CancellationTokenSource(600))await Reject(()=>tunnels.StartAsync(instance,null,cancelled.Token));
     Need(!tunnels.GetStatus(instance).Running&&games.Kills==0,"Cancellation left client running.");
    });
    await Test("Manager host crash closes the frpc job and forwarding endpoint",async()=>{
     if(!host.HasExited){host.Kill();host.WaitForExit(5000);}host.Dispose();host=StartRelay();var childRoot=Path.Combine(root,"CrashHost");var signal=Path.Combine(childRoot,"ready");
     var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,WindowsCommandLine.Join(new[]{"--job-host",zip,certs,childRoot,controlPort.ToString(),remotePort.ToString(),echo.Port.ToString()})){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};info.EnvironmentVariables["SWB_FRP_TEST_TOKEN"]=token;
     using(var child=Process.Start(info)){try{await Until(()=>File.Exists(signal)||child.HasExited,15000);Need(!child.HasExited&&File.Exists(signal),"Crash fixture did not register.");await Payload(remotePort);child.Kill();child.WaitForExit(5000);await Until(()=>!CanConnect(remotePort),5000);await Payload(echo.Port);}finally{if(!child.HasExited)child.Kill();}}
    });
    await Test("Downloaded archive cannot bypass pin through injected transfer implementation",async()=>{
     var badRoot=new AppPaths(Path.Combine(root,"CorruptPackage"));var badTransfer=new FixtureTransfer(zip){Corrupt=true};
     using(var bad=new FrpTunnelService(badRoot,badTransfer,new InstanceRepository(badRoot),games,protector))await Reject(()=>bad.InstallClientAsync(null,CancellationToken.None));
    });
    await Test("Token is absent from every config, persisted profile, log and exception artifact",async()=>{
     foreach(var file in Directory.GetFiles(paths.Root,"*",SearchOption.AllDirectories).Where(f=>f.EndsWith(".toml")||f.EndsWith(".json")||f.EndsWith(".log")))Need(!File.ReadAllText(file).Contains(token),"Secret found in "+Path.GetFileName(file));
     Need(!string.Join("\n",tunnels.ReadLog(instance)).Contains(token),"Secret appeared in UI log.");await Task.CompletedTask;
    });
   }finally{tunnels.Stop(instance);if(!host.HasExited)host.Kill();host.Dispose();}
  }
  File.WriteAllText(Path.Combine(root,"results.json"),JsonConvert.SerializeObject(new{date=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),frp=FrpTunnelService.PinnedVersion,os=Environment.OSVersion.ToString(),passed,failed,checks,scope="Loopback-only frps/frpc with ephemeral CA and mock TCP game; no real public node or Internet reachability test",game_processes_touched=false},Formatting.Indented));
  Console.WriteLine("RESULT passed="+passed+" failed="+failed);return failed==0?0:1;
 }
 static async Task InstallRegressions()
 {
  await Test("Directory held without delete sharing permits pinned install; incomplete marker blocks execution",async()=>{
   var paths=new AppPaths(Path.Combine(root,"HeldComponent"));var folder=Path.Combine(paths.Root,"Tunnels","Client","frp-"+FrpTunnelService.PinnedVersion);Directory.CreateDirectory(folder);
   using(var held=HoldDirectory(folder))using(var tunnel=new FrpTunnelService(paths,new FixtureTransfer(zip),new InstanceRepository(paths),new Game(),Protector())) {
    // This is the relevant Windows sharing violation from OneDrive/AV handles:
    // a directory rename fails while writes to files inside remain permitted.
    Reject(()=>Directory.Move(folder,folder+"-renamed"));Need(Directory.Exists(folder),"Directory sharing fixture did not hold target.");
    await tunnel.InstallClientAsync(null,CancellationToken.None);Need(tunnel.ClientInstalled,"Held-directory install was not complete.");
    Need(File.Exists(Path.Combine(folder,"LICENSE-frp.txt")),"Official component license missing.");
    var marker=Path.Combine(folder,"install.complete");File.WriteAllText(marker,"installing");Need(!tunnel.ClientInstalled,"Incomplete marker allowed a client to start.");
    using(var retry=new CancellationTokenSource(10000))await tunnel.InstallClientAsync(null,retry.Token);
    Need(tunnel.ClientInstalled,"Retry after incomplete install did not recover.");
    Need(Hash(File.ReadAllBytes(Path.Combine(folder,"frpc.exe")))==FrpTunnelService.ExecutableSha256,"Published executable differs from pin.");
   }
  });
  await Test("Busy staging cleanup warns without masking installation or holding installer semaphore",async()=>{
   var paths=new AppPaths(Path.Combine(root,"HeldCleanup"));var progress=new RecordedProgress();
   using(var transfer=new HeldCleanupTransfer(zip))using(var tunnel=new FrpTunnelService(paths,transfer,new InstanceRepository(paths),new Game(),Protector())) {
    await tunnel.InstallClientAsync(progress,CancellationToken.None);Need(tunnel.ClientInstalled,"Cleanup sharing violation masked successful install.");
    Need(Directory.Exists(transfer.Stage),"Cleanup fixture did not keep the staging directory busy.");
    Need(progress.Messages.Any(message=>message.Contains("暂存目录")&&message.Contains("保留缓存")),"Busy cleanup did not produce a cache-retention warning.");
    using(var retry=new CancellationTokenSource(3000))await tunnel.InstallClientAsync(null,retry.Token);
    Need(tunnel.ClientInstalled,"Installer semaphore remained held after busy cleanup.");
   }
  });
 }
 static Microsoft.Win32.SafeHandles.SafeFileHandle HoldDirectory(string path)
 {
  var handle=CreateFile(path,0x81,3,IntPtr.Zero,3,0x02000000,IntPtr.Zero);if(handle.IsInvalid){int error=Marshal.GetLastWin32Error();handle.Dispose();throw new System.ComponentModel.Win32Exception(error,"Could not hold directory for the sharing regression.");}return handle;
 }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string file,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
 static Process StartRelay()
 {
  var config=Path.Combine(root,"frps-local.toml");
  File.WriteAllText(config,"bindAddr = \"127.0.0.1\"\nbindPort = "+controlPort+"\nproxyBindAddr = \"127.0.0.1\"\ntransport.tls.force = true\ntransport.tls.certFile = "+Q(Path.Combine(certs,"server.pem"))+"\ntransport.tls.keyFile = "+Q(Path.Combine(certs,"server-key.pem"))+"\nauth.method = \"token\"\nauth.token = \"{{ .Envs.SWB_FRP_TOKEN }}\"\nauth.additionalScopes = [\"HeartBeats\", \"NewWorkConns\"]\nallowPorts = [{ single = "+remotePort+" }]\nmaxPortsPerClient = 1\nlog.to = \"console\"\nlog.level = \"warn\"\nlog.disablePrintColor = true\n",new UTF8Encoding(false));
  var start=new ProcessStartInfo(frps,"-c "+WindowsCommandLine.Quote(config)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.EnvironmentVariables["SWB_FRP_TOKEN"]=token;
  var process=new Process{StartInfo=start};process.OutputDataReceived+=(s,e)=>{};process.ErrorDataReceived+=(s,e)=>{};process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
  try{for(int i=0;i<100;i++){if(process.WaitForExit(50))throw new Exception("Local frps exited.");if(CanConnect(controlPort))return process;}throw new Exception("Local frps did not listen.");}catch{if(!process.HasExited)process.Kill();process.Dispose();throw;}
 }
 static async Task<int> JobHost(string[] args)
 {
  token=Environment.GetEnvironmentVariable("SWB_FRP_TEST_TOKEN");var dataRoot=args[3];Directory.CreateDirectory(dataRoot);var paths=new AppPaths(Path.Combine(dataRoot,"Data"));var repository=new InstanceRepository(paths);var game=new Game();
  var instance=new ServerInstance{Name="CRASH LOCAL TEST",Port=int.Parse(args[6]),Kind=ServerKind.Custom,EntryPoint="fixture.exe"};repository.Save(instance);
  var tunnel=new FrpTunnelService(paths,new FixtureTransfer(args[1]),repository,game,Protector());
  tunnel.Save(instance,new FrpTunnelSettings{ServerAddress="127.0.0.1",ServerPort=int.Parse(args[4]),RemotePort=int.Parse(args[5]),ServerName="localhost",TrustedCaFile=Path.Combine(args[2],"ca.pem")},token);
  await tunnel.StartAsync(instance,null,CancellationToken.None);File.WriteAllText(Path.Combine(dataRoot,"ready"),"registered");await Task.Delay(Timeout.Infinite);return 0;
 }
 static int Handles(){using(var process=Process.GetCurrentProcess())return process.HandleCount;}
 static int Threads(){using(var process=Process.GetCurrentProcess())return process.Threads.Count;}
 static int Count(Dictionary<string,int> values,string type)=>values.TryGetValue(type,out var count)?count:0;
 static Dictionary<string,int> NativeHandles()
 {
  // Total HandleCount includes worker-thread and IOCP caches. Snapshot only our
  // own kernel handles and classify their types to test native launch resources.
  const int capacity=1024*1024;var table=Marshal.AllocHGlobal(capacity);var details=Marshal.AllocHGlobal(4096);var result=new Dictionary<string,int>();
  try {
   int status=NtQueryInformationProcess(new IntPtr(-1),51,table,capacity,out var returned);if(status!=0)throw new InvalidOperationException("Cannot snapshot own process handles: "+status.ToString("x"));
   long count=Marshal.ReadInt64(table);Need(count>=0&&count<16000,"Unexpected native handle table size.");
   for(int i=0;i<count;i++) {
    var handle=Marshal.ReadIntPtr(table,16+40*i);if(NtQueryObject(handle,2,details,4096,out returned)!=0)continue;
    string type=Marshal.PtrToStringUni(Marshal.ReadIntPtr(details,8),Marshal.ReadInt16(details)/2);if(!string.IsNullOrEmpty(type))result[type]=Count(result,type)+1;
   }
   return result;
  }finally{Marshal.FreeHGlobal(details);Marshal.FreeHGlobal(table);}
 }
 [DllImport("ntdll.dll")]static extern int NtQueryInformationProcess(IntPtr process,int informationClass,IntPtr information,int length,out int returned);
 [DllImport("ntdll.dll")]static extern int NtQueryObject(IntPtr handle,int informationClass,IntPtr information,int length,out int returned);
 static int FreePort(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int value=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return value;}
 static bool CanConnect(int port){using(var tcp=new TcpClient()){try{return tcp.ConnectAsync("127.0.0.1",port).Wait(200)&&tcp.Connected;}catch{return false;}}}
 static async Task Until(Func<bool> condition,int timeout){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<timeout){if(condition())return;await Task.Delay(80);}throw new TimeoutException("Expected lifecycle state was not reached.");}
 static async Task Payload(int port)
 {
  var payload=new byte[128*1024];using(var random=RandomNumberGenerator.Create())random.GetBytes(payload);
  using(var client=new TcpClient()){var connect=client.ConnectAsync("127.0.0.1",port);if(await Task.WhenAny(connect,Task.Delay(5000))!=connect)throw new TimeoutException("Forward connect timeout.");await connect;
   var stream=client.GetStream();await stream.WriteAsync(payload,0,payload.Length);var reply=new byte[payload.Length];int offset=0;
   while(offset<reply.Length){var read=stream.ReadAsync(reply,offset,reply.Length-offset);if(await Task.WhenAny(read,Task.Delay(5000))!=read)throw new TimeoutException("Forward payload timeout.");int count=await read;if(count==0)throw new IOException("Forward stream closed early.");offset+=count;}
   Need(Hash(payload)==Hash(reply),"Forward binary payload changed.");
  }
 }
 sealed class FixtureTransfer:ITransferService
 {
  readonly string fixture;readonly TransferService normal=new TransferService();public bool Corrupt;
  public FixtureTransfer(string fixture){this.fixture=fixture;}
  public Task DownloadAsync(DownloadRequest request,IProgress<WorkProgress> progress,CancellationToken token){Need(request.Url==FrpTunnelService.ArchiveUrl&&request.Hash==FrpTunnelService.ArchiveSha256,"Unexpected fixture request.");Directory.CreateDirectory(Path.GetDirectoryName(request.Destination));if(Corrupt)File.WriteAllText(request.Destination,"bad package");else File.Copy(fixture,request.Destination);return Task.CompletedTask;}
  public void ExtractZip(string archive,string destination,CancellationToken token)=>normal.ExtractZip(archive,destination,token);
  public void CopyTree(string from,string to,CancellationToken token)=>normal.CopyTree(from,to,token);
 }
 sealed class RecordedProgress:IProgress<WorkProgress>
 {
  public readonly List<string> Messages=new List<string>();
  public void Report(WorkProgress progress)=>Messages.Add(progress.Message);
 }
 sealed class HeldCleanupTransfer:ITransferService,IDisposable
 {
  readonly FixtureTransfer normal;Microsoft.Win32.SafeHandles.SafeFileHandle held;public string Stage;
  public HeldCleanupTransfer(string fixture){normal=new FixtureTransfer(fixture);}
  public async Task DownloadAsync(DownloadRequest request,IProgress<WorkProgress> progress,CancellationToken token){await normal.DownloadAsync(request,progress,token);Stage=Path.GetDirectoryName(request.Destination);held=HoldDirectory(Stage);}
  public void ExtractZip(string archive,string destination,CancellationToken token)=>normal.ExtractZip(archive,destination,token);
  public void CopyTree(string from,string to,CancellationToken token)=>normal.CopyTree(from,to,token);
  public void Dispose(){held?.Dispose();if(Stage!=null&&Directory.Exists(Stage))Directory.Delete(Stage,true);}
 }
 sealed class Game:IProcessManager
 {
  public bool Running=true;public int Kills;public event EventHandler<ServerLogEventArgs> LogReceived{add{}remove{}}public event EventHandler StatusChanged;
  public void Changed()=>StatusChanged?.Invoke(this,EventArgs.Empty);
  public ServerStatus GetStatus(ServerInstance s)=>new ServerStatus{InstanceId=s.Id,Running=Running,Ready=Running};
  public Task StartAsync(ServerInstance s,IProgress<WorkProgress> p,CancellationToken c)=>Task.CompletedTask;
  public Task<bool> StopAsync(ServerInstance s,int timeout,CancellationToken c)=>Task.FromResult(true);
  public void Kill(ServerInstance s){Kills++;}public void Send(ServerInstance s,string c){}public IReadOnlyList<string> ReadLog(ServerInstance s)=>new string[0];public ResourceSample GetResources(ServerInstance s)=>new ResourceSample();public void Dispose(){}
 }
 sealed class Echo:IDisposable
 {
  readonly TcpListener listener=new TcpListener(IPAddress.Loopback,0);public int Port=>((IPEndPoint)listener.LocalEndpoint).Port;
  public Echo(){listener.Start();Task.Run(async()=>{while(true){TcpClient client;try{client=await listener.AcceptTcpClientAsync();}catch{return;}_=Task.Run(async()=>{using(client){var stream=client.GetStream();var buffer=new byte[32768];try{int count;while((count=await stream.ReadAsync(buffer,0,buffer.Length))>0)await stream.WriteAsync(buffer,0,count);}catch(IOException){}}});}});}
  public void Dispose()=>listener.Stop();
 }
}
