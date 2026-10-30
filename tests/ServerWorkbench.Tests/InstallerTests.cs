using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ServerWorkbench.Core;

internal static class InstallerTests
{
 public static async Task Run(Func<string,Func<Task>,Task> test)
 {
  if(Environment.OSVersion.Platform!=PlatformID.Win32NT)return;
  await test("Forge 托管脚本在 Java 退出后立即退出并保留错误码（中文空格目录）",async()=>
  {
   var root=Program.Paths("forge-script-中文 空格").Root;
   var argsFile=Path.Combine(root,"libraries","net","minecraftforge","forge","1.20.1-test","win_args.txt");
   Directory.CreateDirectory(Path.GetDirectoryName(argsFile));File.WriteAllText(argsFile,"fixture");
   var original="@echo off\r\njava @user_jvm_args.txt @libraries/test/win_args.txt %*\r\npause\r\n";
   File.WriteAllText(Path.Combine(root,"run.bat"),original);
   typeof(PresetService).GetMethod("PrepareForgeLaunchScript",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{root,argsFile,1024});
   Program.Equal(original,File.ReadAllText(Path.Combine(root,"run.bat.workbench-original")));
   var bin=Path.Combine(root,"test java");Directory.CreateDirectory(bin);
   var app=typeof(InstallerTests).Assembly.Location;var source=Path.GetDirectoryName(app);
   foreach(var file in Directory.GetFiles(source))if(new[]{".exe",".dll",".json",".config"}.Contains(Path.GetExtension(file)))File.Copy(file,Path.Combine(bin,Path.GetFileName(file)));
   var host=Path.Combine(source,Path.GetFileNameWithoutExtension(app)+".exe");
   if(!File.Exists(host))throw new FileNotFoundException("Windows 脚本测试需要测试程序的 apphost。",host);
   File.Copy(host,Path.Combine(bin,"java.exe"));
   var marker=Path.Combine(root,"java-received.txt");
   using(var p=new Process{StartInfo=new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC")??"cmd.exe","/d /s /c \"\""+Path.Combine(root,"run.bat")+"\" nogui\""){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}})
   {
    p.StartInfo.Environment["PATH"]=bin+";"+(p.StartInfo.Environment["PATH"]??"");p.StartInfo.Environment["SWB_FORGE_SCRIPT_TEST"]=marker;
    p.Start();var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();
    var end=DateTime.UtcNow.AddSeconds(10);while(!p.HasExited&&DateTime.UtcNow<end)await Task.Delay(50);
    if(!p.HasExited){p.Kill(true);throw new Exception("Java 已退出但 run.bat 仍等待输入。");}
    Program.Equal(23,p.ExitCode);Program.Assert(File.Exists(marker),"未运行私有 PATH 中的 Java 测试入口："+await stdout+await stderr);
    var received=File.ReadAllLines(marker);Program.Assert(received.Contains("@user_jvm_args.txt")&&received.Contains("@libraries\\net\\minecraftforge\\forge\\1.20.1-test\\win_args.txt")&&received.Contains("nogui"),"参数未完整传给 Java。");
   }
  });
  await test("tModLoader 同步脚本保留标准输入输出及子进程退出码",async()=>
  {
   var root=Program.Paths("tmod-script-中文 空格").Root;var launch=Path.Combine(root,"LaunchUtils");Directory.CreateDirectory(launch);
   var app=typeof(InstallerTests).Assembly.Location;var source=Path.GetDirectoryName(app);
   foreach(var file in Directory.GetFiles(source))if(new[]{".exe",".dll",".json",".config"}.Contains(Path.GetExtension(file)))File.Copy(file,Path.Combine(launch,Path.GetFileName(file)));
   File.Copy(Path.Combine(source,Path.GetFileNameWithoutExtension(app)+".exe"),Path.Combine(launch,"busybox64.exe"));
   File.WriteAllText(Path.Combine(root,"start-tModLoaderServer.sh"),"fixture");
   var script=Path.Combine(root,"start-tModLoaderServer.bat");var original="@echo off\r\nLaunchUtils/busybox-sh.bat ./start-tModLoaderServer.sh %*\r\n";File.WriteAllText(script,original);
   typeof(PresetService).GetMethod("PrepareTModLoaderLaunchScript",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{root,script});
   Program.Equal(original,File.ReadAllText(script+".workbench-original"));var marker=Path.Combine(root,"shell-received.txt");
   using(var p=new Process{StartInfo=new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC")??"cmd.exe","/d /s /c \"\""+script+"\" -nosteam -config serverconfig.txt\""){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}})
   {
    p.StartInfo.Environment["SWB_FORGE_SCRIPT_TEST"]=marker;p.StartInfo.Environment["SWB_SCRIPT_STDIN_TEST"]="1";
    p.Start();var stdout=p.StandardOutput.ReadToEndAsync();var stderr=p.StandardError.ReadToEndAsync();p.StandardInput.WriteLine("save");p.StandardInput.Flush();
    var end=DateTime.UtcNow.AddSeconds(10);while(!p.HasExited&&DateTime.UtcNow<end)await Task.Delay(50);
    if(!p.HasExited){p.Kill(true);throw new Exception("同步 tModLoader 脚本未退出。");}
    Program.Equal(23,p.ExitCode);Program.Assert((await stdout).Contains("PIPE:save"),"游戏未继承管理器 stdin/stdout："+await stderr);
    var received=File.ReadAllLines(marker);Program.Assert(received.Contains("bash")&&received.Contains("./start-tModLoaderServer.sh")&&received.Contains("-nosteam")&&received.Contains("serverconfig.txt"),"官方脚本参数丢失。");
   }
  });
  await test("tModLoader 预置官方精确 .NET 版本并保持原始运行配置",async()=>
  {
   var paths=Program.Paths("tmod-exact-runtime");var working=Path.Combine(paths.Temp,"server");Directory.CreateDirectory(working);
   var configuration="{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"8.0.0\"}}}";var file=Path.Combine(working,"tModLoader.runtimeconfig.json");File.WriteAllText(file,configuration);
   var runtime=new Program.FakeRuntime();var service=new PresetService(paths,new InstanceRepository(paths),new TransferService(),runtime);
   var operation=(Task)typeof(PresetService).GetMethod("PrepareTModLoaderRuntimeAsync",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(service,new object[]{working,null,default(System.Threading.CancellationToken)});await operation;
   Program.Equal("8.0.0",runtime.LastRequirement.Version);Program.Equal("Disable",runtime.LastRequirement.RollForward);
   Program.Assert(File.Exists(Path.Combine(working,"dotnet","dotnet.exe"))&&File.Exists(Path.Combine(working,"dotnet","shared","Microsoft.NETCore.App","8.0.0","fixture.txt")),"官方脚本所需私有运行时布局不完整");Program.Equal(configuration,File.ReadAllText(file));
   Program.Assert(!Directory.Exists(Path.Combine(working,"tModLoader-Logs")),"首启前不能创建日志目录使官方脚本误判启动失败并删除运行时");
  });
 }
}
