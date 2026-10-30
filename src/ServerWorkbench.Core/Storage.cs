using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace ServerWorkbench.Core
{
 public static class PathSafety
 {
  public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  public static string EnsureRelative(string path)
  {
   if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("路径不能为空。");
   path=path.Replace('\\','/');
   if(path.StartsWith("/") || path.IndexOf(':')>=0 || path.IndexOf('\0')>=0) throw new InvalidDataException("不允许绝对路径或盘符："+path);
   foreach(var p in path.Split('/'))
   {
    if(p==".." || p.EndsWith(" ") || (p.EndsWith(".") && p!=".")) throw new InvalidDataException("不安全的相对路径："+path);
    if(p.IndexOfAny(new[]{'<','>','"','|','?','*'})>=0 || p.Any(c=>c<32)) throw new InvalidDataException("Windows 不支持此路径："+path);
    if(Regex.IsMatch(p.Split('.')[0],@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",RegexOptions.IgnoreCase)) throw new InvalidDataException("Windows 保留文件名："+path);
   }
   return path.Replace('/',Path.DirectorySeparatorChar);
  }
  public static string ResolveUnder(string root,string relative)
  {
   var safe=EnsureRelative(relative); var full=Path.GetFullPath(Path.Combine(root,safe));
   var basePath=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(full!=basePath.TrimEnd(Path.DirectorySeparatorChar) && !full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径越过实例目录。");
   return full;
  }
  public static void RejectReparse(string path)
  {
   if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("不导入符号链接或重解析点："+path);
  }
  public static string HashFile(string path,string algorithm)
  {
   using(var h=NewHash(algorithm)) using(var s=File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();
  }
  public static HashAlgorithm NewHash(string algorithm)
  {
   switch((algorithm??"").Replace("-","").ToUpperInvariant())
   { case "SHA1": return SHA1.Create(); case "SHA256":return SHA256.Create();case "SHA512":return SHA512.Create();default:throw new InvalidDataException("不支持的哈希算法。"); }
  }
 }
 public static class AtomicJson
 {
  public static void Write(string path,object value)
  {
   // Framework file APIs need an extended path even on older Windows where the
   // Windows 10 longPathAware manifest setting is unavailable. Keep this internal;
   // game command lines and persisted paths retain their ordinary user-facing form.
   path=IoPath(path);var directory=Path.GetDirectoryName(path);Directory.CreateDirectory(directory);
   var temporary=Path.Combine(directory,"~"+Guid.NewGuid().ToString("N").Substring(0,16)+".tmp");bool owned=false;
   try {
    using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
     owned=true;using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(JsonConvert.SerializeObject(value,Formatting.Indented));
    }
    if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
   } finally { if(owned&&File.Exists(temporary)) File.Delete(temporary); }
  }
  private static string IoPath(string path)
  {
   var full=Path.GetFullPath(path);if(!PathSafety.IsWindows||full.StartsWith(@"\\?\",StringComparison.Ordinal))return full;
   return full.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+full.Substring(2):@"\\?\"+full;
  }

 }
 public sealed class InstanceRepository : IInstanceRepository
 {
  public const string StorageMarker=".workbench-instance.json";
  private readonly AppPaths paths; private readonly object gate=new object();
  public InstanceRepository(AppPaths paths) { this.paths=paths; Directory.CreateDirectory(paths.Instances); }
  public IReadOnlyList<ServerInstance> List()
  {
   lock(gate) {
    var result=new List<ServerInstance>();
    foreach(var directory in Directory.GetDirectories(paths.Instances))
    {
     var file=Path.Combine(directory,"instance.json"); if(!File.Exists(file)) continue;
     try { var s=JsonConvert.DeserializeObject<ServerInstance>(File.ReadAllText(file)); Validate(s); if(Path.GetFileName(directory)!=s.Id) throw new InvalidDataException("实例 ID 与目录不一致。");if(!string.IsNullOrEmpty(s.StorageDirectory)&&Directory.Exists(paths.InstanceDirectory(s)))RequireManagedStorage(paths,s);result.Add(s); }
     catch(Exception e) { throw new InvalidDataException("实例配置损坏，请保留文件并修复："+file,e); }
    }
    return result.OrderByDescending(x=>x.CreatedUtc).ToList();
   }
  }
  public static void Validate(ServerInstance s)
  {
   if(s==null || !Regex.IsMatch(s.Id??"",@"^[a-fA-F0-9]{32}$")) throw new InvalidDataException("无效的实例 ID。");
   s.StorageDirectory=NormalizeStorageDirectory(s.StorageDirectory);
   if(s.SchemaVersion!=1) throw new InvalidDataException("此配置版本需要更新的管理器。");
   if(string.IsNullOrWhiteSpace(s.Name) || s.Name.Length>100) throw new InvalidDataException("名称需要 1–100 个字符。");
   if(s.Port<0 || s.Port>65535) throw new InvalidDataException("端口应为 1–65535，或 0（未设置）。");
   if(s.MemoryMb<256||s.MemoryMb>1048576)throw new InvalidDataException("内存上限需为 256MB–1TB。");
   if(s.AutoSaveMinutes<0||s.AutoSaveMinutes>1440)throw new InvalidDataException("定时保存间隔需为 0–1440 分钟。");
   if(s.Save!=null){foreach(var path in s.Save.Paths??new List<string>())PathSafety.EnsureRelative(path);if(new[]{s.Save.Command,s.Save.PauseCommand,s.Save.ResumeCommand,s.Save.EmergencyCommand,s.StopCommand}.Any(x=>x!=null&&x.IndexOfAny(new[]{'\r','\n'})>=0))throw new InvalidDataException("保存和停止命令必须各为一行。");}
   if(!string.IsNullOrEmpty(s.EntryPoint)) PathSafety.EnsureRelative(s.EntryPoint);
   PathSafety.EnsureRelative(s.WorkingDirectory); PathSafety.EnsureRelative(s.ModDirectory);
   if(s.ModDirectory==".") throw new InvalidDataException("Mod 目录不能是整个实例根目录。");
   if(s.Arguments==null || s.Arguments.Any(x=>x==null || x.IndexOf('\0')>=0)) throw new InvalidDataException("无效启动参数。");
   if(s.Environment==null || s.Environment.Any(x=>string.IsNullOrWhiteSpace(x.Key) || x.Key.Contains("=") || x.Key.IndexOf('\0')>=0 || (x.Value??"").IndexOf('\0')>=0)) throw new InvalidDataException("无效环境变量。");
   if(s.Runtime==null) s.Runtime=new RuntimeRequirement(); if(s.Mods==null) s.Mods=new List<InstalledMod>();
  }
  public static string NormalizeStorageDirectory(string directory)
  {
   if(string.IsNullOrWhiteSpace(directory))return "";
   if(!Path.IsPathRooted(directory)||(PathSafety.IsWindows&&!Regex.IsMatch(directory,@"^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+[\\/])")))throw new InvalidDataException("服务器存放路径必须是完整绝对目录。");
   if(directory.StartsWith(@"\\?\",StringComparison.Ordinal)||directory.StartsWith(@"\\.\",StringComparison.Ordinal))throw new InvalidDataException("请选择普通文件夹路径，不能使用设备路径。");
   var full=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);var root=Path.GetPathRoot(Path.GetFullPath(directory)).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
   if(full.Equals(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("不能将磁盘或共享根目录作为服务器实例目录。");
   return full;
  }
  private static bool UnderOrSame(string path,string parent)=>path.Equals(parent,StringComparison.OrdinalIgnoreCase)||path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
  private static void RejectReparseParents(string directory)
  {for(var current=directory;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current)){if(Directory.Exists(current))PathSafety.RejectReparse(current);var parent=Path.GetDirectoryName(current);if(parent==current)break;}}
  private static bool HasStorageIdentity(string directory,string id)
  {
   var marker=Path.Combine(directory,StorageMarker);if(!File.Exists(marker))return false;PathSafety.RejectReparse(marker);
   try{var identity=JsonConvert.DeserializeObject<StorageIdentity>(File.ReadAllText(marker));return identity!=null&&identity.SchemaVersion==1&&identity.InstanceId==id;}catch(JsonException){return false;}
  }
  public static void WriteStorageIdentity(string directory,ServerInstance instance)
  {
   // Callers use this only on a payload they own, before publishing a new instance.
   Validate(instance);directory=NormalizeStorageDirectory(directory);RejectReparseParents(directory);Directory.CreateDirectory(directory);
   AtomicJson.Write(Path.Combine(directory,StorageMarker),new StorageIdentity{InstanceId=instance.Id});AtomicJson.Write(Path.Combine(directory,"instance.json"),instance);
  }
  private static void ValidateStorageLocation(AppPaths paths,ServerInstance instance)
  {
   var target=paths.InstanceDirectory(instance);if(string.IsNullOrEmpty(instance.StorageDirectory))return;
   var data=Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar);if(UnderOrSame(data,target))throw new InvalidDataException("实例目录不能覆盖管理器数据目录或其上级目录。");
   foreach(var reserved in new[]{paths.Instances,paths.Backups,paths.Runtimes,paths.Temp,Path.Combine(paths.Root,"SaveSnapshots")})if(UnderOrSame(target,Path.GetFullPath(reserved))||UnderOrSame(Path.GetFullPath(reserved),target))throw new InvalidDataException("服务器目录不能与管理器索引、运行时、临时目录或备份目录重叠。");
   RejectReparseParents(target);if(File.Exists(target))throw new InvalidDataException("服务器存放路径已是文件。");
   if(Directory.Exists(paths.Instances))foreach(var directory in Directory.GetDirectories(paths.Instances)) {
    var file=Path.Combine(directory,"instance.json");if(!File.Exists(file))continue;var other=JsonConvert.DeserializeObject<ServerInstance>(File.ReadAllText(file));if(other==null||other.Id==instance.Id)continue;Validate(other);var existing=paths.InstanceDirectory(other);
    if(UnderOrSame(target,existing)||UnderOrSame(existing,target))throw new InvalidDataException("服务器存放目录与其他已管理实例重叠。");
   }
  }
  public static void RequireManagedStorage(AppPaths paths,ServerInstance instance)
  {
   Validate(instance);ValidateStorageLocation(paths,instance);var target=paths.InstanceDirectory(instance);RejectReparseParents(target);
   if(string.IsNullOrEmpty(instance.StorageDirectory)){var file=Path.Combine(target,"instance.json");if(!File.Exists(file)||JsonConvert.DeserializeObject<ServerInstance>(File.ReadAllText(file))?.Id!=instance.Id)throw new InvalidDataException("实例目录身份不匹配，已保留文件。");if(File.Exists(Path.Combine(target,StorageMarker))&&!HasStorageIdentity(target,instance.Id))throw new InvalidDataException("实例目录标记不匹配，已保留文件。");}
   else if(!HasStorageIdentity(target,instance.Id))throw new InvalidDataException("外部实例目录缺少匹配的管理标记，已保留文件。");
  }
  public void Save(ServerInstance instance)
  {
   lock(gate){Validate(instance);var index=paths.InstanceIndexDirectory(instance);var indexFile=Path.Combine(index,"instance.json");
    if(File.Exists(indexFile)){var previous=JsonConvert.DeserializeObject<ServerInstance>(File.ReadAllText(indexFile));if(previous?.Id==instance.Id&&!NormalizeStorageDirectory(previous.StorageDirectory).Equals(instance.StorageDirectory,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("已有实例不能仅修改路径来搬家；请复制实例或重新导入。");}
    ValidateStorageLocation(paths,instance);var target=paths.InstanceDirectory(instance);
    if(!string.IsNullOrEmpty(instance.StorageDirectory)){
     if(Directory.Exists(target)&&Directory.EnumerateFileSystemEntries(target).Any()&&!HasStorageIdentity(target,instance.Id))throw new InvalidDataException("所选目录已有其他内容，不能覆盖或接管。请选择新的独立实例目录。");
     WriteStorageIdentity(target,instance);Directory.CreateDirectory(index);AtomicJson.Write(indexFile,instance);
    }else{if(File.Exists(Path.Combine(target,StorageMarker))&&!HasStorageIdentity(target,instance.Id))throw new InvalidDataException("目标目录包含其他实例标记，请先在新副本中移除旧标记。");Directory.CreateDirectory(target);AtomicJson.Write(indexFile,instance);}
   }
  }
  public void Delete(string id)
  {
   if(!Regex.IsMatch(id??"",@"^[a-fA-F0-9]{32}$"))throw new InvalidDataException("无效实例 ID。");
   lock(gate){var index=Path.Combine(paths.Instances,id);var file=Path.Combine(index,"instance.json");if(!File.Exists(file))return;var instance=JsonConvert.DeserializeObject<ServerInstance>(File.ReadAllText(file));Validate(instance);if(instance.Id!=id)throw new InvalidDataException("实例索引身份不匹配，已保留文件。");var target=paths.InstanceDirectory(instance);
    if(Directory.Exists(target)){RequireManagedStorage(paths,instance);RejectTree(target);Directory.Delete(target,true);}
    if(!string.IsNullOrEmpty(instance.StorageDirectory)&&Directory.Exists(index)){RejectReparseParents(index);RejectTree(index);Directory.Delete(index,true);}
   }
  }
  private static void RejectTree(string directory){PathSafety.RejectReparse(directory);foreach(var file in Directory.GetFiles(directory))PathSafety.RejectReparse(file);foreach(var child in Directory.GetDirectories(directory))RejectTree(child);}
  private sealed class StorageIdentity{public int SchemaVersion=1;public string InstanceId;}

 }
 public static class WindowsCommandLine
 {
  public static string Quote(string value)
  {
   if(value==null) throw new ArgumentNullException(nameof(value));
   if(value.Length>0 && !value.Any(c=>char.IsWhiteSpace(c)||c=='"')) return value;
   var b=new StringBuilder("\""); int slashes=0;
   foreach(var c in value) { if(c=='\\') { slashes++;continue; } if(c=='"') { b.Append('\\',slashes*2+1);b.Append('"');slashes=0;continue; } b.Append('\\',slashes);slashes=0;b.Append(c); }
   b.Append('\\',slashes*2);b.Append('"');return b.ToString();
  }
  public static string Join(IEnumerable<string> args) => string.Join(" ",args.Select(Quote));
  public static List<string> Parse(string input)
  {
   var result=new List<string>(); var b=new StringBuilder(); bool quote=false,started=false;
   for(int i=0;i<(input??"").Length;i++) {
    var c=input[i];
    if(c=='\\') { int n=0;while(i<input.Length && input[i]=='\\'){n++;i++;}if(i<input.Length && input[i]=='"'){b.Append('\\',n/2);if(n%2==1)b.Append('"');else quote=!quote;started=true;}else{b.Append('\\',n);i--;started=true;} }
    else if(c=='"'){quote=!quote;started=true;}
    else if(char.IsWhiteSpace(c)&&!quote){if(started){result.Add(b.ToString());b.Clear();started=false;}}
    else{b.Append(c);started=true;}
   }
   if(quote)throw new InvalidDataException("启动参数中的引号不匹配。");if(started)result.Add(b.ToString());return result;
  }
 }
}
