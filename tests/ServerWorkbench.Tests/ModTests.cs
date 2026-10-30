using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using ServerWorkbench.Core;
internal static class ModTests
{
 public static async Task Run(Func<string,Func<Task>,Task> test,string root)
 {
  await test("Mod 必需依赖安装、启用保护及禁用恢复",async()=>{
   var f=new Fixture("mods-required");f.Add("Alpha","A1","alpha.jar",new JArray(Dependency("Beta","B1","required")));f.Add("Beta","B1","beta.jar");
   await f.Provider.InstallAsync(f.Instance,"A1",null,default);Program.Equal(2,f.Instance.Mods.Count);Program.Assert(File.Exists(Path.Combine(f.Directory,"mods","beta.jar")),"前置应安装");
   await Program.ThrowsAsync(()=>f.Provider.SetEnabledAsync(f.Instance,f.Instance.Mods.Single(x=>x.ProjectId=="Beta"),false,default));
   await f.Provider.SetEnabledAsync(f.Instance,f.Instance.Mods.Single(x=>x.ProjectId=="Alpha"),false,default);await f.Provider.SetEnabledAsync(f.Instance,f.Instance.Mods.Single(x=>x.ProjectId=="Beta"),false,default);
   await Program.ThrowsAsync(()=>f.Provider.SetEnabledAsync(f.Instance,f.Instance.Mods.Single(x=>x.ProjectId=="Alpha"),true,default));
   await f.Provider.SetEnabledAsync(f.Instance,f.Instance.Mods.Single(x=>x.ProjectId=="Beta"),true,default);await f.Provider.SetEnabledAsync(f.Instance,f.Instance.Mods.Single(x=>x.ProjectId=="Alpha"),true,default);Program.Assert(f.Instance.Mods.All(x=>x.Enabled),"恢复启用");
  });
  await test("Modrinth 环境数组及客户端过滤回归",async()=>{
   var paths=Program.Paths("mods-environment-array");var repo=new InstanceRepository(paths);var s=new ServerInstance{Kind=ServerKind.Fabric,GameVersion="1.20.1"};repo.Save(s);var process=new Program.FakeProcesses();var transfer=new TransferService();
   var response=new JObject{["hits"]=new JArray(new JObject{["project_id"]="ServerMod",["title"]="Server",["environment"]=new JArray("client_or_server_prefers_both"),["server_side"]="optional"},new JObject{["project_id"]="ClientMod",["title"]="Client",["environment"]=new JArray("client_only")})};
   var provider=new ModProvider(paths,repo,transfer,process,new BackupService(paths,transfer,process),Program.Client(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(response.ToString())}));var result=await provider.SearchAsync(s,"test",default);Program.Equal(1,result.Count);Program.Equal("ServerMod",result[0].Id);
  });
  await test("Mod 声明冲突和循环依赖拒绝且不修改文件",async()=>{
   var f=new Fixture("mods-conflict");f.Add("Alpha","A1","a.jar",new JArray(Dependency("Beta","B1","required"),Dependency("Beta",null,"incompatible")));f.Add("Beta","B1","b.jar");await Program.ThrowsAsync(()=>f.Provider.InstallAsync(f.Instance,"A1",null,default));Program.Equal(0,f.Instance.Mods.Count);
   var cycle=new Fixture("mods-cycle");cycle.Add("Alpha","A1","a.jar",new JArray(Dependency("Beta","B1","required")));cycle.Add("Beta","B1","b.jar",new JArray(Dependency("Alpha","A1","required")));await Program.ThrowsAsync(()=>cycle.Provider.InstallAsync(cycle.Instance,"A1",null,default));Program.Equal(0,cycle.Instance.Mods.Count);
  });
  await test("Mod 不兼容版本、客户端项目、重复文件与错误哈希",async()=>{
   var f=new Fixture("mods-invalid");f.Add("Alpha","A1","same.jar",new JArray(Dependency("Beta","B1","required")));f.Add("Beta","B1","same.jar");await Program.ThrowsAsync(()=>f.Provider.InstallAsync(f.Instance,"A1",null,default));Program.Equal(0,f.Instance.Mods.Count);
   f.Versions["A1"]["dependencies"]=new JArray();f.Versions["A1"]["game_versions"]=new JArray("wrong");await Program.ThrowsAsync(()=>f.Provider.InstallAsync(f.Instance,"A1",null,default));
   f.Versions["A1"]["game_versions"]=new JArray("1.20.1");f.Projects["Alpha"]["server_side"]="unsupported";await Program.ThrowsAsync(()=>f.Provider.InstallAsync(f.Instance,"A1",null,default));
   f.Projects["Alpha"]["server_side"]="required";f.Versions["A1"]["files"][0]["hashes"]["sha512"]=new string('0',128);await Program.ThrowsAsync(()=>f.Provider.InstallAsync(f.Instance,"A1",null,default));Program.Equal(0,f.Instance.Mods.Count);
  });
  await test("Mod 提交失败完整回滚",async()=>{
   var f=new Fixture("mods-rollback",true);f.Add("Alpha","A1","a.jar");await Program.ThrowsAsync(()=>f.Provider.InstallAsync(f.Instance,"A1",null,default));Program.Equal(0,f.Instance.Mods.Count);Program.Assert(!File.Exists(Path.Combine(f.Directory,"mods","a.jar")),"不应遗留半安装文件");Program.Equal(0,f.Repository.List().Single().Mods.Count);
  });
  await test("本地 Mod 导入、禁用、删除与运行中保护",async()=>{
   var f=new Fixture("mods-local");var file=Path.Combine(root,"local-test.jar");File.WriteAllText(file,"local file");await f.Provider.ImportLocalAsync(f.Instance,file,"",null,default);Program.Equal(1,f.Instance.Mods.Count);var mod=f.Instance.Mods[0];await f.Provider.SetEnabledAsync(f.Instance,mod,false,default);Program.Assert(File.Exists(Path.Combine(f.Directory,".disabled-mods",mod.FileName)),"应移出加载目录");f.Processes.Running=true;await Program.ThrowsAsync(()=>f.Provider.RemoveAsync(f.Instance,f.Instance.Mods[0],default));f.Processes.Running=false;await f.Provider.RemoveAsync(f.Instance,f.Instance.Mods[0],default);Program.Equal(0,f.Instance.Mods.Count);
  });
  await test("tModLoader 内部名、启用清单及损坏文件",async()=>{
   var f=new Fixture("mods-tml");f.Instance.Kind=ServerKind.TModLoader;f.Repository.Save(f.Instance);var source=Path.Combine(root,"local-tml");Directory.CreateDirectory(source);var file=Path.Combine(source,"display-name.tmod");MakeTmod(file,"InternalMod");File.WriteAllText(Path.Combine(source,"enabled.json"),"[\"InternalMod\"]");
   await f.Provider.ImportLocalAsync(f.Instance,source,"",null,default);Program.Equal("InternalMod",f.Instance.Mods.Single().InternalName);var enabled=Path.Combine(f.Directory,"mods","enabled.json");Program.Assert(File.ReadAllText(enabled).Contains("InternalMod"),"应启用内部名");await f.Provider.SetEnabledAsync(f.Instance,f.Instance.Mods[0],false,default);Program.Equal(0,JArray.Parse(File.ReadAllText(enabled)).Count);
   File.WriteAllText(file,"bad .tmod");await Program.ThrowsAsync(()=>f.Provider.ImportLocalAsync(f.Instance,file,"InternalMod",null,default));
  });
 }
 private static JObject Dependency(string project,string version,string kind)=>new JObject{["project_id"]=project,["version_id"]=version,["dependency_type"]=kind};
 private sealed class Fixture
 {
  public readonly AppPaths Paths;public readonly IInstanceRepository Repository;public readonly ServerInstance Instance;public readonly Program.FakeProcesses Processes=new Program.FakeProcesses();public readonly ModProvider Provider;public readonly Dictionary<string,JObject> Versions=new Dictionary<string,JObject>(),Projects=new Dictionary<string,JObject>();readonly Dictionary<string,byte[]> files=new Dictionary<string,byte[]>();
  public string Directory=>Paths.InstanceDirectory(Instance);
  public Fixture(string name,bool failSave=false){Paths=Program.Paths(name);var repo=new InstanceRepository(Paths);Instance=Program.Instance();Instance.Kind=ServerKind.Fabric;Instance.GameVersion="1.20.1";repo.Save(Instance);Repository=failSave?new FailOnceRepository(repo):repo;var transfer=new TransferService(Program.Client(req=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(files[req.RequestUri.AbsolutePath])}));Provider=new ModProvider(Paths,Repository,transfer,Processes,new BackupService(Paths,transfer,Processes),Program.Client(Response));}
  public void Add(string project,string id,string file,JArray dependencies=null){var bytes=Encoding.UTF8.GetBytes("mod data "+id);files["/"+id]=bytes;Projects[project]=new JObject{["id"]=project,["title"]=project,["server_side"]="required"};Versions[id]=new JObject{["id"]=id,["project_id"]=project,["name"]=id,["version_number"]="1.0",["game_versions"]=new JArray("1.20.1"),["loaders"]=new JArray("fabric"),["dependencies"]=dependencies??new JArray(),["files"]=new JArray(new JObject{["filename"]=file,["primary"]=true,["url"]="https://cdn.example/"+id,["hashes"]=new JObject{["sha512"]=Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant()}})};}
  private HttpResponseMessage Response(HttpRequestMessage req){var path=req.RequestUri.AbsolutePath.Substring("/v2/".Length);JToken value;if(path.StartsWith("version/"))value=Versions[path.Substring(8)];else if(path.EndsWith("/version")){var project=path.Split('/')[1];value=new JArray(Versions.Values.Where(x=>(string)x["project_id"]==project));}else if(path.StartsWith("project/"))value=Projects[path.Substring(8)];else throw new Exception("Unexpected API: "+path);return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(value.ToString(),Encoding.UTF8,"application/json")};}
 }
 private sealed class FailOnceRepository:IInstanceRepository{readonly IInstanceRepository inner;bool fail=true;public FailOnceRepository(IInstanceRepository inner){this.inner=inner;}public IReadOnlyList<ServerInstance> List()=>inner.List();public void Delete(string id)=>inner.Delete(id);public void Save(ServerInstance s){if(fail){fail=false;throw new IOException("Simulated metadata commit failure");}inner.Save(s);}}
 private static void MakeTmod(string path,string name){byte[] data;using(var memory=new MemoryStream()){using(var writer=new BinaryWriter(memory,Encoding.UTF8,true)){writer.Write(name);writer.Write("1.0");}data=memory.ToArray();}using(var writer=new BinaryWriter(File.Create(path))){writer.Write(Encoding.ASCII.GetBytes("TMOD"));writer.Write("2026.08.0");writer.Write(SHA1.HashData(data));writer.Write(new byte[256]);writer.Write(data.Length);writer.Write(data);}}
}
