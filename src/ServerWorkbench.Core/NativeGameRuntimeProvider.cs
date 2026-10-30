using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWorkbench.Core
{
 /// <summary>Prepares app-local native dependencies; never runs MSI installation actions.</summary>
 public sealed class NativeGameRuntimeProvider
 {
  // Microsoft Download Center: https://www.microsoft.com/en-us/download/details.aspx?id=27598
  // This exact Microsoft-signed 4.0 Refresh MSI was verified during Windows acceptance.
  public const string XnaDownloadUrl="https://download.microsoft.com/download/5/3/A/53A804C8-EC78-43CD-A0F0-2FB4D45603D3/xnafx40_redist.msi";
  public const string XnaSha256="2b03c130c4bb5f106b9619bea8150201f7e982709cfe9a0cc8bde75ff0a83b27";
  private static readonly string[] xnaFiles={"Microsoft.Xna.Framework.dll","Microsoft.Xna.Framework.Graphics.dll","Microsoft.Xna.Framework.Game.dll","Microsoft.Xna.Framework.GamerServices.dll","Microsoft.Xna.Framework.Input.Touch.dll","Microsoft.Xna.Framework.Xact.dll","XnaNative.dll","msvcr100.dll","msvcp100.dll"};
  private static readonly Dictionary<string,string> directXFiles=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"Feb2010_X3DAudio_x86.cab","X3DAudio1_7.dll"},{"Mar2009_d3dx9_41_x86.cab","d3dx9_41.dll"}};
  private readonly AppPaths paths;private readonly ITransferService transfers;
  public NativeGameRuntimeProvider(AppPaths paths,ITransferService transfers){this.paths=paths??throw new ArgumentNullException(nameof(paths));this.transfers=transfers??throw new ArgumentNullException(nameof(transfers));}

  /// <summary>Only accepts a new installation payload under AppPaths.Temp, not an existing user's instance.</summary>
  public async Task PrepareTerrariaAsync(string serverDirectory,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();
   if(!PathSafety.IsWindows){progress?.Report(new WorkProgress("Terraria 的私有 XNA/DirectX 依赖需在 Windows 安装和实测；当前系统未执行 Windows 提取。"));return;}
   var destination=Path.GetFullPath(serverDirectory);var temp=Path.GetFullPath(paths.Temp).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!destination.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Directory.Exists(destination)||!File.Exists(Path.Combine(destination,"TerrariaServer.exe")))throw new InvalidOperationException("私有 Terraria 运行库只可准备到临时安装包的 server 目录，不能修改现有实例。");
   RejectReparseParents(paths.Temp,destination);
   var staging=PathSafety.ResolveUnder(paths.Temp,"xna-private-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
   try
   {
    var msi=Path.Combine(staging,"xnafx40_redist.msi");progress?.Report(new WorkProgress("下载微软 XNA 4.0 Refresh，校验后仅提取实例私有依赖，不安装系统组件"));
    await transfers.DownloadAsync(new DownloadRequest{Url=XnaDownloadUrl,Destination=msi,Hash=XnaSha256,HashAlgorithm="SHA256"},progress,ct).ConfigureAwait(false);
    // Recheck here too: a custom transfer implementation must not bypass the pinned payload.
    if(!PathSafety.HashFile(msi,"SHA256").Equals(XnaSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("微软 XNA 包 SHA-256 不匹配，未准备运行库。");
    await Task.Run(()=>ExtractAndCopy(msi,staging,destination,progress,ct),ct).ConfigureAwait(false);
    progress?.Report(new WorkProgress("Terraria 私有 XNA、VC++ 和 DirectX 依赖已准备（未修改系统 PATH/GAC）",100));
   }
   finally{if(Directory.Exists(staging)){foreach(var file in Directory.GetFiles(staging,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);Directory.Delete(staging,true);}}
  }

  private static void RejectReparseParents(string root,string destination)
  {
   var current=destination;var fullRoot=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
   while(current.Length>=fullRoot.Length){PathSafety.RejectReparse(current);if(current.Equals(fullRoot,StringComparison.OrdinalIgnoreCase))return;current=Path.GetDirectoryName(current);}
   throw new InvalidDataException("临时安装目录越界。");
  }

  private static void ExtractAndCopy(string msi,string staging,string destination,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var cab=Path.Combine(staging,"product.cab");uint database=0;
   Check(MsiOpenDatabase(msi,IntPtr.Zero,out database),"无法只读打开微软 XNA 包");
   try
   {
    uint view=0;Check(MsiDatabaseOpenView(database,"SELECT `File`, `FileName` FROM `File`",out view),"无法读取 XNA 文件表");
    try
    {
     Check(MsiViewExecute(view,0),"无法查询 XNA 文件表");uint record;
     for(;;){uint result=MsiViewFetch(view,out record);if(result==259)break;Check(result,"无法读取 XNA 文件记录");try{ct.ThrowIfCancellationRequested();var key=ReadString(record,1);var name=ReadString(record,2).Split('|').Last();if(xnaFiles.Contains(name,StringComparer.OrdinalIgnoreCase)||directXFiles.ContainsKey(name)){if(!Regex.IsMatch(key,@"^[A-Za-z0-9_.{}-]+$"))throw new InvalidDataException("XNA CAB 文件键不安全。");if(files.ContainsKey(name))throw new InvalidDataException("XNA 文件表有重复目标。");files.Add(name,key);}}finally{MsiCloseHandle(record);}}
    }finally{MsiViewClose(view);MsiCloseHandle(view);}
    foreach(var name in xnaFiles.Concat(directXFiles.Keys))if(!files.ContainsKey(name))throw new InvalidDataException("固定 XNA 包缺少运行库："+name);
    ExportCabinet(database,cab,ct);
   }finally{MsiCloseHandle(database);}

   var extracted=Path.Combine(staging,"extracted");Directory.CreateDirectory(extracted);var ready=Path.Combine(staging,"ready");Directory.CreateDirectory(ready);
   foreach(var pair in files)
   {
    ct.ThrowIfCancellationRequested();Expand(cab,pair.Value,extracted,ct);var source=Path.Combine(extracted,pair.Value);
    if(!File.Exists(source))throw new InvalidDataException("XNA CAB 未生成预期文件："+pair.Value);
    if(directXFiles.TryGetValue(pair.Key,out var dll))
    {
     var part=Path.Combine(staging,Path.GetFileNameWithoutExtension(pair.Key));Directory.CreateDirectory(part);Expand(source,dll,part,ct);source=Path.Combine(part,dll);
     if(!File.Exists(source))throw new InvalidDataException("DirectX CAB 未生成预期文件："+dll);
     File.Copy(source,Path.Combine(ready,dll));
    }
    else File.Copy(source,Path.Combine(ready,pair.Key));
   }
   var prepared=Directory.GetFiles(ready);foreach(var file in prepared){File.SetAttributes(file,FileAttributes.Normal);RuntimeProvider.ValidateArchitecture(file,"x86");var target=Path.Combine(destination,Path.GetFileName(file));if(File.Exists(target)){PathSafety.RejectReparse(target);if(!PathSafety.HashFile(file,"SHA256").Equals(PathSafety.HashFile(target,"SHA256"),StringComparison.OrdinalIgnoreCase))throw new IOException("安装包已有不同的运行库，未覆盖："+Path.GetFileName(file));}}
   var created=new List<string>();
   try{foreach(var file in prepared){ct.ThrowIfCancellationRequested();var target=Path.Combine(destination,Path.GetFileName(file));if(!File.Exists(target)){File.Copy(file,target);created.Add(target);}progress?.Report(new WorkProgress("准备私有运行库："+Path.GetFileName(file)));}}
   catch{foreach(var file in created)if(File.Exists(file))File.Delete(file);throw;}
  }

  private static void ExportCabinet(uint database,string path,CancellationToken ct)
  {
   uint view=0;Check(MsiDatabaseOpenView(database,"SELECT `Data` FROM `_Streams` WHERE `Name`='product.cab'",out view),"无法读取 XNA 内嵌 CAB");
   try
   {
    Check(MsiViewExecute(view,0),"无法查询 XNA CAB");uint record;Check(MsiViewFetch(view,out record),"XNA 包缺少 product.cab");
    try{using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var buffer=new byte[65536];for(;;){ct.ThrowIfCancellationRequested();uint size=(uint)buffer.Length;Check(MsiRecordReadStream(record,1,buffer,ref size),"无法导出 XNA CAB");if(size==0)break;output.Write(buffer,0,(int)size);if(output.Length>32*1024*1024)throw new InvalidDataException("XNA CAB 超过大小限制。");}}}finally{MsiCloseHandle(record);}
   }finally{MsiViewClose(view);MsiCloseHandle(view);}
  }

  private static string ReadString(uint record,uint field)
  {
   uint length=0;uint result=MsiRecordGetString(record,field,null,ref length);if(result!=0&&result!=234)Check(result,"无法读取 MSI 字符串");if(length>4096)throw new InvalidDataException("MSI 字符串超过限制。");var text=new StringBuilder((int)length+1);length++;Check(MsiRecordGetString(record,field,text,ref length),"无法读取 MSI 字符串");return text.ToString();
  }
  private static void Expand(string cabinet,string file,string destination,CancellationToken ct)
  {
   var executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"expand.exe");if(!File.Exists(executable))throw new FileNotFoundException("Windows 缺少系统 expand.exe，无法准备私有 XNA 依赖。",executable);
   using(var process=new Process{StartInfo=new ProcessStartInfo{FileName=executable,Arguments=WindowsCommandLine.Join(new[]{"-F:"+file,cabinet,destination}),UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Path.GetDirectoryName(cabinet)}})
   {
    ct.ThrowIfCancellationRequested();process.Start();var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
    try{var timer=Stopwatch.StartNew();while(!process.WaitForExit(100)){ct.ThrowIfCancellationRequested();if(timer.Elapsed.TotalSeconds>30)throw new TimeoutException("Windows expand.exe 提取超时。");}Task.WhenAll(output,error).GetAwaiter().GetResult();ct.ThrowIfCancellationRequested();if(process.ExitCode!=0)throw new IOException("Windows expand.exe 无法提取 "+file+"，代码 "+process.ExitCode+"。");}
    finally{if(!process.HasExited)try{process.Kill();process.WaitForExit();}catch(InvalidOperationException){}}
   }
  }
  private static void Check(uint result,string message){if(result!=0)throw new Win32Exception((int)result,message+"（Windows Installer 错误 "+result+"）");}
  [DllImport("msi.dll",EntryPoint="MsiOpenDatabaseW",CharSet=CharSet.Unicode)]private static extern uint MsiOpenDatabase(string path,IntPtr persist,out uint database);
  [DllImport("msi.dll",EntryPoint="MsiDatabaseOpenViewW",CharSet=CharSet.Unicode)]private static extern uint MsiDatabaseOpenView(uint database,string query,out uint view);
  [DllImport("msi.dll")]private static extern uint MsiViewExecute(uint view,uint record);
  [DllImport("msi.dll")]private static extern uint MsiViewFetch(uint view,out uint record);
  [DllImport("msi.dll")]private static extern uint MsiViewClose(uint view);
  [DllImport("msi.dll")]private static extern uint MsiCloseHandle(uint handle);
  [DllImport("msi.dll",EntryPoint="MsiRecordGetStringW",CharSet=CharSet.Unicode)]private static extern uint MsiRecordGetString(uint record,uint field,StringBuilder value,ref uint length);
  [DllImport("msi.dll")]private static extern uint MsiRecordReadStream(uint record,uint field,byte[] data,ref uint size);
 }
}
