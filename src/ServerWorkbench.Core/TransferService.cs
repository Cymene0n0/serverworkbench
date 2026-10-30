using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWorkbench.Core
{
 public sealed class TransferService : ITransferService
 {
  private readonly HttpClient client;
  public TransferService(HttpClient client=null)
  {
   this.client=client??new HttpClient(new HttpClientHandler { AllowAutoRedirect=false });
   this.client.Timeout=TimeSpan.FromMinutes(30);
   if(!this.client.DefaultRequestHeaders.UserAgent.Any()) this.client.DefaultRequestHeaders.UserAgent.ParseAdd("ServerWorkbench/0.1 (personal game server manager)");
  }
  private static Uri SecureUri(string url)
  {
   if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo))throw new InvalidDataException("请输入不含账号密码的 HTTPS 文件直链。");return uri;
  }
  public async Task DownloadAsync(DownloadRequest request,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   var uri=SecureUri(request.Url);Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Destination)));
   var partial=request.Destination+"."+Guid.NewGuid().ToString("N")+".part";
   try {
    for(int attempt=0;;attempt++) {
     try { await DownloadOne(uri,partial,progress,ct).ConfigureAwait(false);break; }
     catch(HttpRequestException) when(attempt<2) { if(File.Exists(partial))File.Delete(partial);await Task.Delay(1000*(attempt+1),ct).ConfigureAwait(false); }
    }
    ct.ThrowIfCancellationRequested();
    if(!string.IsNullOrEmpty(request.Hash) && !string.Equals(PathSafety.HashFile(partial,request.HashAlgorithm),request.Hash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("下载文件哈希不匹配，已拒绝安装。");
    if(File.Exists(request.Destination)) File.Replace(partial,request.Destination,null); else File.Move(partial,request.Destination);
   } finally { if(File.Exists(partial))File.Delete(partial); }
  }
  private async Task DownloadOne(Uri uri,string destination,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   for(int redirect=0;redirect<=6;redirect++) {
    using(var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false)) {
     int code=(int)response.StatusCode;
     if(code>=300&&code<400) { if(response.Headers.Location==null)throw new InvalidDataException("下载重定向缺少地址。");uri=SecureUri(new Uri(uri,response.Headers.Location).AbsoluteUri);continue; }
     response.EnsureSuccessStatusCode();
     if(response.RequestMessage?.RequestUri!=null)SecureUri(response.RequestMessage.RequestUri.AbsoluteUri);
     if((response.Content.Headers.ContentType?.MediaType??"").IndexOf("text/html",StringComparison.OrdinalIgnoreCase)>=0)throw new InvalidDataException("地址返回网页或登录页，请使用直接下载链接。");
     long? total=response.Content.Headers.ContentLength;if(total>20L*1024*1024*1024)throw new InvalidDataException("单次下载不能超过 20GB。");
     using(var source=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var file=new FileStream(destination,FileMode.Create,FileAccess.Write,FileShare.None,81920,true)) {
      var buffer=new byte[81920];long received=0;int count;bool first=true;var last=DateTime.MinValue;
      while((count=await source.ReadAsync(buffer,0,buffer.Length,ct).ConfigureAwait(false))>0) {
       if(first){first=false;var prefix=System.Text.Encoding.UTF8.GetString(buffer,0,Math.Min(count,256)).TrimStart('\uFEFF',' ','\r','\n','\t');if(prefix.StartsWith("<!doctype html",StringComparison.OrdinalIgnoreCase)||prefix.StartsWith("<html",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("返回内容是 HTML 网页，不能作为服务端包。");}
       received+=count;if(received>20L*1024*1024*1024)throw new InvalidDataException("下载超过 20GB 限制。");
       await file.WriteAsync(buffer,0,count,ct).ConfigureAwait(false);
       if((DateTime.UtcNow-last).TotalMilliseconds>200){last=DateTime.UtcNow;progress?.Report(new WorkProgress("下载 "+(received/1048576.0).ToString("F1")+" MB",total>0?received*100.0/total:null));}
      }
      if(total.HasValue && received!=total)throw new HttpRequestException("下载中断，文件长度不完整。");
      if(received==0)throw new InvalidDataException("下载的文件为空。");
     }
     return;
    }
   }
   throw new InvalidDataException("下载重定向次数过多。");
  }
  public void ExtractZip(string archive,string destination,CancellationToken ct)
  {
   Directory.CreateDirectory(destination);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
   var attributes=ReadZipAttributes(archive);
   using(var zip=ZipFile.OpenRead(archive)) {
    if(zip.Entries.Count>100000)throw new InvalidDataException("压缩包文件数量超过限制。");
    if(attributes.Count!=zip.Entries.Count)throw new InvalidDataException("ZIP 中央目录不一致。");
    int entryIndex=0;
    foreach(var entry in zip.Entries) {
     ct.ThrowIfCancellationRequested();
     uint external=attributes[entryIndex++];
     var name=entry.FullName.Replace('\\','/');if(string.IsNullOrEmpty(name))continue;
     var relative=name.TrimEnd('/');if(relative.Length==0)continue;
     PathSafety.EnsureRelative(relative);
     if(relative.Split('/').Any(p=>p=="."||p.Length==0))throw new InvalidDataException("压缩包包含歧义路径。");
     if(!names.Add(relative))throw new InvalidDataException("压缩包包含重复或大小写冲突的路径："+name);
     int mode=(int)((external>>16)&0xF000);
     if(mode==0xA000 || (external&0x400)!=0)throw new InvalidDataException("不解压符号链接或重解析点。");
     total=checked(total+entry.Length);if(total>20L*1024*1024*1024 || entry.Length>8L*1024*1024*1024)throw new InvalidDataException("解压大小超过限制。");
     var target=PathSafety.ResolveUnder(destination,relative);
     if(name.EndsWith("/")){Directory.CreateDirectory(target);continue;}
     Directory.CreateDirectory(Path.GetDirectoryName(target));
     using(var source=entry.Open())using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write)) {
      var buffer=new byte[81920];int n;long written=0;while((n=source.Read(buffer,0,buffer.Length))>0){ct.ThrowIfCancellationRequested();written+=n;if(written>entry.Length)throw new InvalidDataException("ZIP 实际文件大小与声明不一致。");output.Write(buffer,0,n);}if(written!=entry.Length)throw new InvalidDataException("ZIP 文件不完整。");
     }
    }
   }
  }
  private static List<uint> ReadZipAttributes(string path)
  {
   // Read central-directory attributes directly: the public property is absent in netstandard2.0.
   using(var stream=File.OpenRead(path))using(var reader=new BinaryReader(stream)) {
    int length=(int)Math.Min(stream.Length,65557);var tail=new byte[length];stream.Position=stream.Length-length;reader.Read(tail,0,length);int end=-1;
    for(int i=length-22;i>=0;i--)if(BitConverter.ToUInt32(tail,i)==0x06054B50 && i+22+BitConverter.ToUInt16(tail,i+20)==length){end=i;break;}
    if(end<0)throw new InvalidDataException("ZIP 中央目录缺失或损坏。");
    if(BitConverter.ToUInt16(tail,end+4)!=0||BitConverter.ToUInt16(tail,end+6)!=0)throw new InvalidDataException("不支持分卷 ZIP。");
    uint offset=BitConverter.ToUInt32(tail,end+16);ulong count=BitConverter.ToUInt16(tail,end+10);
    if(offset==uint.MaxValue || count==ushort.MaxValue) {
     long locator=stream.Length-length+end-20;if(locator<0)throw new InvalidDataException("ZIP64 目录损坏。");stream.Position=locator;
     if(reader.ReadUInt32()!=0x07064B50)throw new InvalidDataException("ZIP64 目录缺失。");reader.ReadUInt32();ulong zip64=reader.ReadUInt64();if(zip64>(ulong)stream.Length-56)throw new InvalidDataException("ZIP64 目录损坏。");stream.Position=(long)zip64;
     if(reader.ReadUInt32()!=0x06064B50)throw new InvalidDataException("ZIP64 目录损坏。");stream.Position=(long)zip64+32;count=reader.ReadUInt64();reader.ReadUInt64();ulong largeOffset=reader.ReadUInt64();if(largeOffset>(ulong)stream.Length)throw new InvalidDataException("ZIP64 偏移越界。");stream.Position=(long)largeOffset;
    } else stream.Position=offset;
    if(count>100000)throw new InvalidDataException("压缩包文件数量超过限制。");var result=new List<uint>();
    for(ulong n=0;n<count;n++) {
     long start=stream.Position;if(reader.ReadUInt32()!=0x02014B50)throw new InvalidDataException("ZIP 中央目录损坏。");stream.Position=start+28;int name=reader.ReadUInt16(),extra=reader.ReadUInt16(),comment=reader.ReadUInt16();stream.Position=start+38;result.Add(reader.ReadUInt32());stream.Position=checked(start+46+name+extra+comment);
    }
    return result;
   }
  }
  public void CopyTree(string source,string destination,CancellationToken ct)
  {
   var sourceRoot=Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   var target=Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(target.StartsWith(sourceRoot,StringComparison.OrdinalIgnoreCase)||sourceRoot.StartsWith(target,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("源目录和目标目录不能包含彼此。");
   CopyFolder(source,destination,ct);
  }
  private static void CopyFolder(string source,string target,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();PathSafety.RejectReparse(source);Directory.CreateDirectory(target);
   foreach(var f in Directory.GetFiles(source)){ct.ThrowIfCancellationRequested();PathSafety.RejectReparse(f);PathSafety.EnsureRelative(Path.GetFileName(f));File.Copy(f,Path.Combine(target,Path.GetFileName(f)),false);}
   foreach(var d in Directory.GetDirectories(source)){PathSafety.EnsureRelative(Path.GetFileName(d));CopyFolder(d,Path.Combine(target,Path.GetFileName(d)),ct);}
  }
 }
}
