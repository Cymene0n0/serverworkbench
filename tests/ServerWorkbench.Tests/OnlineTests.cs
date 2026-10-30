using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ServerWorkbench.Core;
internal static class OnlineTests
{
 public static async Task Run(Func<string,Func<Task>,Task> test)
 {
  var paths=Program.Paths("online");var repo=new InstanceRepository(paths);var transfers=new TransferService();var presets=new PresetService(paths,repo,transfers,new Program.FakeRuntime());
  await test("在线官方版本源 Mojang/Fabric/Forge/Terraria/tModLoader",async()=>{
   foreach(var kind in new[]{ServerKind.Vanilla,ServerKind.Fabric,ServerKind.Forge,ServerKind.Terraria,ServerKind.TModLoader}){var versions=await presets.GetVersionsAsync(kind,default);Program.Assert(versions.Count>0,"官方版本源为空："+kind);Console.WriteLine("  SOURCE "+kind+" count="+versions.Count+" first="+versions[0].Id);}
   foreach(string mc in new[]{"1.16.5","1.20.1","1.21.1"})foreach(var kind in new[]{ServerKind.Fabric,ServerKind.Forge})Program.Assert((await presets.GetLoaderVersionsAsync(kind,mc,default)).Count>0,"加载器源为空 "+kind+" "+mc);
  });
  foreach(var mc in new[]{"1.16.5","1.20.1","1.21.1"})await test("真实下载并校验 Vanilla "+mc,async()=>{
   var s=await presets.InstallAsync(new CreateServerRequest{Name="下载校验 "+mc,Kind=ServerKind.Vanilla,GameVersion=mc,AcceptEula=true,Port=30000},null,default);Program.Assert(new FileInfo(Path.Combine(paths.InstanceDirectory(s),s.EntryPoint)).Length>10*1024*1024,"server.jar 过小");Program.Equal(mc=="1.16.5"?8:mc=="1.20.1"?17:21,s.Runtime.JavaMajor);repo.Delete(s.Id);
  });
  await test("真实下载 Fabric 1.20.1 启动包",async()=>{
   var loader=(await presets.GetLoaderVersionsAsync(ServerKind.Fabric,"1.20.1",default))[0];var s=await presets.InstallAsync(new CreateServerRequest{Name="Fabric install verification",Kind=ServerKind.Fabric,GameVersion="1.20.1",LoaderVersion=loader.Id,AcceptEula=true,Port=30001},null,default);Program.Assert(File.Exists(Path.Combine(paths.InstanceDirectory(s),"fabric-server-launch.jar")),"Fabric 启动包缺失");repo.Delete(s.Id);
  });
  await test("真实下载 Terraria Windows 专服包",async()=>{
   var v=(await presets.GetVersionsAsync(ServerKind.Terraria,default))[0];var s=await presets.InstallAsync(new CreateServerRequest{Name="Terraria verification",Kind=ServerKind.Terraria,GameVersion=v.Id,Port=30002},null,default);Program.Assert(File.Exists(Path.Combine(paths.InstanceDirectory(s),s.EntryPoint)),"Windows 服务端缺失");repo.Delete(s.Id);
  });
  await test("真实下载 tModLoader 稳定发布包",async()=>{
   var v=(await presets.GetVersionsAsync(ServerKind.TModLoader,default))[0];var s=await presets.InstallAsync(new CreateServerRequest{Name="tModLoader verification",Kind=ServerKind.TModLoader,GameVersion=v.Id,Port=30003},null,default);Program.Assert(File.Exists(Path.Combine(paths.InstanceDirectory(s),s.EntryPoint)),"启动脚本缺失");repo.Delete(s.Id);
  });
  await RunModrinth(test);
 }
 public static Task RunModrinth(Func<string,Func<Task>,Task> test)=>test("Modrinth 真实搜索与匹配版本",async()=>{
  var paths=Program.Paths("online-modrinth");var repo=new InstanceRepository(paths);var transfers=new TransferService();var process=new Program.FakeProcesses();var mods=new ModProvider(paths,repo,transfers,process,new BackupService(paths,transfers,process));var instance=new ServerInstance{Kind=ServerKind.Fabric,GameVersion="1.20.1"};var result=await mods.SearchAsync(instance,"fabric api",default);Program.Assert(result.Count>0,"搜索未返回项目");Program.Assert((await mods.GetVersionsAsync(instance,result[0].Id,default)).Count>0,"项目无匹配版本");
 });
 public static async Task RunRuntimes(Func<string,Func<Task>,Task> test)
 {
  var paths=Program.Paths("online-runtimes");var transfers=new TransferService();var runtime=new RuntimeProvider(paths,transfers);
  await test("真实准备 Windows Java 17 私有环境与PE架构校验",async()=>{var result=await runtime.EnsureAsync(new RuntimeRequirement{Kind=RuntimeKind.Java,JavaMajor=17},"",null,default);Program.Assert(File.Exists(result.Executable),"java.exe 缺失");RuntimeProvider.ValidateArchitecture(result.Executable,"x64");});
  await test("真实准备 Windows .NET 8 私有环境与PE架构校验",async()=>{var result=await runtime.EnsureAsync(new RuntimeRequirement{Kind=RuntimeKind.DotNet,Version="8.0.0",RollForward="LatestPatch"},"",null,default);Program.Assert(File.Exists(result.Executable),"dotnet.exe 缺失");RuntimeProvider.ValidateArchitecture(result.Executable,"x64");});
 }
}
