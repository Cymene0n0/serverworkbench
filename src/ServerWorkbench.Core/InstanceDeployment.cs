using System;
using System.IO;
using System.Threading;

namespace ServerWorkbench.Core
{
 internal static class InstanceDeployment
 {
  // Returns true only when the original staging directory was consumed. A copy
  // across volumes keeps it available until the caller commits its repository.
  internal static bool Commit(string source,string target,ITransferService transfers,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();
   var sourceRoot=Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
   var targetRoot=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
   if(targetRoot.Equals(sourceRoot,StringComparison.OrdinalIgnoreCase)||targetRoot.StartsWith(sourceRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("实例目标不能位于安装暂存目录内部。");
   if(Directory.Exists(target)||File.Exists(target))throw new IOException("实例目录已存在，已停止创建以保留原有内容。");
   var parent=Path.GetDirectoryName(Path.GetFullPath(target));Directory.CreateDirectory(parent);
   try {Directory.Move(source,target);return true;}
   catch(IOException)
   {
    // Directory.Move cannot cross volume boundaries. Copy to an owned sibling
    // and rename there; target never contains a partial server on cancellation.
    if(Directory.Exists(target)||File.Exists(target))throw;
    var temporary=Path.Combine(parent,".swb-deploy-"+Guid.NewGuid().ToString("N"));
    try
    {
     transfers.CopyTree(source,temporary,ct);ct.ThrowIfCancellationRequested();
     if(Directory.Exists(target)||File.Exists(target))throw new IOException("实例目录在创建过程中已被占用，未覆盖任何内容。");
     Directory.Move(temporary,target);return false;
    }
    finally {if(Directory.Exists(temporary))Directory.Delete(temporary,true);}
   }
  }
 }
}
