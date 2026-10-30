using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ServerWorkbench.Core
{
 public interface ISecretProtector
 {
  string Protect(string plaintext);
  string Unprotect(string protectedText);
 }
 public sealed class FrpTunnelSettings
 {
  public string ServerAddress {get;set;}="";
  public int ServerPort {get;set;}=7000;
  public string ServerName {get;set;}="";
  public int RemotePort {get;set;}=7777;
  public string TrustedCaFile {get;set;}="";
 }
 public sealed class FrpTunnelStatus
 {
  public bool Running {get;set;}
  public bool Ready {get;set;}
  public string Message {get;set;}="未连接";
  public string PublicEndpoint {get;set;}="";
 }
 public sealed class FrpTunnelService : IDisposable
 {
  public const string PinnedVersion="0.71.1";
  public const string ArchiveUrl="https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_windows_amd64.zip";
  public const string ArchiveSha256="b90074b2b6a1d792a7a40f085ac6d276bcab2d1bc60201f8a86ca53ce65ce821";
  public const string ExecutableSha256="23d11640bee87207c7a99d006b84c554df997feeba7ac7243db20a88a9cb5e01";
  private const string TokenVariable="SWB_FRP_TOKEN";
  private readonly AppPaths paths;
  private readonly ITransferService transfers;
  private readonly IInstanceRepository repository;
  private readonly IProcessManager games;
  private readonly ISecretProtector protector;
  private readonly ProcessManager clients;
  private readonly object gate=new object();
  private readonly Dictionary<string,Session> sessions=new Dictionary<string,Session>();
  private readonly HashSet<string> secrets=new HashSet<string>(StringComparer.Ordinal);
  private readonly SemaphoreSlim installGate=new SemaphoreSlim(1,1),startGate=new SemaphoreSlim(1,1);
  private readonly Timer gameWatcher;
  private volatile bool disposed;
  private int checking;
  private string Root=>Path.Combine(paths.Root,"Tunnels");
  private string ComponentDirectory=>Path.Combine(Root,"Client","frp-"+PinnedVersion);
  private string ClientExecutable=>Path.Combine(ComponentDirectory,"frpc.exe");
  private string CompletionMarker=>Path.Combine(ComponentDirectory,"install.complete");
  private static string CompletionSignature=>PinnedVersion+"\n"+ExecutableSha256+"\n"+ArchiveSha256;
  public string Version=>PinnedVersion;
  public bool IsSupported=>PathSafety.IsWindows&&Environment.Is64BitOperatingSystem&&Environment.OSVersion.Version.Major>=10&&LocalPath(Root);
  public string UnsupportedReason=>!LocalPath(Root)?"frp 凭据和组件必须保存在本地磁盘的 Data 目录，不接受 UNC 或设备路径。":"本版 frp 组件仅在 Windows 10/11 x64 启用；管理器的 Win7/8.1 支持保持不变，不会自动降级安装旧穿透组件。";
  public bool ClientInstalled {get {try{if(!LocalPath(Root))return false;RejectReparseParents(ComponentDirectory);return File.Exists(CompletionMarker)&&new FileInfo(CompletionMarker).Length<1024&&File.ReadAllText(CompletionMarker)==CompletionSignature&&File.Exists(ClientExecutable)&&PathSafety.HashFile(ClientExecutable,"SHA256")==ExecutableSha256;}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(InvalidDataException){return false;}}}
  public event EventHandler StatusChanged;
  private sealed class StoredProfile
  {
   public int SchemaVersion=1;
   public string InstanceId;
   public FrpTunnelSettings Settings;
   public string ProtectedToken;
   public string CaSha256;
  }
  private sealed class ProtectedCredential
  {
   public int SchemaVersion=1;
   public string InstanceId,ContextHash,Token;
  }
  private sealed class Session
  {
   public ServerInstance Game,Client;
   public FrpTunnelSettings Settings;
   public string ProxyName,LastMessage="正在准备穿透组件";
   public bool Ready,StopRequested;
   public int CleanupQueued;
   public CancellationTokenSource Startup;
   public readonly object OperationGate=new object();
  }
  public FrpTunnelService(AppPaths paths,ITransferService transfers,IInstanceRepository repository,IProcessManager games,ISecretProtector protector)
  {
   this.paths=paths??throw new ArgumentNullException(nameof(paths));this.transfers=transfers??throw new ArgumentNullException(nameof(transfers));this.repository=repository??throw new ArgumentNullException(nameof(repository));this.games=games??throw new ArgumentNullException(nameof(games));this.protector=protector??throw new ArgumentNullException(nameof(protector));
   clients=new ProcessManager(paths,new RuntimeProvider(paths,transfers),Redact,true);
   clients.LogReceived+=ClientLog;clients.StatusChanged+=ClientStatus;games.StatusChanged+=GameStatus;
   gameWatcher=new Timer(_=>CheckGames(),null,1000,1000);
  }
  private static bool LocalPath(string value)=>!string.IsNullOrWhiteSpace(value)&&Path.IsPathRooted(value)&&!value.StartsWith(@"\\",StringComparison.Ordinal)&&!value.StartsWith("//",StringComparison.Ordinal)&&!value.Any(char.IsControl);
  private string ProfileDirectory(ServerInstance instance){InstanceRepository.Validate(instance);if(!LocalPath(Root))throw new InvalidDataException(UnsupportedReason);return Path.Combine(Root,"Profiles",instance.Id);}
  private string ProfilePath(ServerInstance instance)=>Path.Combine(ProfileDirectory(instance),"tunnel.json");
  private static FrpTunnelSettings Copy(FrpTunnelSettings value)=>new FrpTunnelSettings{ServerAddress=value.ServerAddress,ServerPort=value.ServerPort,ServerName=value.ServerName,RemotePort=value.RemotePort,TrustedCaFile=value.TrustedCaFile};
  private void Alive(){if(disposed)throw new ObjectDisposedException(nameof(FrpTunnelService));}
  private void Supported(){Alive();if(!IsSupported)throw new PlatformNotSupportedException(UnsupportedReason);}
  private static void RejectReparseParents(string path)
  {for(var current=Path.GetFullPath(path);!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){if(File.Exists(current)||Directory.Exists(current))PathSafety.RejectReparse(current);if(Path.GetDirectoryName(current)==current)break;}}
  private StoredProfile ReadProfile(ServerInstance instance)
  {
   var file=ProfilePath(instance);if(!File.Exists(file))return null;RejectReparseParents(file);
   if(new FileInfo(file).Length>128*1024)throw new InvalidDataException("穿透配置超过大小限制。");
   var record=JsonConvert.DeserializeObject<StoredProfile>(File.ReadAllText(file));
   if(record==null||record.SchemaVersion!=1||record.InstanceId!=instance.Id||record.Settings==null)throw new InvalidDataException("穿透配置身份或版本无效，请保留文件并重新配置。");
   // Validate the managed CA location before any filesystem access to a value
   // from JSON. In particular, a forged UNC path must never trigger SMB access.
   if(!Regex.IsMatch(record.CaSha256??"",@"\A[a-f0-9]{64}\z",RegexOptions.CultureInvariant)||!LocalPath(record.Settings.TrustedCaFile))throw new InvalidDataException("穿透配置中的 CA 位置或摘要无效，请重新配置。");
   var expected=Path.Combine(ProfileDirectory(instance),"trusted-ca-"+record.CaSha256.Substring(0,16)+".pem");
   if(!string.Equals(Path.GetFullPath(record.Settings.TrustedCaFile),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("穿透 CA 必须位于本实例的受管理目录，不能直接修改配置路径。");
   return record;
  }
  public FrpTunnelSettings Load(ServerInstance instance)
  {Alive();var record=ReadProfile(instance);return record==null?new FrpTunnelSettings{RemotePort=instance.Port>0?instance.Port:7777}:Copy(record.Settings);}
  public bool HasSavedToken(ServerInstance instance){Alive();return !string.IsNullOrEmpty(ReadProfile(instance)?.ProtectedToken);}
  private static string Host(string value,string label)
  {
   value=(value??"").Trim();IPAddress address;
   if(value.Length==0||value.Length>253||value.Any(c=>c>127||char.IsControl(c)||char.IsWhiteSpace(c))||value.IndexOfAny(new[]{'/','\\','@','"','\'','[',']'})>=0||(!IPAddress.TryParse(value,out address)&&Uri.CheckHostName(value)!=UriHostNameType.Dns))throw new InvalidDataException(label+"需为纯主机名或 IP，不含协议、端口、路径或账号密码。");
   return value;
  }
  public static void ValidateSettings(FrpTunnelSettings settings)
  {
   if(settings==null)throw new ArgumentNullException(nameof(settings));
   Host(settings.ServerAddress,"中转地址");Host(settings.ServerName,"证书服务器名称");
   if(settings.ServerPort<1||settings.ServerPort>65535)throw new InvalidDataException("中转控制端口应为 1–65535。");
   if(settings.RemotePort<1024||settings.RemotePort>65535)throw new InvalidDataException("游戏公网端口应为 1024–65535。");
   if(settings.RemotePort==settings.ServerPort)throw new InvalidDataException("游戏公网端口不能与中转控制端口相同。");
   if(!LocalPath(settings.TrustedCaFile))throw new InvalidDataException("请选择本地磁盘上的 CA PEM 公钥证书，不接受网络或设备路径。");
  }
  private static void ValidateToken(string value)
  {if(!Regex.IsMatch(value??"",@"\A[A-Za-z0-9_-]{32,128}\z",RegexOptions.CultureInvariant))throw new InvalidDataException("认证令牌需为 32–128 个英文字母、数字、下划线或短横线，请与中转服务端保持一致。");}
  private static byte[] ReadCa(string file)
  {
   RejectReparseParents(file);if(!File.Exists(file))throw new FileNotFoundException("找不到 CA PEM 文件。");
   if(new FileInfo(file).Length>256*1024)throw new InvalidDataException("CA 文件超过 256KB 限制。");
   string text=File.ReadAllText(file);if(text.IndexOf("PRIVATE KEY",StringComparison.OrdinalIgnoreCase)>=0)throw new InvalidDataException("客户端只需要 CA 公钥证书，不能导入任何私钥。");
   var pattern=new Regex(@"-----BEGIN CERTIFICATE-----\s*([A-Za-z0-9+/=\s]+?)\s*-----END CERTIFICATE-----",RegexOptions.CultureInvariant);
   var matches=pattern.Matches(text);if(matches.Count!=1||pattern.Replace(text,"").Trim().Length!=0)throw new InvalidDataException("只接受包含单张 CA 公钥证书的 PEM 文件，请核对界面显示的指纹。");
   foreach(Match match in matches)try {
    var der=Convert.FromBase64String(match.Groups[1].Value);if(X509Certificate2.GetCertContentType(der)!=X509ContentType.Cert)throw new InvalidDataException("PEM 必须封装单张 DER 公钥证书，不能使用 PFX、PKCS7 或私钥容器。");
    using(var cert=new X509Certificate2(der)) {
     if(cert.HasPrivateKey)throw new InvalidDataException("不能导入含私钥的证书。");
     if(cert.NotAfter.ToUniversalTime()<=DateTime.UtcNow||cert.NotBefore.ToUniversalTime()>DateTime.UtcNow)throw new InvalidDataException("CA 证书已过期或尚未生效。");
     var constraints=cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
     if(constraints==null||!constraints.CertificateAuthority)throw new InvalidDataException("导入证书未声明 CA 用途。");
    }
   }catch(FormatException){throw new InvalidDataException("CA PEM 内容无效。");}catch(System.Security.Cryptography.CryptographicException){throw new InvalidDataException("CA 证书格式无效。");}
   return new UTF8Encoding(false).GetBytes(text.Trim()+"\n");
  }
  private static void GamePort(ServerInstance instance)
  {InstanceRepository.Validate(instance);if(instance.Udp||instance.Port<1)throw new InvalidOperationException("本版穿透只支持已设置端口的 TCP 游戏实例。");}
  private static string CredentialContext(StoredProfile profile)
  {
   var value=JsonConvert.SerializeObject(new{profile.InstanceId,profile.Settings.ServerAddress,profile.Settings.ServerPort,profile.Settings.ServerName,profile.Settings.RemotePort,profile.CaSha256});
   using(var hash=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();
  }
  private string UnprotectToken(StoredProfile profile)
  {
   try {
    var credential=JsonConvert.DeserializeObject<ProtectedCredential>(protector.Unprotect(profile.ProtectedToken));
    if(credential==null||credential.SchemaVersion!=1||credential.InstanceId!=profile.InstanceId||credential.ContextHash!=CredentialContext(profile))throw new InvalidDataException();
    ValidateToken(credential.Token);lock(gate)secrets.Add(credential.Token);return credential.Token;
   }catch{throw new InvalidOperationException("无法解密令牌或节点配置已改变。请使用原 Windows 用户并重新填写令牌，不能直接修改配置文件。");}
  }
  public void Save(ServerInstance instance,FrpTunnelSettings settings,string token)
  {
   Supported();GamePort(instance);if(GetStatus(instance).Running)throw new InvalidOperationException("请先断开本实例的穿透，再修改配置。");
   ValidateSettings(settings);var ca=ReadCa(settings.TrustedCaFile);var previous=ReadProfile(instance);
   if(string.IsNullOrEmpty(token)){if(string.IsNullOrEmpty(previous?.ProtectedToken))throw new InvalidOperationException("首次配置需要填写认证令牌。");token=UnprotectToken(previous);}
   ValidateToken(token);lock(gate)secrets.Add(token);
   var folder=ProfileDirectory(instance);RejectReparseParents(folder);Directory.CreateDirectory(folder);
   string digest;using(var hash=System.Security.Cryptography.SHA256.Create())digest=BitConverter.ToString(hash.ComputeHash(ca)).Replace("-","").ToLowerInvariant();
   var caFile=Path.Combine(folder,"trusted-ca-"+digest.Substring(0,16)+".pem");
   if(File.Exists(caFile)){RejectReparseParents(caFile);if(PathSafety.HashFile(caFile,"SHA256")!=digest)throw new InvalidDataException("现有 CA 文件内容已变化，请保留文件并重新导入。");}
   else using(var file=new FileStream(caFile,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.Write(ca,0,ca.Length);
   var saved=Copy(settings);saved.ServerAddress=Host(settings.ServerAddress,"中转地址");saved.ServerName=Host(settings.ServerName,"证书服务器名称");saved.TrustedCaFile=caFile;
   ValidateSettings(saved);var profile=new StoredProfile{InstanceId=instance.Id,Settings=saved,CaSha256=digest};
   var plaintext=JsonConvert.SerializeObject(new ProtectedCredential{InstanceId=instance.Id,ContextHash=CredentialContext(profile),Token=token});
   try{profile.ProtectedToken=protector.Protect(plaintext);}catch(Exception error){throw new InvalidOperationException("无法保护认证令牌："+Redact(error.Message));}
   if(string.IsNullOrEmpty(profile.ProtectedToken)||profile.ProtectedToken==plaintext||profile.ProtectedToken==token||profile.ProtectedToken.Length>64*1024)throw new InvalidOperationException("令牌保护器未返回有效的加密结果。");
   AtomicJson.Write(ProfilePath(instance),profile);RaiseStatus();
  }
  public async Task InstallClientAsync(IProgress<WorkProgress> progress,CancellationToken ct)
  {
   Supported();await installGate.WaitAsync(ct).ConfigureAwait(false);string stage=null;
   try {
    if(ClientInstalled){progress?.Report(new WorkProgress("官方 frpc "+PinnedVersion+" 校验通过。",100));return;}
    stage=Path.Combine(paths.Temp,"frp-install-"+Guid.NewGuid().ToString("N"));RejectReparseParents(stage);Directory.CreateDirectory(stage);
    var zip=Path.Combine(stage,"package.zip");
    await transfers.DownloadAsync(new DownloadRequest{Url=ArchiveUrl,Destination=zip,Hash=ArchiveSha256,HashAlgorithm="SHA256"},progress,ct).ConfigureAwait(false);
    // Independently enforce the pin even when a caller injects another transfer implementation.
    if(PathSafety.HashFile(zip,"SHA256")!=ArchiveSha256)throw new InvalidDataException("frp 官方压缩包 SHA256 不匹配，拒绝安装。");
    var unpacked=Path.Combine(stage,"unpacked");transfers.ExtractZip(zip,unpacked,ct);
    var executable=Path.Combine(unpacked,"frp_"+PinnedVersion+"_windows_amd64","frpc.exe");
    if(!File.Exists(executable)||PathSafety.HashFile(executable,"SHA256")!=ExecutableSha256)throw new InvalidDataException("frpc.exe SHA256 不匹配，拒绝运行。");
    var target=ComponentDirectory;RejectReparseParents(target);Directory.CreateDirectory(Path.GetDirectoryName(target));ct.ThrowIfCancellationRequested();
    // OneDrive/AV may hold a directory without FILE_SHARE_DELETE. Do not rename
    // installation directories: publish files, verify them, then write a final
    // completion marker. No client can start from an incomplete installation.
    Directory.CreateDirectory(target);RejectReparseParents(CompletionMarker);File.WriteAllText(CompletionMarker,"installing",new UTF8Encoding(false));
    CopyComponentFile(executable,ClientExecutable,ct);CopyComponentFile(zip,Path.Combine(target,"official-package.zip"),ct);
    var license=Path.Combine(unpacked,"frp_"+PinnedVersion+"_windows_amd64","LICENSE");if(File.Exists(license))CopyComponentFile(license,Path.Combine(target,"LICENSE-frp.txt"),ct);
    ct.ThrowIfCancellationRequested();if(PathSafety.HashFile(ClientExecutable,"SHA256")!=ExecutableSha256||PathSafety.HashFile(Path.Combine(target,"official-package.zip"),"SHA256")!=ArchiveSha256)throw new InvalidDataException("组件提交后的摘要校验失败，未允许启动。");
    File.WriteAllText(CompletionMarker,CompletionSignature,new UTF8Encoding(false));progress?.Report(new WorkProgress("已安装并校验官方 frpc "+PinnedVersion+"；尚未连接任何中转节点。",100));
   }finally{
    try{if(stage!=null&&Directory.Exists(stage))Directory.Delete(stage,true);}
    catch(IOException){progress?.Report(new WorkProgress("组件暂存目录仍被占用，已保留缓存；没有修改系统权限。"));}
    catch(UnauthorizedAccessException){progress?.Report(new WorkProgress("组件暂存目录暂时不能清理，已保留缓存；没有修改系统权限。"));}
    finally{installGate.Release();}
   }
  }
  private static void CopyComponentFile(string source,string destination,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();RejectReparseParents(destination);
   if(File.Exists(destination))File.Copy(destination,destination+".preserved-"+Guid.NewGuid().ToString("N"));
   File.Copy(source,destination,true);
  }
  public static string RenderConfiguration(FrpTunnelSettings settings,int localPort,string proxyName)
  {
   ValidateSettings(settings);if(localPort<1||localPort>65535)throw new InvalidDataException("本地游戏端口无效。");
   if(!Regex.IsMatch(proxyName??"",@"\Aswb-[a-fA-F0-9]{32}\z"))throw new InvalidDataException("穿透代理名称无效。");
   // frpc expands Go templates before TOML decoding. Unicode-escape braces in
   // every dynamic TOML string so legal paths containing {{...}} stay literal.
   Func<string,string> quote=s=>JsonConvert.SerializeObject(s).Replace("{",@"\u007b").Replace("}",@"\u007d");
   return "serverAddr = "+quote(Host(settings.ServerAddress,"中转地址"))+"\nserverPort = "+settings.ServerPort+"\nloginFailExit = true\n"+
    "auth.method = \"token\"\nauth.token = \"{{ .Envs."+TokenVariable+" }}\"\nauth.additionalScopes = [\"HeartBeats\", \"NewWorkConns\"]\n"+
    "transport.protocol = \"tcp\"\ntransport.tls.enable = true\ntransport.tls.trustedCaFile = "+quote(settings.TrustedCaFile)+"\ntransport.tls.serverName = "+quote(Host(settings.ServerName,"证书服务器名称"))+"\ntransport.tls.disableCustomTLSFirstByte = true\n"+
    "log.to = \"console\"\nlog.level = \"info\"\nlog.disablePrintColor = true\n\n[[proxies]]\nname = "+quote(proxyName)+"\ntype = \"tcp\"\nlocalIP = \"127.0.0.1\"\nlocalPort = "+localPort+"\nremotePort = "+settings.RemotePort+"\n";
  }
  public async Task StartAsync(ServerInstance instance,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   Supported();GamePort(instance);await startGate.WaitAsync(ct).ConfigureAwait(false);Session session=null;CancellationTokenSource startup=null;
   try {
    if(GetStatus(instance).Running)throw new InvalidOperationException("此实例的穿透正在启动或运行。");
    RequireGame(instance);var profile=ReadProfile(instance)??throw new InvalidOperationException("请先保存中转节点、CA 和认证令牌。");ValidateSettings(profile.Settings);
    string token=UnprotectToken(profile);
    var ca=ReadCa(profile.Settings.TrustedCaFile);if(PathSafety.HashFile(profile.Settings.TrustedCaFile,"SHA256")!=profile.CaSha256)throw new InvalidDataException("保存的 CA 证书已改变，请重新导入确认。");
    startup=CancellationTokenSource.CreateLinkedTokenSource(ct);session=new Session{Game=instance,Settings=Copy(profile.Settings),ProxyName="swb-"+instance.Id,Startup=startup};
    lock(gate)sessions[instance.Id]=session;RaiseStatus();
    await InstallClientAsync(progress,startup.Token).ConfigureAwait(false);RequireGame(instance);startup.Token.ThrowIfCancellationRequested();
    var folder=ProfileDirectory(instance);RejectReparseParents(folder);Directory.CreateDirectory(folder);
    var executable=Path.Combine(folder,"frpc.exe");if(File.Exists(executable)&&PathSafety.HashFile(executable,"SHA256")!=ExecutableSha256)File.Move(executable,executable+".preserved-"+Guid.NewGuid().ToString("N"));
    if(!File.Exists(executable))File.Copy(ClientExecutable,executable);
    if(PathSafety.HashFile(executable,"SHA256")!=ExecutableSha256)throw new InvalidDataException("启动前 frpc.exe 校验失败。");
    var configuration=Path.Combine(folder,"frpc.toml");RejectReparseParents(configuration);File.WriteAllText(configuration,RenderConfiguration(session.Settings,instance.Port,session.ProxyName),new UTF8Encoding(false));
    // This synthetic instance is never saved to the game repository. Its only secret
    // is inherited through the child environment, not a file or a command argument.
    var client=new ServerInstance{Id=Guid.NewGuid().ToString("N"),Name="frpc",Kind=ServerKind.Custom,StorageDirectory=folder,EntryPoint="frpc.exe",WorkingDirectory=".",Port=0,AutoSaveMinutes=0,Runtime=new RuntimeRequirement{Kind=RuntimeKind.Native},Arguments=new List<string>{"-c",configuration}};
    client.Environment[TokenVariable]=token;session.Client=client;session.LastMessage="等待中转节点确认代理注册";
    Task launch;lock(session.OperationGate){startup.Token.ThrowIfCancellationRequested();if(session.StopRequested)throw new OperationCanceledException(startup.Token);launch=clients.StartAsync(client,progress,startup.Token);}
    try{await launch.ConfigureAwait(false);}finally{client.Environment.Remove(TokenVariable);}
    var deadline=DateTime.UtcNow.AddSeconds(25);
    while(DateTime.UtcNow<deadline) {
     startup.Token.ThrowIfCancellationRequested();if(session.StopRequested)throw new OperationCanceledException(startup.Token);RequireGame(instance);
     var status=GetStatus(instance);if(status.Ready){progress?.Report(new WorkProgress("中转节点已确认代理注册；公网连通性仍需异地朋友实测。",100));return;}
     if(!clients.GetStatus(client).Running)throw new InvalidOperationException("frpc 已退出，请检查节点、令牌、CA、证书名称和远端端口。日志已隐藏令牌。");
     await Task.Delay(100,startup.Token).ConfigureAwait(false);
    }
    throw new TimeoutException("中转节点未在 25 秒内确认代理注册，已断开本次穿透；游戏继续运行。");
   }catch(Exception error) {
    if(session!=null)StopSession(session,"穿透未连接："+Redact(error.Message));
    if(error is OperationCanceledException)throw;
    throw new InvalidOperationException("穿透启动失败："+Redact(error.Message));
   }finally {if(session!=null){lock(gate)session.Startup=null;}startup?.Dispose();startGate.Release();RaiseStatus();}
  }
  private void RequireGame(ServerInstance instance)
  {var status=games.GetStatus(instance);if(!status.Running||!status.Ready)throw new InvalidOperationException("请先启动游戏服务器并等待就绪，再开启穿透。");}
  private string Redact(string value)
  {if(disposed)return "[穿透日志已停止]";if(value==null)return "";lock(gate){foreach(var secret in secrets.OrderByDescending(x=>x.Length))value=value.Replace(secret,"[令牌已隐藏]");}return value;}
  private void ClientLog(object sender,ServerLogEventArgs e)
  {
   Session session;lock(gate)session=sessions.Values.FirstOrDefault(s=>s.Client?.Id==e.InstanceId&&!s.StopRequested);
   if(session==null)return;bool changed=false;
   lock(gate) {
    if(e.Line.IndexOf("["+session.ProxyName+"] start proxy success",StringComparison.OrdinalIgnoreCase)>=0){session.Ready=true;session.LastMessage="中转节点已接受转发（公网连通性未验证）";changed=true;}
    else if(Regex.IsMatch(e.Line,@"(?:connection|control|session).*\b(?:closed|closing|lost)\b|(?:login|connect|proxy).*\b(?:error|failed)\b|heartbeat.*\b(?:timeout|failed|error)\b|\breconnect|\bEOF\b|i/o timeout|context deadline exceeded",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)){session.Ready=false;session.LastMessage="穿透连接中断或被拒绝；请查看日志（公网未验证）";changed=true;}
   }
   if(changed)RaiseStatus();
  }
  private void ClientStatus(object sender,EventArgs e)
  {
   Session[] copy;lock(gate)copy=sessions.Values.Where(s=>s.Client!=null&&s.Startup==null).ToArray();
   foreach(var session in copy)if(!clients.GetStatus(session.Client).Running)QueueCleanup(session);RaiseStatus();
  }
  private void GameStatus(object sender,EventArgs e)=>CheckGames();
  private void CheckGames()
  {
   if(disposed||Interlocked.Exchange(ref checking,1)!=0)return;
   try {
    Session[] copy;lock(gate)copy=sessions.Values.Where(s=>!s.StopRequested).ToArray();
    foreach(var session in copy){var status=games.GetStatus(session.Game);if(!status.Running||!status.Ready)StopSession(session,"游戏已停止或未就绪，穿透已自动断开。");else if(session.Startup==null&&session.Client!=null&&!clients.GetStatus(session.Client).Running)QueueCleanup(session);}
   }catch{ }finally{Interlocked.Exchange(ref checking,0);}
  }
  private void StopSession(Session session,string message)
  {
   CancellationTokenSource startup;lock(gate){session.StopRequested=true;session.Ready=false;session.LastMessage=message;startup=session.Startup;}
   try{startup?.Cancel();}catch(ObjectDisposedException){}
   try{lock(session.OperationGate)if(session.Client!=null)clients.Kill(session.Client);}
   catch{lock(gate)session.LastMessage="无法确认穿透已断开，请重试断开或退出管理器。";throw;}
   finally{QueueCleanup(session);RaiseStatus();}
  }
  private void QueueCleanup(Session session)
  {
   if(session.Client==null||Interlocked.Exchange(ref session.CleanupQueued,1)!=0)return;
   var client=session.Client;Task.Run(async()=>{try{await clients.ForgetStoppedAsync(client).ConfigureAwait(false);}catch(IOException){}catch(ObjectDisposedException){}});
  }
  public void Stop(ServerInstance instance)
  {Alive();Session session;lock(gate)sessions.TryGetValue(instance.Id,out session);if(session!=null)StopSession(session,"穿透已断开；游戏服务器继续运行。");}
  public FrpTunnelStatus GetStatus(ServerInstance instance)
  {
   Alive();Session session;bool preparing,ready,stopped;string message;lock(gate){sessions.TryGetValue(instance.Id,out session);preparing=session?.Startup!=null;ready=session?.Ready??false;stopped=session?.StopRequested??false;message=session?.LastMessage;}
   if(session==null)return new FrpTunnelStatus();
   bool running=session.Client!=null&&clients.GetStatus(session.Client).Running;
   if(!running&&!preparing){ready=false;if(!stopped)message="穿透进程已停止，请查看日志。";QueueCleanup(session);}
   if(stopped)ready=false;
   return new FrpTunnelStatus{Running=running||(preparing&&!stopped),Ready=running&&ready,Message=message??"未连接",PublicEndpoint=running&&ready?Endpoint(session.Settings):""};
  }
  private static string Endpoint(FrpTunnelSettings settings)=>(settings.ServerAddress.Contains(":")?"["+settings.ServerAddress+"]":settings.ServerAddress)+":"+settings.RemotePort;
  public IReadOnlyList<string> ReadLog(ServerInstance instance)
  {
   Alive();Session session;lock(gate)sessions.TryGetValue(instance.Id,out session);
   var client=session?.Client??new ServerInstance{Id=instance.Id,Name="frpc",StorageDirectory=ProfileDirectory(instance)};
   return clients.ReadLog(client).Select(Redact).ToArray();
  }
  private void RaiseStatus(){if(!disposed)StatusChanged?.Invoke(this,EventArgs.Empty);}
  public void Dispose()
  {
   if(disposed)return;disposed=true;gameWatcher.Dispose();games.StatusChanged-=GameStatus;Session[] copy;lock(gate)copy=sessions.Values.ToArray();
   foreach(var session in copy)try{StopSession(session,"管理器已退出，穿透已断开。");}catch{ }
   clients.LogReceived-=ClientLog;clients.StatusChanged-=ClientStatus;clients.Dispose();lock(gate)secrets.Clear();
  }
 }
}
