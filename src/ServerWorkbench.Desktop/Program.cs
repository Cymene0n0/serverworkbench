using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using ServerWorkbench.Core;
namespace ServerWorkbench.Desktop
{
 internal static class Program
 {
  private static string SettingsPath=>Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.json");
  private static string LocalSettingsPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ServerWorkbench","settings.json");
  [STAThread] private static void Main()
  {
   using(var mutex=new Mutex(true,@"Local\ServerWorkbench-Desktop-v01",out bool first)) {
    if(!first){MessageBox.Show("开服工坊已经运行，请检查任务栏或系统托盘。","开服工坊");return;}
    ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException+=(s,e)=>Report(e.Exception);
    AppDomain.CurrentDomain.UnhandledException+=(s,e)=>Report(e.ExceptionObject as Exception??new Exception("未知异常"));
    try {
     string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Data");
     var configuration=File.Exists(LocalSettingsPath)&&(!File.Exists(SettingsPath)||File.GetLastWriteTimeUtc(LocalSettingsPath)>File.GetLastWriteTimeUtc(SettingsPath))?LocalSettingsPath:SettingsPath;
     if(File.Exists(configuration)){var value=JsonConvert.DeserializeObject<Settings>(File.ReadAllText(configuration));if(!string.IsNullOrWhiteSpace(value?.DataRoot))root=Path.IsPathRooted(value.DataRoot)?value.DataRoot:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,value.DataRoot);}
     while(true)try{Directory.CreateDirectory(root);string probe=Path.Combine(root,".write-test-"+Guid.NewGuid().ToString("N"));File.WriteAllText(probe,"");File.Delete(probe);break;}catch(Exception e){MessageBox.Show("数据目录不可写，请选择其他目录。\n"+e.Message,"选择数据目录");using(var d=new FolderBrowserDialog{Description="选择可写的数据目录，世界和配置将保存在这里。"}){if(d.ShowDialog()!=DialogResult.OK)return;root=d.SelectedPath;SaveDataRoot(root);}}
     using(var services=AppBootstrap.Create(root,new DpapiSecretProtector()))using(var window=new MainForm(services))Application.Run(window);
    }catch(Exception e){Report(e);}
   }
  }
  internal static void SaveDataRoot(string path){var value=new Settings{DataRoot=Path.GetFullPath(path)};try{AtomicJson.Write(SettingsPath,value);}catch(UnauthorizedAccessException){AtomicJson.Write(LocalSettingsPath,value);}catch(IOException){AtomicJson.Write(LocalSettingsPath,value);}}
  private static void Report(Exception e)
  {try{File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"error.log"),DateTime.Now+" "+e+Environment.NewLine);}catch{}MessageBox.Show(e.GetBaseException().Message+"\n详细信息记录在程序旁的 error.log。","开服工坊 · 操作失败",MessageBoxButtons.OK,MessageBoxIcon.Error);}
  private sealed class Settings{public int SchemaVersion=1;public string DataRoot;}
 }
}
