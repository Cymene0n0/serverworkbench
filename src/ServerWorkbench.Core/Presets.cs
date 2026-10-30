using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ServerWorkbench.Core
{
 public sealed class PresetService : IPresetService
 {
  private readonly AppPaths paths;private readonly IInstanceRepository repository;private readonly ITransferService transfers;private readonly IRuntimeProvider runtimes;
  private readonly Dictionary<ServerKind,IGameAdapter> adapters=new Dictionary<ServerKind,IGameAdapter>();
  public PresetService(AppPaths paths,IInstanceRepository repository,ITransferService transfers,IRuntimeProvider runtimes)
  {
   this.paths=paths;this.repository=repository;this.transfers=transfers;this.runtimes=runtimes;
   foreach(var kind in new[]{ServerKind.Vanilla,ServerKind.Fabric,ServerKind.Forge,ServerKind.Terraria,ServerKind.TModLoader})adapters[kind]=new OfficialGameAdapter(this,kind);
  }
  public Task<IReadOnlyList<VersionOption>> GetVersionsAsync(ServerKind kind,CancellationToken ct) => Adapter(kind).GetVersionsAsync(ct);
  public Task<IReadOnlyList<VersionOption>> GetLoaderVersionsAsync(ServerKind kind,string gameVersion,CancellationToken ct) => Adapter(kind).GetLoaderVersionsAsync(gameVersion,ct);
  public Task<ServerInstance> InstallAsync(CreateServerRequest request,IProgress<WorkProgress> progress,CancellationToken ct) => Adapter(request.Kind).InstallAsync(request,progress,ct);
  private IGameAdapter Adapter(ServerKind kind) { IGameAdapter adapter;if(!adapters.TryGetValue(kind,out adapter))throw new NotSupportedException("此游戏请通过通用导入添加。");return adapter; }
  private sealed class OfficialGameAdapter : IGameAdapter
  {
   private readonly PresetService owner;public ServerKind Kind { get; }
   public OfficialGameAdapter(PresetService owner,ServerKind kind){this.owner=owner;Kind=kind;}
   public Task<IReadOnlyList<VersionOption>> GetVersionsAsync(CancellationToken ct)=>owner.VersionsAsync(Kind,ct);
   public Task<IReadOnlyList<VersionOption>> GetLoaderVersionsAsync(string gameVersion,CancellationToken ct)=>owner.LoadersAsync(Kind,gameVersion,ct);
   public Task<ServerInstance> InstallAsync(CreateServerRequest request,IProgress<WorkProgress> progress,CancellationToken ct)=>owner.InstallCoreAsync(request,progress,ct);
  }
  private async Task<IReadOnlyList<VersionOption>> VersionsAsync(ServerKind kind,CancellationToken ct)
  {
   if(kind==ServerKind.Terraria)
   {
    var data=await OfficialMetadata.GetAsync("https://terraria.org/api/get/dedicated-servers-names",ct).ConfigureAwait(false);
    var names=Strings(data).Where(x=>Regex.IsMatch(x,@"^terraria-server-[0-9]+\.zip$",RegexOptions.IgnoreCase)).Distinct().ToList();
    if(names.Count==0)throw new InvalidDataException("Terraria 官网下载信息已变化，请使用官网 ZIP 或直链通用导入。");
    return names.Select(x=>new VersionOption { Id=x,Label="官方专服 "+Regex.Match(x,"[0-9]+").Value,Url="https://terraria.org/api/download/pc-dedicated-server/"+Uri.EscapeDataString(x) }).ToList();
   }
   if(kind==ServerKind.TModLoader)
   {
    var data=await OfficialMetadata.GetAsync("https://api.github.com/repos/tModLoader/tModLoader/releases?per_page=50",ct).ConfigureAwait(false);
    return data.Children().Where(x=>!(bool)(x["prerelease"]??false) && !(bool)(x["draft"]??false))
     .Select(x=>new { Release=x, Asset=x["assets"]?.Children().FirstOrDefault(a=>(string)a["name"]=="tModLoader.zip") })
     .Where(x=>x.Asset!=null).Select(x=>new VersionOption { Id=(string)x.Release["tag_name"],Label=(string)x.Release["name"]??(string)x.Release["tag_name"],Url=(string)x.Asset["browser_download_url"] }).ToList();
   }
   var manifest=await OfficialMetadata.GetAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",ct).ConfigureAwait(false);
   var versions=manifest["versions"].Children().Where(x=>(string)x["type"]=="release"&&SupportedMinecraft((string)x["id"]));
   if(kind==ServerKind.Forge)
   {
    var forge=await OfficialMetadata.GetAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",ct).ConfigureAwait(false);
    var supported=new HashSet<string>(((JObject)forge["promos"]).Properties().Select(x=>Regex.Replace(x.Name,@"-(recommended|latest)$","")),StringComparer.Ordinal);
    versions=versions.Where(x=>supported.Contains((string)x["id"]));
   }
   if(kind==ServerKind.Fabric)
   {
    var fabric=await OfficialMetadata.GetAsync("https://meta.fabricmc.net/v2/versions/game",ct).ConfigureAwait(false);
    var supported=new HashSet<string>(fabric.Children().Where(x=>(bool)(x["stable"]??false)).Select(x=>(string)x["version"]),StringComparer.Ordinal);
    versions=versions.Where(x=>supported.Contains((string)x["id"]));
   }
   return versions.Select(x=>new VersionOption { Id=(string)x["id"],Label=(string)x["id"],Url=(string)x["url"] }).ToList();
  }
  public static bool SupportedMinecraft(string id)
  {
   Version version;return Version.TryParse(id,out version)&&version>=new Version(1,16,5);
  }
  private async Task<IReadOnlyList<VersionOption>> LoadersAsync(ServerKind kind,string gameVersion,CancellationToken ct)
  {
   if(kind==ServerKind.Fabric)
   {
    var data=await OfficialMetadata.GetAsync("https://meta.fabricmc.net/v2/versions/loader/"+Uri.EscapeDataString(gameVersion),ct).ConfigureAwait(false);
    return data.Children().Where(x=>(bool)(x["loader"]?["stable"]??false)).Select(x=>new VersionOption { Id=(string)x["loader"]["version"],Label=(string)x["loader"]["version"] }).ToList();
   }
   if(kind==ServerKind.Forge)
   {
    var data=await OfficialMetadata.GetAsync("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json",ct).ConfigureAwait(false);
    var result=new List<VersionOption>();foreach(var tag in new[]{"recommended","latest"})
    {
     var v=(string)data["promos"]?[gameVersion+"-"+tag];if(!string.IsNullOrWhiteSpace(v)&&!result.Any(x=>x.Id==v))result.Add(new VersionOption { Id=v,Label=v+(tag=="recommended"?"（推荐）":"（最新）") });
    }
    return result;
   }
   return new List<VersionOption>();
  }
  private async Task<ServerInstance> InstallCoreAsync(CreateServerRequest request,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   ValidateRequest(request);ct.ThrowIfCancellationRequested();
   if(request.Port==0)request.Port=request.Kind==ServerKind.Terraria||request.Kind==ServerKind.TModLoader?7777:25565;
   if(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x=>x.Port==request.Port))throw new InvalidOperationException("端口 "+request.Port+" 已被使用，请换一个端口。");
   var instance=new ServerInstance { Name=request.Name.Trim(),Kind=request.Kind,GameVersion=request.GameVersion,LoaderVersion=request.LoaderVersion,Port=request.Port,MemoryMb=request.MemoryMb,RuntimePath=request.RuntimePath??"",StorageDirectory=request.StorageDirectory??"" };
   InstanceRepository.Validate(instance);
   var target=paths.InstanceDirectory(instance);if(Directory.Exists(target)||File.Exists(target))throw new IOException("实例目录已存在，已停止安装以保留数据。");
   var temporary=Path.Combine(paths.Temp,"preset-"+Guid.NewGuid().ToString("N"));var payload=Path.Combine(temporary,"instance");Directory.CreateDirectory(payload);bool committed=false;
   try
   {
    if(request.Kind==ServerKind.Vanilla||request.Kind==ServerKind.Fabric||request.Kind==ServerKind.Forge)await InstallMinecraftAsync(request,instance,payload,progress,ct).ConfigureAwait(false);
    else if(request.Kind==ServerKind.Terraria)await InstallTerrariaAsync(request,instance,payload,progress,ct).ConfigureAwait(false);
    else if(request.Kind==ServerKind.TModLoader)await InstallTModLoaderAsync(request,instance,payload,progress,ct).ConfigureAwait(false);
    else throw new NotSupportedException("此预设尚不可用，请使用通用导入。");
    ct.ThrowIfCancellationRequested();InstanceRepository.Validate(instance);
    var entry=PathSafety.ResolveUnder(payload,instance.EntryPoint);if(!File.Exists(entry))throw new InvalidDataException("安装产物缺少可启动入口，请通用导入并手动补全。");
    InstanceRepository.WriteStorageIdentity(payload,instance);
    InstanceDeployment.Commit(payload,target,transfers,ct);
    try { repository.Save(instance);committed=true; }
    catch { if(Directory.Exists(target))Directory.Delete(target,true);throw; }
    progress?.Report(new WorkProgress("安装完成。首次启动可能由官方启动器下载剩余依赖。",100));return instance;
   }
   finally { if(Directory.Exists(temporary))Directory.Delete(temporary,true);if(!committed)progress?.Report(new WorkProgress("安装未提交，已有实例和存档保持完整。")); }
  }
  private static void ValidateRequest(CreateServerRequest r)
  {
   if(r==null)throw new ArgumentNullException(nameof(r));
   if(string.IsNullOrWhiteSpace(r.Name)||r.Name.Trim().Length>100)throw new InvalidDataException("请填写 1–100 字符的服务器名称。");
   if(r.Port<0||r.Port>65535)throw new InvalidDataException("端口超出范围。");
   if(r.MemoryMb<256||r.MemoryMb>1048576)throw new InvalidDataException("内存应在 256 MB 至 1 TB 之间。");
   var minecraft=r.Kind==ServerKind.Vanilla||r.Kind==ServerKind.Fabric||r.Kind==ServerKind.Forge;
   if(minecraft&&!r.AcceptEula)throw new InvalidOperationException("请先阅读并同意 Minecraft EULA。");
   if(minecraft&&!SupportedMinecraft(r.GameVersion))throw new NotSupportedException("此预设自动安装范围为 Minecraft 1.16.5 及之后的正式版；其他版本请通用导入。");
   if((r.Kind==ServerKind.Fabric||r.Kind==ServerKind.Forge)&&!Regex.IsMatch(r.LoaderVersion??"",@"^[0-9A-Za-z.+_-]+$"))throw new InvalidDataException("请选择有效的加载器版本。");
   if(r.MaxPlayers<1||r.MaxPlayers>255)throw new InvalidDataException("人数应为 1–255。");
   if((r.Password??"").Any(c=>c=='\r'||c=='\n')||(r.WorldName??"").Any(c=>c=='\r'||c=='\n'))throw new InvalidDataException("世界名和密码不能包含换行符。");
  }
  private async Task InstallMinecraftAsync(CreateServerRequest request,ServerInstance instance,string root,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   progress?.Report(new WorkProgress("读取 Mojang 官方版本信息"));
   var manifest=await OfficialMetadata.GetAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",ct).ConfigureAwait(false);
   var selected=manifest["versions"].Children().FirstOrDefault(x=>(string)x["id"]==request.GameVersion&&(string)x["type"]=="release");
   if(selected==null)throw new InvalidDataException("官方版本列表中没有所选正式版本。");
   var metadata=await OfficialMetadata.GetAsync((string)selected["url"],ct).ConfigureAwait(false);
   var javaMajor=(int?)metadata["javaVersion"]?["majorVersion"];if(javaMajor==null)throw new InvalidDataException("官方元数据没有声明 Java 要求，请通过通用导入选择运行时。");
   instance.Runtime=new RuntimeRequirement { Kind=RuntimeKind.Java,JavaMajor=javaMajor.Value,Architecture="x64" };
   var java=await runtimes.EnsureAsync(instance.Runtime,request.RuntimePath,progress,ct).ConfigureAwait(false);instance.RuntimePath=java.Executable;
   var server=metadata["downloads"]?["server"];if(server==null)throw new InvalidDataException("此版本没有可下载的专用服务端。");
   instance.SourceUrl=(string)server["url"];
   await transfers.DownloadAsync(new DownloadRequest { Url=(string)server["url"],Destination=Path.Combine(root,"server.jar"),Hash=(string)server["sha1"],HashAlgorithm="SHA1" },progress,ct).ConfigureAwait(false);
   ValidateJar(Path.Combine(root,"server.jar"));
   instance.EntryPoint="server.jar";instance.Arguments=new List<string>{"nogui"};instance.StopCommand="stop";instance.ReadyPattern=@"Done \(";instance.ModDirectory="mods";
   if(request.Kind==ServerKind.Fabric)
   {
    var loaders=await LoadersAsync(ServerKind.Fabric,request.GameVersion,ct).ConfigureAwait(false);if(!loaders.Any(x=>x.Id==request.LoaderVersion))throw new InvalidDataException("Fabric 官方未列出此游戏/加载器组合。");
    var installers=await OfficialMetadata.GetAsync("https://meta.fabricmc.net/v2/versions/installer",ct).ConfigureAwait(false);
    var installer=installers.Children().FirstOrDefault(x=>(bool)(x["stable"]??false))??installers.First;
    if(installer==null)throw new InvalidDataException("Fabric 安装器版本不可用。");
    var url="https://meta.fabricmc.net/v2/versions/loader/"+Uri.EscapeDataString(request.GameVersion)+"/"+Uri.EscapeDataString(request.LoaderVersion)+"/"+Uri.EscapeDataString((string)installer["version"])+"/server/jar";
    await transfers.DownloadAsync(new DownloadRequest { Url=url,Destination=Path.Combine(root,"fabric-server-launch.jar") },progress,ct).ConfigureAwait(false);
    ValidateJar(Path.Combine(root,"fabric-server-launch.jar"));instance.EntryPoint="fabric-server-launch.jar";instance.SourceUrl=url;
   }
   if(request.Kind==ServerKind.Forge)await InstallForgeAsync(request,instance,root,java.Executable,progress,ct).ConfigureAwait(false);
   File.WriteAllText(Path.Combine(root,"eula.txt"),"# Accepted by the instance creator\neula=true\n",new UTF8Encoding(false));
   File.WriteAllText(Path.Combine(root,"server.properties"),"server-port="+request.Port+"\nmax-players="+request.MaxPlayers+"\nonline-mode=true\n",new UTF8Encoding(false));
   Directory.CreateDirectory(Path.Combine(root,"mods"));
  }
  private async Task InstallForgeAsync(CreateServerRequest request,ServerInstance instance,string root,string java,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   if(!PathSafety.IsWindows)throw new PlatformNotSupportedException("Forge 安装器需要在 Windows 目标机器运行；当前系统可构建软件并测试元数据，不能验证 Windows 安装。");
   var loaders=await LoadersAsync(ServerKind.Forge,request.GameVersion,ct).ConfigureAwait(false);if(!loaders.Any(x=>x.Id==request.LoaderVersion))throw new InvalidDataException("Forge 官方推荐列表未列出所选构建，请刷新后重选。");
   var full=request.GameVersion+"-"+request.LoaderVersion;var url="https://maven.minecraftforge.net/net/minecraftforge/forge/"+full+"/forge-"+full+"-installer.jar";
   var hash=Regex.Match(await OfficialMetadata.TextAsync(url+".sha1",ct).ConfigureAwait(false),@"\b[0-9a-fA-F]{40}\b").Value;
   if(hash.Length!=40)throw new InvalidDataException("Forge 官方校验值不可用。");
   var installer=Path.Combine(root,"forge-installer.jar");await transfers.DownloadAsync(new DownloadRequest { Url=url,Destination=installer,Hash=hash,HashAlgorithm="SHA1" },progress,ct).ConfigureAwait(false);
   progress?.Report(new WorkProgress("运行 Forge 官方服务端安装器；首次下载依赖可能需要数分钟"));
   await RunInstallerAsync(java,ProcessManager.JavaUtf8Arguments.Concat(new[]{"-jar",installer,"--installServer",root}),root,progress,ct).ConfigureAwait(false);
   var bat=Path.Combine(root,"run.bat");
   if(File.Exists(bat))
   {
    var args=Directory.Exists(Path.Combine(root,"libraries","net","minecraftforge","forge"))?Directory.GetFiles(Path.Combine(root,"libraries","net","minecraftforge","forge"),"win_args.txt",SearchOption.AllDirectories):new string[0];
    if(args.Length!=1)throw new InvalidDataException("Forge 已生成脚本，但启动布局未识别。请通过通用导入补全，不会将此次安装标为成功。");
    PrepareForgeLaunchScript(root,args[0],request.MemoryMb);
    instance.Runtime=new RuntimeRequirement { Kind=RuntimeKind.Script,JavaMajor=instance.Runtime.JavaMajor,Architecture="x64" };
    instance.EntryPoint="run.bat";instance.Arguments=new List<string>{"nogui"};
    instance.Environment["JAVA_HOME"]=Path.GetDirectoryName(Path.GetDirectoryName(java));
   }
   else
   {
    var candidates=Directory.GetFiles(root,"forge-*.jar",SearchOption.TopDirectoryOnly).Where(x=>!x.EndsWith("-installer.jar",StringComparison.OrdinalIgnoreCase)&&!x.EndsWith("-universal.jar",StringComparison.OrdinalIgnoreCase)).ToList();
    if(candidates.Count!=1)throw new InvalidDataException("Forge 安装器没有生成可识别的服务端入口，请使用通用导入补全。");
    ValidateJar(candidates[0]);instance.EntryPoint=Path.GetFileName(candidates[0]);
   }
   instance.SourceUrl=url;File.Delete(installer);
  }
  internal static void PrepareForgeLaunchScript(string root,string argumentsFile,int memoryMb)
  {
   // Forge's interactive run.bat ends in pause. A managed server must exit as soon
   // as Java exits, otherwise stop waits forever and the script can consume stdin.
   var relative=Relative(root,argumentsFile).Replace('/','\\');
   if(!PathSafety.ResolveUnder(root,relative).Equals(Path.GetFullPath(argumentsFile),StringComparison.OrdinalIgnoreCase)||relative.Any(c=>c>127)||relative.IndexOfAny(new[]{'&','|','<','>','^','%','!','\r','\n','"'})>=0)
    throw new InvalidDataException("Forge 启动参数文件路径不能安全用于 Windows 脚本。");
   var script=Path.Combine(root,"run.bat");var original=script+".workbench-original";
   if(!File.Exists(original))File.Copy(script,original);
   File.WriteAllText(Path.Combine(root,"user_jvm_args.txt"),string.Join("\n",ProcessManager.JavaUtf8Arguments)+"\n-Xms"+Math.Min(512,memoryMb)+"M\n-Xmx"+memoryMb+"M\n",new UTF8Encoding(false));
   File.WriteAllText(script,"@echo off\r\njava @user_jvm_args.txt \"@"+relative+"\" %*\r\nexit /b %errorlevel%\r\n",Encoding.ASCII);
  }
  private async Task InstallTerrariaAsync(CreateServerRequest request,ServerInstance instance,string root,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   var versions=await VersionsAsync(ServerKind.Terraria,ct).ConfigureAwait(false);var selected=versions.FirstOrDefault(x=>x.Id==request.GameVersion);
   if(selected==null)throw new InvalidDataException("所选 Terraria 官方包已不可用，请刷新版本或使用通用导入。");
   var archive=Path.Combine(root,"download.zip");var extracted=Path.Combine(root,"unpack");
   await transfers.DownloadAsync(new DownloadRequest { Url=selected.Url,Destination=archive },progress,ct).ConfigureAwait(false);transfers.ExtractZip(archive,extracted,ct);
   var exe=Directory.GetFiles(extracted,"TerrariaServer.exe",SearchOption.AllDirectories).FirstOrDefault(x=>Path.GetFileName(Path.GetDirectoryName(x)).Equals("Windows",StringComparison.OrdinalIgnoreCase))??Directory.GetFiles(extracted,"TerrariaServer.exe",SearchOption.AllDirectories).FirstOrDefault();
   if(exe==null)throw new InvalidDataException("官方压缩包中没有 TerrariaServer.exe，请手动导入。");
   var server=Path.Combine(root,"server");transfers.CopyTree(Path.GetDirectoryName(exe),server,ct);Directory.Delete(extracted,true);File.Delete(archive);
   await new NativeGameRuntimeProvider(paths,transfers).PrepareTerrariaAsync(server,progress,ct).ConfigureAwait(false);
   instance.EntryPoint="server/TerrariaServer.exe";instance.WorkingDirectory="server";instance.Runtime=new RuntimeRequirement { Kind=RuntimeKind.Native };instance.RuntimePath="";
   instance.Arguments=new List<string>{"-config","serverconfig.txt"};instance.StopCommand="exit";instance.ReadyPattern="Server started";instance.ModDirectory="server/Mods";instance.SourceUrl=selected.Url;
   WriteTerrariaConfig(request,server);Directory.CreateDirectory(Path.Combine(server,"Mods"));
  }
  private async Task InstallTModLoaderAsync(CreateServerRequest request,ServerInstance instance,string root,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   if(!Regex.IsMatch(request.GameVersion??"",@"^[0-9A-Za-z._+-]+$"))throw new InvalidDataException("无效的 tModLoader 发布标签。");
   var release=await OfficialMetadata.GetAsync("https://api.github.com/repos/tModLoader/tModLoader/releases/tags/"+Uri.EscapeDataString(request.GameVersion),ct).ConfigureAwait(false);
   if((bool)(release["prerelease"]??false)||(bool)(release["draft"]??false))throw new InvalidDataException("首版仅自动安装正式稳定发布。");
   var asset=release["assets"]?.Children().FirstOrDefault(x=>(string)x["name"]=="tModLoader.zip");if(asset==null)throw new InvalidDataException("此发布没有 tModLoader.zip。");
   var zip=Path.Combine(root,"download.zip");var server=Path.Combine(root,"server");var digest=(string)asset["digest"]??"";
   await transfers.DownloadAsync(new DownloadRequest { Url=(string)asset["browser_download_url"],Destination=zip,Hash=digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)?digest.Substring(7):"",HashAlgorithm="SHA256" },progress,ct).ConfigureAwait(false);
   transfers.ExtractZip(zip,server,ct);File.Delete(zip);
   var bat=Directory.GetFiles(server,"start-tModLoaderServer.bat",SearchOption.AllDirectories).FirstOrDefault();if(bat==null)throw new InvalidDataException("tModLoader 发布包缺少 Windows 服务端启动入口。");
   var working=Path.GetDirectoryName(bat);var runtimeConfig=Path.Combine(working,"tModLoader.runtimeconfig.json");
   if(File.Exists(runtimeConfig))
   {
    var options=JObject.Parse(File.ReadAllText(runtimeConfig))["runtimeOptions"];var framework=options?["framework"]??options?["frameworks"]?.First;
    Version v;if(Version.TryParse((string)framework?["version"],out v)&&PathSafety.IsWindows&&Environment.OSVersion.Version.Major<10&&v.Major>=7)
     throw new PlatformNotSupportedException("此 tModLoader 发布要求 .NET "+v+"，没有经过验证的 Win7/8.1 支持。请在新 Windows 安装，或通用导入兼容历史版本。");
   }
   await PrepareTModLoaderRuntimeAsync(working,progress,ct).ConfigureAwait(false);
   PrepareTModLoaderLaunchScript(working,bat);
   instance.EntryPoint=Relative(root,bat);instance.WorkingDirectory=Relative(root,working);instance.Runtime=new RuntimeRequirement { Kind=RuntimeKind.Script };instance.RuntimePath="";
   instance.Arguments=new List<string>{"-nosteam","-config","serverconfig.txt","-tmlsavedirectory","."};instance.StopCommand="exit";instance.ReadyPattern="Server started";
   instance.ModDirectory=Relative(root,Path.Combine(working,"Mods"));instance.SourceUrl=(string)asset["browser_download_url"];
   instance.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";WriteTerrariaConfig(request,working);Directory.CreateDirectory(Path.Combine(working,"Mods"));
   if(!File.Exists(Path.Combine(working,"Mods","enabled.json")))File.WriteAllText(Path.Combine(working,"Mods","enabled.json"),"[]",new UTF8Encoding(false));
   progress?.Report(new WorkProgress("tModLoader 已安装，Windows 私有 .NET 按官方发布要求准备；启动仍使用官方游戏脚本。"));
  }
  private async Task PrepareTModLoaderRuntimeAsync(string working,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   if(!PathSafety.IsWindows){progress?.Report(new WorkProgress("当前系统仅检查 tModLoader 安装包；精确私有 .NET 在 Windows 上准备及验证。"));return;}
   var config=Path.Combine(working,"tModLoader.runtimeconfig.json");if(!File.Exists(config))throw new InvalidDataException("tModLoader 发布缺少明确的 .NET 运行时要求。");
   var options=JObject.Parse(File.ReadAllText(config))["runtimeOptions"];var framework=options?["framework"]??options?["frameworks"]?.Children().FirstOrDefault(x=>(string)x["name"]=="Microsoft.NETCore.App");
   Version version;if((string)framework?["name"]!="Microsoft.NETCore.App"||!Version.TryParse((string)framework?["version"],out version))throw new InvalidDataException("无法识别 tModLoader 官方发布要求的精确 .NET 版本。");
   // Official ScriptCaller pins Disable, so a newer servicing runtime is not a
   // substitute. The provider downloads Microsoft's exact, hash-checked ZIP.
   var required=new RuntimeRequirement{Kind=RuntimeKind.DotNet,Framework="Microsoft.NETCore.App",Version=version.ToString(),RollForward="Disable",Architecture="x64"};
   progress?.Report(new WorkProgress("准备 tModLoader 官方要求的精确私有 .NET "+required.Version));
   var runtime=await runtimes.EnsureAsync(required,"",progress,ct).ConfigureAwait(false);var source=Path.GetDirectoryName(runtime.Executable);
   if(!File.Exists(runtime.Executable)||!File.Exists(Path.Combine(source,"dotnet.exe"))||!Directory.Exists(Path.Combine(source,"shared","Microsoft.NETCore.App",required.Version)))throw new InvalidDataException("运行时提供器没有返回 tModLoader 要求的精确 .NET 包。");
   var destination=Path.Combine(working,"dotnet");if(Directory.Exists(destination))throw new InvalidDataException("官方发布已含 dotnet 目录，不能覆盖未知运行时布局。");
   transfers.CopyTree(source,destination,ct);
   progress?.Report(new WorkProgress("已准备精确私有 .NET "+required.Version+"；官方脚本将使用 server/dotnet，保留官方依赖与启动流程。"));
  }
  internal static void PrepareTModLoaderLaunchScript(string working,string script)
  {
   if(!File.Exists(Path.Combine(working,"LaunchUtils","busybox64.exe"))||!File.Exists(Path.Combine(working,"start-tModLoaderServer.sh")))
    throw new InvalidDataException("tModLoader 缺少可托管的 Windows 启动组件，请重新安装官方发布包。");
   // The official busybox-sh.bat uses START, which opens a detached console and
   // loses the manager's redirected stdin/stdout. Run the same official shell
   // synchronously so bootstrap logs and game commands stay on our pipes.
   var original=script+".workbench-original";if(!File.Exists(original))File.Copy(script,original);
   File.WriteAllText(script,"@echo off\r\ncd /D \"%~dp0\"\r\n\"LaunchUtils\\busybox64.exe\" bash ./start-tModLoaderServer.sh %*\r\nexit /b %errorlevel%\r\n",Encoding.ASCII);
  }
  private static void WriteTerrariaConfig(CreateServerRequest r,string directory)
  {
   var name=string.IsNullOrWhiteSpace(r.WorldName)?"World":r.WorldName.Trim();var file=OfficialMetadata.SafePart(name);if(string.IsNullOrWhiteSpace(file))file="World";
   Directory.CreateDirectory(Path.Combine(directory,"Worlds"));
   File.WriteAllText(Path.Combine(directory,"serverconfig.txt"),"world=Worlds/"+file+".wld\nautocreate=1\nworldname="+name+"\nworldpath=Worlds\nmodpath=Mods\nport="+r.Port+"\nmaxplayers="+r.MaxPlayers+"\npassword="+(r.Password??"")+"\nsecure=1\n",new UTF8Encoding(false));
  }
  private static string Relative(string root,string file)=>file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length+1).Replace(Path.DirectorySeparatorChar,'/');
  private static IEnumerable<string> Strings(JToken token)
  {
   if(token is JValue && token.Type==JTokenType.String)yield return (string)token;
   foreach(var child in token.Children())foreach(var value in Strings(child))yield return value;
  }
  private static void ValidateJar(string path)
  {
   using(var archive=ZipFileCompat.Open(path))if(archive.GetEntry("META-INF/MANIFEST.MF")==null)throw new InvalidDataException("下载内容没有可执行 JAR 的 Manifest。");
  }
  private static Task RunInstallerAsync(string executable,IEnumerable<string> arguments,string working,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   return Task.Run(async()=>
   {
    using(var p=new Process { StartInfo=new ProcessStartInfo(executable,WindowsCommandLine.Join(arguments)) { WorkingDirectory=working,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } })
    {
     p.OutputDataReceived+=(s,e)=> {if(e.Data!=null)progress?.Report(new WorkProgress(e.Data));};p.ErrorDataReceived+=(s,e)=> {if(e.Data!=null)progress?.Report(new WorkProgress(e.Data));};
     p.Start();p.BeginOutputReadLine();p.BeginErrorReadLine();
     using(ct.Register(()=>{try{if(!p.HasExited)p.Kill();}catch{}}))
     {
      var until=DateTime.UtcNow.AddMinutes(20);while(!p.WaitForExit(200)){ct.ThrowIfCancellationRequested();if(DateTime.UtcNow>until){try{p.Kill();}catch{}throw new TimeoutException("Forge 安装超过 20 分钟，请检查网络后重试。");}await Task.Yield();}
      p.WaitForExit();ct.ThrowIfCancellationRequested();if(p.ExitCode!=0)throw new InvalidOperationException("Forge 官方安装器返回错误码 "+p.ExitCode+"，请查看安装日志。");
     }
    }
   },ct);
  }
 }
 internal static class ZipFileCompat
 {
  public static ZipArchive Open(string path)=>new ZipArchive(File.OpenRead(path),ZipArchiveMode.Read,false);
 }
}
