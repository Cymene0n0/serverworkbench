using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ServerWorkbench.Core;

internal static class DeploymentTests
{
 public static async Task Run(Func<string,Func<Task>,Task> test)
 {
  await test("通用导入提交到独立服务器目录并保持原来源不变",async()=>
  {
   var paths=Program.Paths("external-import");var repository=new InstanceRepository(paths);var importer=new ImportService(paths,repository,new TransferService(),new Program.FakeRuntime());
   var source=Path.Combine(paths.Root,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"server.cmd"),"@echo source");
   var instance=Program.Instance();instance.EntryPoint="server.cmd";instance.Runtime.Kind=RuntimeKind.Script;instance.StorageDirectory=Path.GetFullPath(Path.Combine(paths.Root,"..","separate servers",instance.Id));
   var staged=await importer.StageAsync(source,null,default);await importer.CommitAsync(staged,instance,null,default);
   Program.Equal(instance.StorageDirectory,paths.InstanceDirectory(instance));Program.Assert(File.Exists(Path.Combine(instance.StorageDirectory,"server.cmd")),"独立目录未提交服务端");
   Program.Equal("@echo source",File.ReadAllText(Path.Combine(source,"server.cmd")));Program.Assert(!File.Exists(Path.Combine(source,".workbench-instance.json")),"原来源被写入实例标记");
   Program.Equal(instance.Id,repository.List().Single().Id);Program.Assert(File.Exists(Path.Combine(paths.Instances,instance.Id,"instance.json")),"缺少中央索引");
   var again=await importer.StageAsync(source,null,default);File.WriteAllText(Path.Combine(instance.StorageDirectory,"keep.txt"),"original data");
   await Program.ThrowsAsync(()=>importer.CommitAsync(again,instance,null,default));Program.Equal("original data",File.ReadAllText(Path.Combine(instance.StorageDirectory,"keep.txt")));importer.Discard(again);
   var nested=Program.Instance();nested.EntryPoint="server.cmd";nested.StorageDirectory=Path.Combine(source,"nested server");
   var nestedStage=await importer.StageAsync(source,null,default);await Program.ThrowsAsync(()=>importer.CommitAsync(nestedStage,nested,null,default));Program.Assert(!Directory.Exists(nested.StorageDirectory),"导入向原来源中创建了实例目录");importer.Discard(nestedStage);
  });
  await test("独立目录导入注册失败会回滚暂存且保留源文件",async()=>
  {
   var paths=Program.Paths("external-import-rollback");var importer=new ImportService(paths,new FailingRepository(),new TransferService(),new Program.FakeRuntime());
   var source=Path.Combine(paths.Root,"source");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"server.cmd"),"original");
   var instance=Program.Instance();instance.EntryPoint="server.cmd";instance.Runtime.Kind=RuntimeKind.Script;instance.StorageDirectory=Path.GetFullPath(Path.Combine(paths.Root,"..","failed separate servers",instance.Id));
   var staged=await importer.StageAsync(source,null,default);await Program.ThrowsAsync(()=>importer.CommitAsync(staged,instance,null,default));
   Program.Assert(!Directory.Exists(instance.StorageDirectory),"注册失败后残留已提交目录");Program.Assert(File.Exists(Path.Combine(staged.StagingDirectory,"server.cmd")),"注册失败后暂存不可重试");Program.Equal("original",File.ReadAllText(Path.Combine(source,"server.cmd")));importer.Discard(staged);
  });
 }
 sealed class FailingRepository:IInstanceRepository
 {
  public IReadOnlyList<ServerInstance> List()=>new List<ServerInstance>();
  public void Save(ServerInstance instance){throw new IOException("Simulated registry write failure");}
  public void Delete(string id){throw new NotSupportedException();}
 }
}
