using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ServerWorkbench.Core
{
 internal static class OfficialMetadata
 {
  private static readonly HttpClient http=CreateClient();
  private static HttpClient CreateClient()
  {
   var h=new HttpClient { Timeout=TimeSpan.FromSeconds(60) };
   h.DefaultRequestHeaders.UserAgent.ParseAdd("ServerWorkbench/0.1 (+https://github.com/) ");
   return h;
  }
  public static async Task<JToken> GetAsync(string url,CancellationToken ct)
  {
   using(var r=await http.GetAsync(url,ct).ConfigureAwait(false))
   {
    r.EnsureSuccessStatusCode();
    var text=await r.Content.ReadAsStringAsync().ConfigureAwait(false);
    ct.ThrowIfCancellationRequested();
    if(text.Length>12*1024*1024) throw new InvalidDataException("官方版本信息超过预期大小。");
    return JToken.Parse(text);
   }
  }
  public static async Task<string> TextAsync(string url,CancellationToken ct)
  {
   using(var r=await http.GetAsync(url,ct).ConfigureAwait(false)) { r.EnsureSuccessStatusCode();var s=await r.Content.ReadAsStringAsync().ConfigureAwait(false);ct.ThrowIfCancellationRequested();return s; }
  }
  public static string SafePart(string text) => Regex.Replace(text??"",@"[^A-Za-z0-9._+-]","_");
 }

 public sealed class RuntimeProvider : IRuntimeProvider
 {
  private readonly AppPaths paths;private readonly ITransferService transfers;
  private readonly SemaphoreSlim installLock=new SemaphoreSlim(1,1);
  public RuntimeProvider(AppPaths paths,ITransferService transfers) { this.paths=paths;this.transfers=transfers; }
  public async Task<RuntimeResolution> EnsureAsync(RuntimeRequirement requirement,string existingPath,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   requirement=requirement??new RuntimeRequirement();
   if(requirement.Kind==RuntimeKind.Native || requirement.Kind==RuntimeKind.Script) return new RuntimeResolution { Executable=existingPath??"",Description="使用原生程序或服务端自带启动脚本" };
   if(!string.IsNullOrWhiteSpace(existingPath)) return await ValidateExistingAsync(requirement,existingPath,ct).ConfigureAwait(false);
   if(PathSafety.IsWindows && Environment.OSVersion.Version.Major<10)
    throw new InvalidOperationException("Win7/8.1 不会自动安装未经验证的运行时。请在环境配置中选择已兼容的 java.exe / dotnet.exe，或导入兼容运行时包。");
   await installLock.WaitAsync(ct).ConfigureAwait(false);
   try
   {
    if(requirement.Kind==RuntimeKind.Java) return await EnsureJavaAsync(requirement,progress,ct).ConfigureAwait(false);
    if(requirement.Kind==RuntimeKind.DotNet) return await EnsureDotNetAsync(requirement,progress,ct).ConfigureAwait(false);
    throw new NotSupportedException("此运行时需要手动配置。");
   }
   finally { installLock.Release(); }
  }
  public static bool DotNetVersionMatches(string installed,string requested,string rollForward)
  {
   Version have,want;if(!Version.TryParse(installed,out have)||!Version.TryParse(requested,out want)) return false;
   var policy=(rollForward??"Minor").ToLowerInvariant();
   if(policy=="disable") return have==want;
   if(have<want) return false;
   if(policy=="latestmajor"||policy=="major") return true;
   if(have.Major!=want.Major) return false;
   if(policy=="latestminor"||policy=="minor") return true;
   return have.Minor==want.Minor;
  }
  private async Task<RuntimeResolution> ValidateExistingAsync(RuntimeRequirement requirement,string path,CancellationToken ct)
  {
   path=Path.GetFullPath(path);
   if(Directory.Exists(path))
   {
    var file=requirement.Kind==RuntimeKind.Java?Path.Combine(path,"bin","java.exe"):Path.Combine(path,"dotnet.exe");
    if(!File.Exists(file) && requirement.Kind==RuntimeKind.Java) file=Path.Combine(path,"java.exe");path=file;
   }
   if(!File.Exists(path)) throw new FileNotFoundException("找不到指定运行时。",path);
   ValidateArchitecture(path,requirement.Architecture);
   if(!PathSafety.IsWindows) return new RuntimeResolution { Executable=path,Description="已找到 Windows 运行时；版本需在 Windows 上验证" };
   if(requirement.Kind==RuntimeKind.Java)
   {
    var output=await ProbeAsync(path,"-version",ct).ConfigureAwait(false);
    var m=Regex.Match(output,"(?:java|openjdk) version \"(?<v>[0-9]+)(?:\\.(?<minor>[0-9]+))?",RegexOptions.IgnoreCase);
    if(!m.Success) throw new InvalidDataException("无法识别所选 Java 的版本。");
    var major=int.Parse(m.Groups["v"].Value);if(major==1)major=int.Parse(m.Groups["minor"].Value);
    if(requirement.JavaMajor>0 && major!=requirement.JavaMajor) throw new InvalidDataException("所选 Java 是 "+major+"，此组合要求 Java "+requirement.JavaMajor+"。请为实例选择对应版本。");
   }
   else
   {
    var output=await ProbeAsync(path,"--list-runtimes",ct).ConfigureAwait(false);
    var found=output.Split('\n').Any(line=> {var p=line.Trim().Split(' ');return p.Length>=2 && p[0]==requirement.Framework && DotNetVersionMatches(p[1],requirement.Version,requirement.RollForward);});
    if(!found) throw new InvalidDataException("所选 dotnet 未包含兼容的 "+requirement.Framework+" "+requirement.Version+"。");
   }
   return new RuntimeResolution { Executable=path,Description="已验证现有运行时" };
  }
  private async Task<RuntimeResolution> EnsureJavaAsync(RuntimeRequirement req,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   if(req.JavaMajor<8 || req.JavaMajor>40) throw new InvalidDataException("缺少可信的 Java 主版本要求，请手动指定运行时。");
   if(req.Architecture!="x64" && req.Architecture!="x86") throw new NotSupportedException("首版仅自动准备 Windows x64/x86 Java。");
   var arch=req.Architecture=="x86"?"x32":"x64";
   progress?.Report(new WorkProgress("查询 Temurin Java "+req.JavaMajor+" 官方下载信息"));
   var data=await OfficialMetadata.GetAsync("https://api.adoptium.net/v3/assets/latest/"+req.JavaMajor+"/hotspot?architecture="+arch+"&image_type=jdk&os=windows&vendor=eclipse",ct).ConfigureAwait(false);
   var asset=data.Children().FirstOrDefault(x=>x["binary"]?["package"]?["name"]?.ToString().EndsWith(".zip",StringComparison.OrdinalIgnoreCase)==true);
   if(asset==null) throw new InvalidDataException("该 Java/架构没有官方 ZIP 包，请选择已有运行时。");
   var package=asset["binary"]["package"];
   var folder=Path.Combine(paths.Runtimes,"Java",OfficialMetadata.SafePart(asset["release_name"]+"-"+req.Architecture));
   var marker=Path.Combine(folder,"runtime.complete");
   if(File.Exists(marker))
   {
    var existing=Directory.GetFiles(folder,"java.exe",SearchOption.AllDirectories).FirstOrDefault(x=>Path.GetFileName(Path.GetDirectoryName(x))=="bin");
    if(existing!=null)return await ValidateExistingAsync(req,existing,ct).ConfigureAwait(false);
   }
   var staging=Path.Combine(paths.Temp,"java-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
   try
   {
    var archive=Path.Combine(staging,"java.zip");
    await transfers.DownloadAsync(new DownloadRequest { Url=(string)package["link"],Destination=archive,Hash=(string)package["checksum"],HashAlgorithm="SHA256" },progress,ct).ConfigureAwait(false);
    var extracted=Path.Combine(staging,"files");transfers.ExtractZip(archive,extracted,ct);
    var java=Directory.GetFiles(extracted,"java.exe",SearchOption.AllDirectories).FirstOrDefault(x=>Path.GetFileName(Path.GetDirectoryName(x))=="bin");
    if(java==null)throw new InvalidDataException("Java 下载包中没有 bin/java.exe。");
    ValidateArchitecture(java,req.Architecture);
    var relative=java.Substring(extracted.Length+1);File.WriteAllText(Path.Combine(extracted,"runtime.complete"),(string)asset["release_name"]);
    Directory.CreateDirectory(Path.GetDirectoryName(folder));if(Directory.Exists(folder))Directory.Delete(folder,true);Directory.Move(extracted,folder);
    return await ValidateExistingAsync(req,Path.Combine(folder,relative),ct).ConfigureAwait(false);
   }
   finally { if(Directory.Exists(staging))Directory.Delete(staging,true); }
  }
  private async Task<RuntimeResolution> EnsureDotNetAsync(RuntimeRequirement req,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   Version version;if(!Version.TryParse(req.Version,out version)||version.Major<6)throw new InvalidDataException("自动安装需要明确的现代 .NET 版本（6 或更高）。");
   if(req.Architecture!="x64" && req.Architecture!="x86")throw new NotSupportedException("不支持此 .NET 架构。");
   if(req.Framework!="Microsoft.NETCore.App"&&req.Framework!="Microsoft.WindowsDesktop.App"&&req.Framework!="Microsoft.AspNetCore.App")throw new NotSupportedException("此 .NET 框架需要手动准备。");
   progress?.Report(new WorkProgress("查询 Microsoft .NET 官方发行元数据"));
   var channel=version.Major+"."+version.Minor;
   var data=await OfficialMetadata.GetAsync("https://builds.dotnet.microsoft.com/dotnet/release-metadata/"+channel+"/releases.json",ct).ConfigureAwait(false);
   var family=req.Framework=="Microsoft.WindowsDesktop.App"?"windowsdesktop":req.Framework=="Microsoft.AspNetCore.App"?"aspnetcore-runtime":"runtime";
   var candidates=data["releases"].Children().Where(x=>DotNetVersionMatches((string)x[family]?["version"],req.Version,req.RollForward)).ToList();
   // Prefer the smallest compatible minor and the newest servicing patch in that minor.
   var release=candidates.OrderBy(x=>Version.Parse((string)x[family]["version"]).Major).ThenBy(x=>Version.Parse((string)x[family]["version"]).Minor).ThenByDescending(x=>Version.Parse((string)x[family]["version"])).FirstOrDefault();
   if(release==null)throw new InvalidDataException("官方未提供满足此版本要求的 .NET 运行时。");
   var selected=(string)release[family]["version"];
   var folder=Path.Combine(paths.Runtimes,"DotNet",OfficialMetadata.SafePart(family+"-"+selected+"-"+req.Architecture));
   if(File.Exists(Path.Combine(folder,"runtime.complete")))return await ValidateExistingAsync(req,Path.Combine(folder,"dotnet.exe"),ct).ConfigureAwait(false);
   var staging=Path.Combine(paths.Temp,"dotnet-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
   try
   {
    var extracted=Path.Combine(staging,"files");Directory.CreateDirectory(extracted);
    var groups=new List<string>{"runtime"};if(family!="runtime")groups.Add(family);
    foreach(var group in groups)
    {
     var file=release[group]?["files"]?.Children().FirstOrDefault(x=>(string)x["rid"]=="win-"+req.Architecture && ((string)x["url"]??"").EndsWith(".zip",StringComparison.OrdinalIgnoreCase));
     if(file==null)throw new InvalidDataException("所需 .NET 框架没有可用的 Windows ZIP 包。");
     var archive=Path.Combine(staging,group+".zip");await transfers.DownloadAsync(new DownloadRequest { Url=(string)file["url"],Destination=archive,Hash=(string)file["hash"],HashAlgorithm="SHA512" },progress,ct).ConfigureAwait(false);
     var part=Path.Combine(staging,group);transfers.ExtractZip(archive,part,ct);MergeRuntime(part,extracted,ct);
    }
    if(!File.Exists(Path.Combine(extracted,"dotnet.exe")))throw new InvalidDataException(".NET 包中缺少 dotnet.exe。");
    File.WriteAllText(Path.Combine(extracted,"runtime.complete"),selected);Directory.CreateDirectory(Path.GetDirectoryName(folder));if(Directory.Exists(folder))Directory.Delete(folder,true);Directory.Move(extracted,folder);
    return await ValidateExistingAsync(req,Path.Combine(folder,"dotnet.exe"),ct).ConfigureAwait(false);
   }
   finally {if(Directory.Exists(staging))Directory.Delete(staging,true);}
  }
  private static void MergeRuntime(string source,string destination,CancellationToken ct)
  {
   foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories))
   {
    ct.ThrowIfCancellationRequested();var target=PathSafety.ResolveUnder(destination,file.Substring(source.Length+1));Directory.CreateDirectory(Path.GetDirectoryName(target));File.Copy(file,target,true);
   }
  }
  public static void ValidateArchitecture(string executable,string architecture)
  {
   using(var f=File.OpenRead(executable))using(var reader=new BinaryReader(f))
   {
    if(f.Length<64 || reader.ReadUInt16()!=0x5a4d)throw new InvalidDataException("所选运行时不是 Windows 可执行程序。");
    f.Position=0x3c;int offset=reader.ReadInt32();if(offset<0||offset>f.Length-6)throw new InvalidDataException("Windows PE 头无效。");
    f.Position=offset;if(reader.ReadUInt32()!=0x4550)throw new InvalidDataException("Windows PE 签名无效。");
    int machine=reader.ReadUInt16();if((architecture=="x64"&&machine!=0x8664)||(architecture=="x86"&&machine!=0x14c))throw new InvalidDataException("运行时架构与服务端要求不一致。");
   }
  }
  private static Task<string> ProbeAsync(string executable,string arguments,CancellationToken ct)
  {
   return Task.Run(()=>
   {
    using(var p=new Process { StartInfo=new ProcessStartInfo(executable,arguments) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true } })
    {
     p.Start();var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
     using(ct.Register(()=>{try{if(!p.HasExited)p.Kill();}catch{}}))
     {
      if(!p.WaitForExit(15000)){try{p.Kill();}catch{}throw new TimeoutException("运行时版本检测超时。");}
      ct.ThrowIfCancellationRequested();Task.WaitAll(stdout,stderr);if(p.ExitCode!=0)throw new InvalidDataException("运行时检测失败："+stderr.Result);return stdout.Result+"\n"+stderr.Result;
     }
    }
   },ct);
  }
 }
}
