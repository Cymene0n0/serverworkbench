using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ServerWorkbench.Core
{
 public sealed class ProcessManager : IProcessManager
 {
  // Modern Java separates stdout/stderr from file.encoding; older Java uses the
  // sun.* aliases. All are ordinary -D properties, safe for Java 8 as well.
  internal static readonly string[] JavaUtf8Arguments={"-Dfile.encoding=UTF-8","-Dstdout.encoding=UTF-8","-Dstderr.encoding=UTF-8","-Dsun.stdout.encoding=UTF-8","-Dsun.stderr.encoding=UTF-8"};
  private sealed class RunningServer { public Process Process;public StreamWriter Input;public Task OutputDone=Task.CompletedTask;public DateTime StartedUtc;public bool Ready;public IntPtr Job;public readonly Queue<string> Lines=new Queue<string>();public ServerInstance Instance;public DateTime LastSample;public double LastCpu; }
  private readonly AppPaths paths;private readonly IRuntimeProvider runtimes;private readonly Func<string,string> logSanitizer;private readonly bool killOnClose;private readonly object gate=new object();private readonly Dictionary<string,RunningServer> running=new Dictionary<string,RunningServer>();private readonly Dictionary<string,CancellationTokenSource> starting=new Dictionary<string,CancellationTokenSource>();
  public event EventHandler<ServerLogEventArgs> LogReceived;public event EventHandler StatusChanged;
  public ProcessManager(AppPaths paths,IRuntimeProvider runtimes,Func<string,string> logSanitizer=null,bool killOnClose=false){this.paths=paths;this.runtimes=runtimes;this.logSanitizer=logSanitizer;this.killOnClose=killOnClose;}
  public static bool IsPortAvailable(int port,bool udp=false)
  {
   if(port==0)return true;
   try {if(udp){using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp)){socket.ExclusiveAddressUse=true;socket.Bind(new IPEndPoint(IPAddress.Any,port));}}else{var listener=new TcpListener(IPAddress.Any,port);listener.Server.ExclusiveAddressUse=true;try{listener.Start();}finally{listener.Stop();}}return true;}catch(SocketException){return false;}
  }
  public ServerStatus GetStatus(ServerInstance instance)
  {
   lock(gate){if(starting.ContainsKey(instance.Id))return new ServerStatus {InstanceId=instance.Id,Running=true,Message="正在准备运行环境"};
    if(running.TryGetValue(instance.Id,out var r)&&IsRunning(r))return new ServerStatus {InstanceId=instance.Id,Running=true,Ready=r.Ready,Message=r.Ready?"服务端已就绪（外网连通性未验证）":string.IsNullOrEmpty(instance.ReadyPattern)?"进程运行中，就绪状态未验证":"启动中，等待服务端就绪"};
    return new ServerStatus {InstanceId=instance.Id,Message="已停止"};}
  }
  private static bool IsAlive(Process p){try{return p!=null&&!p.HasExited;}catch{return false;}}
  private static bool IsRunning(RunningServer state)
  {
   if(state.Job!=IntPtr.Zero){var accounting=Marshal.AllocHGlobal(48);try{if(QueryInformationJobObject(state.Job,1,accounting,48,out var ignored))return Marshal.ReadInt32(accounting,40)>0;return true;}finally{Marshal.FreeHGlobal(accounting);}}
   return IsAlive(state.Process);
  }
  public async Task StartAsync(ServerInstance instance,IProgress<WorkProgress> progress,CancellationToken ct)
  {
   if(!PathSafety.IsWindows)throw new PlatformNotSupportedException("此启动器面向 Windows；当前系统仅可进行构建和核心测试。");
   InstanceRepository.Validate(instance);
   CancellationTokenSource startupCancellation;lock(gate){if(starting.ContainsKey(instance.Id)||(running.TryGetValue(instance.Id,out var old)&&IsRunning(old)))throw new InvalidOperationException("此实例已在启动或运行。");startupCancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);starting.Add(instance.Id,startupCancellation);}ct=startupCancellation.Token;
   RaiseStatus();
   try {
    if(!IsPortAvailable(instance.Port,instance.Udp))throw new InvalidOperationException("端口 "+instance.Port+" 已被占用，请先修改端口或停止其他服务端。");
    var directory=paths.InstanceDirectory(instance);var entry=PathSafety.ResolveUnder(directory,instance.EntryPoint);var working=PathSafety.ResolveUnder(directory,instance.WorkingDirectory);
    if(!File.Exists(entry))throw new FileNotFoundException("找不到启动入口。",entry);if(!Directory.Exists(working))throw new DirectoryNotFoundException("工作目录不存在。");
    UpdatePresetConfiguration(instance,directory,working);
    string runtime=instance.RuntimePath;
    if(instance.Runtime.Kind==RuntimeKind.Java||instance.Runtime.Kind==RuntimeKind.DotNet)runtime=(await runtimes.EnsureAsync(instance.Runtime,runtime,progress,ct).ConfigureAwait(false)).Executable;
    if(instance.Runtime.Kind==RuntimeKind.Script&&instance.Runtime.JavaMajor>0)runtime=(await runtimes.EnsureAsync(new RuntimeRequirement{Kind=RuntimeKind.Java,JavaMajor=instance.Runtime.JavaMajor,Architecture=instance.Runtime.Architecture},runtime,progress,ct).ConfigureAwait(false)).Executable;
    var args=new List<string>();string executable=entry;
    if(instance.Runtime.Kind==RuntimeKind.Java){executable=runtime;args.AddRange(JavaUtf8Arguments);args.Add("-Xms"+Math.Min(512,instance.MemoryMb)+"M");args.Add("-Xmx"+instance.MemoryMb+"M");args.Add("-jar");args.Add(entry);}
    else if(instance.Runtime.Kind==RuntimeKind.DotNet){var dll=Path.GetExtension(entry).Equals(".dll",StringComparison.OrdinalIgnoreCase)?entry:Path.ChangeExtension(entry,".dll");if(File.Exists(dll)){executable=runtime;args.Add(dll);}}
    args.AddRange(instance.Arguments);
    var ext=Path.GetExtension(entry);bool script=ext.Equals(".bat",StringComparison.OrdinalIgnoreCase)||ext.Equals(".cmd",StringComparison.OrdinalIgnoreCase);
    var psi=new ProcessStartInfo {FileName=executable,Arguments=WindowsCommandLine.Join(args),WorkingDirectory=working,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
    if(script){ValidateScriptValue(entry);foreach(var arg in args)ValidateScriptValue(arg);psi.FileName=Environment.GetEnvironmentVariable("COMSPEC")??"cmd.exe";psi.Arguments="/d /s /c \"\""+entry+"\" "+WindowsCommandLine.Join(args)+"\"";}
    foreach(var pair in instance.Environment)psi.EnvironmentVariables[pair.Key]=pair.Value??"";
    if(!string.IsNullOrEmpty(runtime)&&File.Exists(runtime)) {
     var bin=Path.GetDirectoryName(runtime);psi.EnvironmentVariables["PATH"]=bin+";"+(psi.EnvironmentVariables["PATH"]??"");
     if(Path.GetFileNameWithoutExtension(runtime).Equals("java",StringComparison.OrdinalIgnoreCase))psi.EnvironmentVariables["JAVA_HOME"]=Path.GetDirectoryName(bin);
     else if(Path.GetFileNameWithoutExtension(runtime).Equals("dotnet",StringComparison.OrdinalIgnoreCase)){psi.EnvironmentVariables["DOTNET_ROOT"]=bin;psi.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"]="0";}
    }
    ct.ThrowIfCancellationRequested();var state=new RunningServer {Instance=instance,StartedUtc=DateTime.UtcNow};
    lock(gate){if(running.TryGetValue(instance.Id,out var previous)){previous.Input?.Dispose();previous.Process?.Dispose();CloseJob(previous);}running[instance.Id]=state;}
    try {
     lock(gate){ct.ThrowIfCancellationRequested();StartInJob(state,psi);}Record(state,"[管理器] 启动进程 PID "+state.Process.Id+"；本地端口 "+instance.Port);
    }catch{lock(gate){running.Remove(instance.Id);}state.Input?.Dispose();state.Process?.Dispose();CloseJob(state);throw;}
   }finally{lock(gate)starting.Remove(instance.Id);startupCancellation.Dispose();RaiseStatus();}
  }
  private static void ValidateScriptValue(string value){if(value.IndexOfAny(new[]{'&','|','<','>','^','%','!','\r','\n','"'})>=0)throw new InvalidDataException("BAT/CMD 的路径和参数不能包含 shell 特殊字符。请将复杂命令放入你自己的脚本，或直接选择 EXE/JAR。");}
  private static void UpdatePresetConfiguration(ServerInstance instance,string directory,string working)
  {
   string file=null,key=null;if(instance.Kind==ServerKind.Vanilla||instance.Kind==ServerKind.Fabric||instance.Kind==ServerKind.Forge){file=Path.Combine(directory,"server.properties");key="server-port";}else if(instance.Kind==ServerKind.Terraria||instance.Kind==ServerKind.TModLoader){file=Path.Combine(working,"serverconfig.txt");key="port";}
   if(file!=null){var lines=File.Exists(file)?File.ReadAllLines(file).ToList():new List<string>();lines.RemoveAll(x=>x.StartsWith(key+"=",StringComparison.Ordinal));lines.Add(key+"="+instance.Port);File.WriteAllLines(file,lines,new UTF8Encoding(false));}
   if(instance.Kind==ServerKind.Forge&&Path.GetFileName(instance.EntryPoint).Equals("run.bat",StringComparison.OrdinalIgnoreCase)) {
    var library=Path.Combine(directory,"libraries","net","minecraftforge","forge");var arguments=Directory.Exists(library)?Directory.GetFiles(library,"win_args.txt",SearchOption.AllDirectories):new string[0];
    if(arguments.Length!=1)throw new InvalidDataException("Forge 的 win_args.txt 布局不可识别，请先修复安装。");
    PresetService.PrepareForgeLaunchScript(directory,arguments[0],instance.MemoryMb);
   }
   if(instance.Kind==ServerKind.TModLoader&&Path.GetFileName(instance.EntryPoint).Equals("start-tModLoaderServer.bat",StringComparison.OrdinalIgnoreCase))PresetService.PrepareTModLoaderLaunchScript(working,PathSafety.ResolveUnder(directory,instance.EntryPoint));
  }
  private static string SafeExitCode(Process p){try{return p.ExitCode.ToString();}catch{return "未知";}}
  private void Record(RunningServer state,string line)
  {
   // Sanitize before the queue, event, ready-pattern evaluation or disk persistence.
   if(logSanitizer!=null)line=logSanitizer(line);
   lock(gate){state.Lines.Enqueue(DateTime.Now.ToString("HH:mm:ss")+" "+line);while(state.Lines.Count>1500)state.Lines.Dequeue();
    if(!state.Ready&&!string.IsNullOrEmpty(state.Instance.ReadyPattern))try{if(Regex.IsMatch(line,state.Instance.ReadyPattern,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(100))){state.Ready=true;RaiseStatus();}}catch(ArgumentException){}catch(RegexMatchTimeoutException){}
    try{var logs=Path.Combine(paths.InstanceDirectory(state.Instance),".workbench-logs");Directory.CreateDirectory(logs);var file=Path.Combine(logs,"latest.log");if(File.Exists(file)&&new FileInfo(file).Length>5*1024*1024){var old=file+".1";if(File.Exists(old))File.Delete(old);File.Move(file,old);}File.AppendAllText(file,DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")+" "+line+Environment.NewLine,new UTF8Encoding(false));}catch(IOException){}catch(UnauthorizedAccessException){}
   }
   LogReceived?.Invoke(this,new ServerLogEventArgs {InstanceId=state.Instance.Id,Line=line});
  }
  private void RaiseStatus()=>StatusChanged?.Invoke(this,EventArgs.Empty);
  public IReadOnlyList<string> ReadLog(ServerInstance instance){lock(gate){if(running.TryGetValue(instance.Id,out var state))return state.Lines.ToList();}var file=Path.Combine(paths.InstanceDirectory(instance),".workbench-logs","latest.log");return File.Exists(file)?File.ReadLines(file).Reverse().Take(1500).Reverse().ToList():new List<string>();}
  public void Send(ServerInstance instance,string command)
  {
   if(command==null||command.IndexOfAny(new[]{'\r','\n'})>=0)throw new InvalidDataException("每次只能输入一行控制台命令。");
   lock(gate){if(!running.TryGetValue(instance.Id,out var state)||!IsRunning(state))throw new InvalidOperationException("服务端未运行。");state.Input.WriteLine(command);state.Input.Flush();}
  }
  public async Task<bool> StopAsync(ServerInstance instance,int timeoutSeconds,CancellationToken ct)
  {
   if(!GetStatus(instance).Running)return true;
   if(string.IsNullOrEmpty(instance.StopCommand))return false;
   Send(instance,instance.StopCommand);var end=DateTime.UtcNow.AddSeconds(timeoutSeconds);
   while(DateTime.UtcNow<end){ct.ThrowIfCancellationRequested();if(!GetStatus(instance).Running){await DrainOutputAsync(instance).ConfigureAwait(false);return true;}await Task.Delay(150,ct).ConfigureAwait(false);}if(!GetStatus(instance).Running){await DrainOutputAsync(instance).ConfigureAwait(false);return true;}return false;
  }
  private async Task DrainOutputAsync(ServerInstance instance)
  {
   RunningServer state;lock(gate)running.TryGetValue(instance.Id,out state);if(state==null)return;
   await state.OutputDone.ConfigureAwait(false);
   if(IsAlive(state.Process))return;
   if(state.Process.ExitCode!=0)throw new InvalidOperationException("服务端启动入口异常退出，代码 "+state.Process.ExitCode+"；未确认正常保存关服，请检查日志。");
  }
  internal async Task ForgetStoppedAsync(ServerInstance instance)
  {
   RunningServer state;var deadline=DateTime.UtcNow.AddSeconds(5);
   while(true) {
    lock(gate){if(!running.TryGetValue(instance.Id,out state))return;if(!IsRunning(state))break;}
    if(DateTime.UtcNow>=deadline)return;await Task.Delay(50).ConfigureAwait(false);
   }
   // Tunnel sessions have a fresh ID per launch. Once stopped, release their
   // native resources and in-memory log queue without requiring exit code zero.
   try{await state.OutputDone.ConfigureAwait(false);}catch(IOException){}catch(ObjectDisposedException){}
   lock(gate) {
    if(!running.TryGetValue(instance.Id,out var current)||!ReferenceEquals(current,state)||IsRunning(state))return;
    running.Remove(instance.Id);CloseJob(state);state.Input?.Dispose();state.Process?.Dispose();
   }
   RaiseStatus();
  }
  public void Kill(ServerInstance instance)
  {
   lock(gate){if(starting.TryGetValue(instance.Id,out var preparation))preparation.Cancel();if(!running.TryGetValue(instance.Id,out var state))return;if(state.Job!=IntPtr.Zero){if(!TerminateJobObject(state.Job,1))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法结束服务端进程组。");}else if(IsAlive(state.Process))state.Process.Kill();}RaiseStatus();
  }
  private static void CloseJob(RunningServer state){if(state.Job!=IntPtr.Zero){CloseHandle(state.Job);state.Job=IntPtr.Zero;}}
  public ResourceSample GetResources(ServerInstance instance)
  {
   var sample=new ResourceSample();lock(gate) {
    if(!running.TryGetValue(instance.Id,out var state)||!IsRunning(state))return sample;
    try {
     double cpu=0;var ids=new List<int>{state.Process.Id};
     if(state.Job!=IntPtr.Zero) {
      var buffer=Marshal.AllocHGlobal(8+8*2048);try{if(QueryInformationJobObject(state.Job,3,buffer,(uint)(8+8*2048),out var ignored)){int count=Math.Min(2048,Marshal.ReadInt32(buffer,4));ids.Clear();for(int i=0;i<count;i++)ids.Add((int)Marshal.ReadIntPtr(buffer,8+i*IntPtr.Size));}else sample.Error="无法读取进程组成员";}finally{Marshal.FreeHGlobal(buffer);}
      var accounting=Marshal.AllocHGlobal(48);try{if(QueryInformationJobObject(state.Job,1,accounting,48,out var ignored))cpu=(Marshal.ReadInt64(accounting,0)+Marshal.ReadInt64(accounting,8))/10000.0;else sample.Error="无法读取进程组CPU统计";}finally{Marshal.FreeHGlobal(accounting);}
     }
     if(state.Job==IntPtr.Zero)sample.Error="进程组不可用，仅能读取主进程资源";
     foreach(int id in ids)try{using(var process=Process.GetProcessById(id)){process.Refresh();sample.MemoryBytes+=process.WorkingSet64;sample.ProcessCount++;if(state.Job==IntPtr.Zero)cpu+=process.TotalProcessorTime.TotalMilliseconds;}}catch(ArgumentException){}catch(InvalidOperationException){}
     var now=DateTime.UtcNow;if(state.LastSample!=default(DateTime)){double elapsed=(now-state.LastSample).TotalMilliseconds;if(elapsed>0)sample.CpuPercent=Math.Max(0,Math.Min(100,(cpu-state.LastCpu)/elapsed/Environment.ProcessorCount*100));}state.LastCpu=cpu;state.LastSample=now;sample.Uptime=now-state.StartedUtc;
    }catch(Exception e){sample.Error="资源读取失败："+e.Message;}
   }return sample;
  }
  public void Dispose(){lock(gate){foreach(var preparation in starting.Values.ToArray())preparation.Cancel();foreach(var state in running.Values){CloseJob(state);state.Input?.Dispose();state.Process?.Dispose();}running.Clear();}}
  private void StartInJob(RunningServer state,ProcessStartInfo info)
  {
   IntPtr inputRead=IntPtr.Zero,inputWrite=IntPtr.Zero,outputRead=IntPtr.Zero,outputWrite=IntPtr.Zero,errorRead=IntPtr.Zero,errorWrite=IntPtr.Zero;
   var security=new SecurityAttributes{Length=Marshal.SizeOf(typeof(SecurityAttributes)),Inherit=true};var native=new ProcessInformation();IntPtr attributeList=IntPtr.Zero,handles=IntPtr.Zero;bool attributesInitialized=false;
   try {
    if(!CreatePipe(out inputRead,out inputWrite,ref security,0)||!CreatePipe(out outputRead,out outputWrite,ref security,0)||!CreatePipe(out errorRead,out errorWrite,ref security,0))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法创建服务端控制台管道。");
    foreach(var handle in new[]{inputWrite,outputRead,errorRead})if(!SetHandleInformation(handle,1,0))throw new Win32Exception(Marshal.GetLastWin32Error());
    // Restrict inheritance: simultaneous server launches must not keep each other's pipes open.
    IntPtr attributeSize=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref attributeSize);if(attributeSize==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
    attributeList=Marshal.AllocHGlobal(attributeSize);if(!InitializeProcThreadAttributeList(attributeList,1,0,ref attributeSize))throw new Win32Exception(Marshal.GetLastWin32Error());attributesInitialized=true;
    handles=Marshal.AllocHGlobal(IntPtr.Size*3);Marshal.WriteIntPtr(handles,0,inputRead);Marshal.WriteIntPtr(handles,IntPtr.Size,outputWrite);Marshal.WriteIntPtr(handles,IntPtr.Size*2,errorWrite);
    if(!UpdateProcThreadAttribute(attributeList,0,new IntPtr(0x20002),handles,new IntPtr(IntPtr.Size*3),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
    var startup=new StartupInfoEx{Startup=new StartupInfo{Size=Marshal.SizeOf(typeof(StartupInfoEx)),Flags=0x100,Input=inputRead,Output=outputWrite,Error=errorWrite},Attributes=attributeList};
    var environment=new StringBuilder();foreach(var key in info.EnvironmentVariables.Keys.Cast<string>().OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))environment.Append(key).Append('=').Append(info.EnvironmentVariables[key]).Append('\0');environment.Append('\0');
    var block=Marshal.StringToHGlobalUni(environment.ToString());
    try {
     // Windows 7 cannot nest jobs. If the host allows breakaway, retry outside its job.
     for(int attempt=0;attempt<2;attempt++) {
      var command=new StringBuilder(WindowsCommandLine.Quote(info.FileName)+" "+info.Arguments);
      if(!CreateProcess(null,command,IntPtr.Zero,IntPtr.Zero,true,0x08080404u|(attempt==1?0x01000000u:0u),block,info.WorkingDirectory,ref startup,out native))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法启动服务端进程。");
      state.Job=CreateJobObject(IntPtr.Zero,null);
      if(state.Job!=IntPtr.Zero&&AssignProcessToJobObject(state.Job,native.Process))break;
      int error=Marshal.GetLastWin32Error();TerminateProcess(native.Process,1);CloseHandle(native.Thread);CloseHandle(native.Process);native=new ProcessInformation();CloseJob(state);
      if(attempt==1)throw new Win32Exception(error,"无法建立服务端进程组；已取消启动，避免留下不受管理的游戏子进程。");
     }
    }finally{Marshal.FreeHGlobal(block);}
    if(killOnClose) {
     var limits=new ExtendedLimitInformation{Basic=new BasicLimitInformation{LimitFlags=0x2000}};int size=Marshal.SizeOf(typeof(ExtendedLimitInformation));var buffer=Marshal.AllocHGlobal(size);
     try{Marshal.StructureToPtr(limits,buffer,false);if(!SetInformationJobObject(state.Job,9,buffer,(uint)size))throw new Win32Exception(Marshal.GetLastWin32Error(),"无法设置退出时清理进程组，已取消启动。");}finally{Marshal.FreeHGlobal(buffer);}
    }
    CloseNative(ref inputRead);CloseNative(ref outputWrite);CloseNative(ref errorWrite);
    state.Input=new StreamWriter(new FileStream(new SafeFileHandle(inputWrite,true),FileAccess.Write),(state.Instance.Kind==ServerKind.Terraria||state.Instance.Kind==ServerKind.TModLoader)?(Encoding)new UnicodeEncoding(false,false):new UTF8Encoding(false)){AutoFlush=true};inputWrite=IntPtr.Zero;
    var stdout=new StreamReader(new FileStream(new SafeFileHandle(outputRead,true),FileAccess.Read),info.StandardOutputEncoding??Encoding.UTF8,true);outputRead=IntPtr.Zero;
    var stderr=new StreamReader(new FileStream(new SafeFileHandle(errorRead,true),FileAccess.Read),info.StandardErrorEncoding??Encoding.UTF8,true);errorRead=IntPtr.Zero;
    state.Process=Process.GetProcessById((int)native.ProcessId);state.Process.EnableRaisingEvents=true;
    state.Process.Exited+=(sender,e)=>{Record(state,"[管理器] 启动入口退出，代码 "+SafeExitCode(state.Process)+"；仍以进程组状态判断是否停服");RaiseStatus();};
    state.OutputDone=Task.WhenAll(ReadOutputAsync(state,stdout,false),ReadOutputAsync(state,stderr,true));
    if(ResumeThread(native.Thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法恢复服务端启动线程。");
   }catch{if(state.Job!=IntPtr.Zero)TerminateJobObject(state.Job,1);else if(native.Process!=IntPtr.Zero)TerminateProcess(native.Process,1);throw;}
   finally{if(attributeList!=IntPtr.Zero){if(attributesInitialized)DeleteProcThreadAttributeList(attributeList);Marshal.FreeHGlobal(attributeList);}if(handles!=IntPtr.Zero)Marshal.FreeHGlobal(handles);CloseNative(ref inputRead);CloseNative(ref inputWrite);CloseNative(ref outputRead);CloseNative(ref outputWrite);CloseNative(ref errorRead);CloseNative(ref errorWrite);CloseNative(ref native.Thread);CloseNative(ref native.Process);}
  }
  private async Task ReadOutputAsync(RunningServer state,StreamReader reader,bool error)
  {using(reader)try{string line;while((line=await reader.ReadLineAsync().ConfigureAwait(false))!=null)Record(state,(error?"[stderr] ":"")+line);}catch(IOException){}catch(ObjectDisposedException){}}
  private static void CloseNative(ref IntPtr handle){if(handle!=IntPtr.Zero){CloseHandle(handle);handle=IntPtr.Zero;}}
  [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes {public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo {public int Size;public string Reserved,Desktop,Title;public int X,Y,Width,Height,CountX,CountY,Fill,Flags;public short Show,ReservedSize;public IntPtr ReservedPointer,Input,Output,Error;}
  [StructLayout(LayoutKind.Sequential)]private struct StartupInfoEx {public StartupInfo Startup;public IntPtr Attributes;}
  [StructLayout(LayoutKind.Sequential)]private struct ProcessInformation {public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
  [StructLayout(LayoutKind.Sequential)]private struct BasicLimitInformation {public long ProcessUserTimeLimit,JobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
  [StructLayout(LayoutKind.Sequential)]private struct IoCounters {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
  [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimitInformation {public BasicLimitInformation Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref SecurityAttributes attributes,int size);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInformation process);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
  [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(IntPtr list);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(IntPtr thread);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool TerminateProcess(IntPtr process,uint code);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateJobObject(IntPtr attributes,string name);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool TerminateJobObject(IntPtr job,uint exitCode);
  [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool QueryInformationJobObject(IntPtr job,int infoClass,IntPtr info,uint length,out uint returned);
  [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(IntPtr job,int infoClass,IntPtr info,uint length);
 }
}
