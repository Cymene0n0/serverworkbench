using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ServerWorkbench.Core;

internal static class Program
{
 static string logfile;
 static int Main(string[] args)
 {
  try { return Run(args).GetAwaiter().GetResult(); }
  catch(Exception e){Log("FAIL "+e);return 1;}
 }
 static async Task<int> Run(string[] args)
 {
  if(args.Length!=2)throw new ArgumentException("Usage: RuntimeTests <empty-test-data-root> Java|DotNet");
  var root=Path.GetFullPath(args[0]);var kind=(RuntimeKind)Enum.Parse(typeof(RuntimeKind),args[1]);
  var marker=Path.Combine(root,".runtime-tests");
  if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any()&&!File.Exists(marker))throw new IOException("Refusing existing non-test data directory.");
  Directory.CreateDirectory(root);File.WriteAllText(marker,"Isolated test data; no user worlds.");
  logfile=Path.Combine(root,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+kind+".log");
  ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
  var paths=new AppPaths(root);Directory.CreateDirectory(paths.Temp);Directory.CreateDirectory(paths.Runtimes);
  var requirement=kind==RuntimeKind.Java?new RuntimeRequirement{Kind=RuntimeKind.Java,JavaMajor=17}:new RuntimeRequirement{Kind=RuntimeKind.DotNet,Version="8.0.0",RollForward="LatestPatch"};
  Log("OS="+Environment.OSVersion+" CLR="+Environment.Version+" x64="+Environment.Is64BitProcess+"; default RuntimeProvider and TransferService, no local mapping or manual cache");
  using(var cancel=new CancellationTokenSource(TimeSpan.FromMinutes(12)))
  {
   var resolved=await new RuntimeProvider(paths,new TransferService()).EnsureAsync(requirement,"",new ProgressLog(),cancel.Token);
   Log("RESOLVED "+resolved.Executable+" "+resolved.Description);
   RuntimeProvider.ValidateArchitecture(resolved.Executable,"x64");
   var arguments=kind==RuntimeKind.Java?"-version":"--list-runtimes";
   using(var process=new Process{StartInfo=new ProcessStartInfo(resolved.Executable,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}})
   {
    process.Start();var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    if(!process.WaitForExit(15000)){process.Kill();throw new TimeoutException("Runtime probe timed out");}
    var output=await stdout+"\n"+await stderr;Log("PROBE "+output);
    if(process.ExitCode!=0)throw new Exception("Runtime probe exit "+process.ExitCode);
    File.WriteAllText(Path.Combine(root,kind+"-verified.json"),JsonConvert.SerializeObject(new{passed=true,os=Environment.OSVersion.ToString(),clr=Environment.Version.ToString(),kind=kind.ToString(),executable=resolved.Executable,probe=output,defaultProvider=true},Formatting.Indented));
    Log("PASS private runtime downloaded, hash/architecture verified, executable probe succeeded");
   }
  }
  return 0;
 }
 static void Log(string value){var line=DateTime.Now.ToString("HH:mm:ss")+" "+value;Console.WriteLine(line);if(logfile!=null)File.AppendAllText(logfile,line+Environment.NewLine,new UTF8Encoding(false));}
 sealed class ProgressLog:IProgress<WorkProgress>
 {
  DateTime last;string message;
  public void Report(WorkProgress value){if(value.Message!=message||(DateTime.UtcNow-last).TotalSeconds>=5){message=value.Message;last=DateTime.UtcNow;Log("WORK "+value.Message+(value.Percent.HasValue?" "+value.Percent.Value.ToString("F1")+"%":""));}}
 }
}
