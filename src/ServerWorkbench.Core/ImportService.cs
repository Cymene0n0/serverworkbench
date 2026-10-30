using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace ServerWorkbench.Core
{
 public sealed class ImportService : IImportService
 {
  private readonly AppPaths paths; private readonly IInstanceRepository repository;private readonly ITransferService transfers;private readonly IRuntimeProvider runtimes;
  public ImportService(AppPaths paths,IInstanceRepository repository,ITransferService transfers,IRuntimeProvider runtimes) { this.paths=paths;this.repository=repository;this.transfers=transfers;this.runtimes=runtimes; }
  public async Task<ImportDraft> StageAsync(string source,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   if(string.IsNullOrWhiteSpace(source))throw new InvalidDataException("请选择文件、目录或 HTTPS 文件直链。");
   var stage=Path.Combine(paths.Temp,Guid.NewGuid().ToString("N"));var payload=Path.Combine(stage,"payload");Directory.CreateDirectory(payload);
   try {
    string package=source;
    if(Uri.TryCreate(source,UriKind.Absolute,out var uri)&&uri.Scheme=="https") {
     var name=Uri.UnescapeDataString(uri.Segments.LastOrDefault()??"download");
     try{PathSafety.EnsureRelative(name);}catch{name="download";}if(string.IsNullOrEmpty(name))name="download";
     package=Path.Combine(stage,name);
     await transfers.DownloadAsync(new DownloadRequest {Url=source,Destination=package},progress,ct).ConfigureAwait(false);
    }
    else if(!Directory.Exists(source)&&!File.Exists(source))throw new FileNotFoundException("找不到源文件；网址必须是 HTTPS 直接下载地址。");
    await Task.Run(()=> {
     ct.ThrowIfCancellationRequested();
     if(Directory.Exists(package))transfers.CopyTree(package,payload,ct);
     else {
      PathSafety.RejectReparse(package);var ext=Path.GetExtension(package).ToLowerInvariant();
      var isZip=IsZip(package);
      if(ext==".jar" || (isZip&&HasMainClass(package)))File.Copy(package,Path.Combine(payload,ext==".jar"?Path.GetFileName(package):"server.jar"));
      else if(ext==".zip"||isZip)transfers.ExtractZip(package,payload,ct);
      else if(new[]{".exe",".bat",".cmd"}.Contains(ext))File.Copy(package,Path.Combine(payload,Path.GetFileName(package)));
      else throw new InvalidDataException("首版支持 ZIP、目录、EXE、BAT/CMD 和可执行 JAR。安装器请先手动安装，再导入其服务端目录。");
     }
    },ct).ConfigureAwait(false);
    progress?.Report(new WorkProgress("分析启动入口与运行环境"));
    var draft=await Task.Run(()=>AnalyzeDirectory(payload,ct),ct).ConfigureAwait(false);draft.Source=source;return draft;
   } catch { if(Directory.Exists(stage))Directory.Delete(stage,true);throw; }
  }
  public static ImportDraft AnalyzeDirectory(string root,CancellationToken ct=default(CancellationToken))
  {
   var draft=new ImportDraft { StagingDirectory=root };
   foreach(var file in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)) {
    ct.ThrowIfCancellationRequested();var relative=file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length+1);var ext=Path.GetExtension(file).ToLowerInvariant();
    if(ext==".jar") {
     try {
      using(var zip=ZipFile.OpenRead(file)) {
       var manifest=zip.GetEntry("META-INF/MANIFEST.MF");string text="";if(manifest!=null)using(var r=new StreamReader(manifest.Open()))text=r.ReadToEnd();
       if(text.IndexOf("Main-Class:",StringComparison.OrdinalIgnoreCase)<0)continue;
       int major=0;bool preview=false;
       foreach(var e in zip.Entries.Where(x=>x.FullName.EndsWith(".class")&&!x.FullName.StartsWith("META-INF/versions/",StringComparison.OrdinalIgnoreCase))) {
        using(var s=e.Open()){var b=new byte[8];if(s.Read(b,0,8)==8&&b[0]==0xCA&&b[1]==0xFE&&b[2]==0xBA&&b[3]==0xBE){major=Math.Max(major,b[6]*256+b[7]);preview|=b[4]==255&&b[5]==255;}}
       }
       draft.Candidates.Add(new EntryCandidate { RelativePath=relative,Description=preview?"预览版字节码：需要手动配置对应 Java 与 --enable-preview":"可执行 JAR；Java "+Math.Max(8,major-44)+" 或适配版本",Compatible=!preview,Runtime=new RuntimeRequirement {Kind=RuntimeKind.Java,JavaMajor=Math.Max(8,major-44)} });
      }
     }catch(InvalidDataException){draft.Notes.Add("无法读取 JAR："+relative);}
    }
    else if(ext==".exe") {
     var candidate=new EntryCandidate { RelativePath=relative,Description=ReadPe(file,out var compatible),Compatible=compatible };
     if(candidate.Description=="Windows x86 程序")candidate.Runtime.Architecture="x86";
     if(Path.GetFileNameWithoutExtension(file).ToLowerInvariant().StartsWith("setup")||Path.GetFileNameWithoutExtension(file).ToLowerInvariant().StartsWith("unins")){candidate.Description+="；疑似安装器，请先安装后导入服务端目录";candidate.Compatible=false;}
     var runtimeConfig=Path.ChangeExtension(file,".runtimeconfig.json");
     if(File.Exists(runtimeConfig))SetDotNetRequirement(candidate,runtimeConfig);
     var config=file+".config";if(File.Exists(config))try{var runtime=XDocument.Load(config).Descendants("supportedRuntime").FirstOrDefault();if(runtime!=null)candidate.Description+="；"+(string)runtime.Attribute("sku");}catch{draft.Notes.Add("无法读取配置："+relative+".config");}
     draft.Candidates.Add(candidate);
    }
    else if(ext==".dll"&&File.Exists(Path.ChangeExtension(file,".runtimeconfig.json"))) {
     var candidate=new EntryCandidate { RelativePath=relative,Description=".NET 程序集" };SetDotNetRequirement(candidate,Path.ChangeExtension(file,".runtimeconfig.json"));draft.Candidates.Add(candidate);
    }
    else if(ext==".bat"||ext==".cmd")draft.Candidates.Add(new EntryCandidate {RelativePath=relative,Description="Windows 启动脚本（仅分析，尚未执行）",Runtime=new RuntimeRequirement {Kind=RuntimeKind.Script}});
    else if(ext==".sh")draft.Notes.Add("发现 Shell 脚本："+relative+"；需选择 Windows 启动入口。");
    else if(ext==""&&IsElf(file))draft.Candidates.Add(new EntryCandidate {RelativePath=relative,Description="Linux ELF 程序，不能在原生 Windows 运行",Compatible=false});
   }
   draft.Candidates=draft.Candidates.OrderByDescending(x=>x.Compatible).ThenBy(x=>x.RelativePath.Length).ToList();
   if(draft.Candidates.Count==0)draft.Notes.Add("未找到可识别的入口；请核对是否为 Windows 专服包，或手动填写入口和运行环境。");
   return draft;
  }
  private static void SetDotNetRequirement(EntryCandidate candidate,string path)
  {
   try { var options=JObject.Parse(File.ReadAllText(path))["runtimeOptions"];var frameworks=options?["frameworks"]?.Children().ToList()??new List<JToken>();if(options?["framework"]!=null)frameworks.Add(options["framework"]);
    // Desktop/ASP.NET packages include the base runtime; selecting the first base declaration would miss them.
    var framework=frameworks.OrderByDescending(x=>(string)x["name"]=="Microsoft.WindowsDesktop.App"?2:(string)x["name"]=="Microsoft.AspNetCore.App"?1:0).FirstOrDefault();
    if(framework!=null){candidate.Runtime=new RuntimeRequirement {Kind=RuntimeKind.DotNet,Architecture=candidate.Runtime.Architecture,Version=(string)framework["version"]??"",Framework=(string)framework["name"]??"Microsoft.NETCore.App",RollForward=(string)options["rollForward"]??"Minor"};candidate.Description+="；"+candidate.Runtime.Framework+" "+candidate.Runtime.Version;}
    else candidate.Description+="；可能已包含自带 .NET 运行时";
   }catch(Exception e){throw new InvalidDataException("无法读取 .NET 运行时配置："+path,e);}
  }
  private static bool IsZip(string file){using(var s=File.OpenRead(file)){return s.ReadByte()=='P'&&s.ReadByte()=='K';}}
  private static bool HasMainClass(string file){try{using(var z=ZipFile.OpenRead(file)){var m=z.GetEntry("META-INF/MANIFEST.MF");if(m==null)return false;using(var r=new StreamReader(m.Open()))return r.ReadToEnd().IndexOf("Main-Class:",StringComparison.OrdinalIgnoreCase)>=0;}}catch{return false;}}
  private static bool IsElf(string file){using(var s=File.OpenRead(file)){return s.ReadByte()==0x7F&&s.ReadByte()=='E'&&s.ReadByte()=='L'&&s.ReadByte()=='F';}}
  private static string ReadPe(string file,out bool compatible)
  {
   compatible=false;try{using(var r=new BinaryReader(File.OpenRead(file))){if(r.ReadUInt16()!=0x5A4D)return "不是有效 Windows PE 文件";r.BaseStream.Position=0x3C;int offset=r.ReadInt32();if(offset<0||offset>r.BaseStream.Length-6)return "PE 文件损坏";r.BaseStream.Position=offset;if(r.ReadUInt32()!=0x4550)return "PE 文件损坏";var machine=r.ReadUInt16();compatible=machine==0x14C||machine==0x8664;return machine==0x14C?"Windows x86 程序":machine==0x8664?"Windows x64 程序":machine==0xAA64?"Windows ARM64：当前 x64 产品不兼容":"未知 PE 架构";}}catch{return "无法读取 Windows 程序架构";}
  }
  public async Task<ServerInstance> CommitAsync(ImportDraft draft,ServerInstance instance,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   InstanceRepository.Validate(instance);ValidateDraft(draft);var entry=PathSafety.ResolveUnder(draft.StagingDirectory,instance.EntryPoint);
   if(!File.Exists(entry))throw new FileNotFoundException("启动入口不存在，请从候选中选择或填写相对路径。");
   var candidate=draft.Candidates.FirstOrDefault(x=>string.Equals(x.RelativePath,instance.EntryPoint,StringComparison.OrdinalIgnoreCase));
   if(candidate!=null&&!candidate.Compatible)throw new InvalidDataException("该入口需要其他系统或手动适配，请选择兼容的 Windows 服务端入口。");
   if(instance.Runtime.Kind==RuntimeKind.Java||instance.Runtime.Kind==RuntimeKind.DotNet) {
    var resolution=await runtimes.EnsureAsync(instance.Runtime,instance.RuntimePath,progress,ct).ConfigureAwait(false);instance.RuntimePath=resolution.Executable;
   }
   ct.ThrowIfCancellationRequested();var target=paths.InstanceDirectory(instance);if(Directory.Exists(target)||File.Exists(target))throw new InvalidDataException("实例目录已存在，未覆盖任何内容。");
   if(Directory.Exists(draft.Source)) {
    var sourceRoot=Path.GetFullPath(draft.Source).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
    if(Path.GetFullPath(target).StartsWith(sourceRoot,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("实例存放目录不能位于导入源目录内部，请选择其他父目录以保留源数据。");
   }
   instance.SourceUrl=draft.Source;InstanceRepository.WriteStorageIdentity(draft.StagingDirectory,instance);
   bool moved=InstanceDeployment.Commit(draft.StagingDirectory,target,transfers,ct);
   try { repository.Save(instance); }catch { if(moved)Directory.Move(target,draft.StagingDirectory);else if(Directory.Exists(target))Directory.Delete(target,true);throw; }
   Discard(draft);return instance;
  }
  private void ValidateDraft(ImportDraft draft){if(draft==null||string.IsNullOrEmpty(draft.StagingDirectory)||!Path.GetFullPath(draft.StagingDirectory).StartsWith(Path.GetFullPath(paths.Temp).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("暂存目录不属于管理器。");}
  public void Discard(ImportDraft draft){ValidateDraft(draft);var parent=Path.GetDirectoryName(draft.StagingDirectory);if(Directory.Exists(parent))Directory.Delete(parent,true);}
 }
}
