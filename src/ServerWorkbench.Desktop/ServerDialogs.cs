using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using ServerWorkbench.Core;

namespace ServerWorkbench.Desktop
{
 internal sealed class ServerEditor : Form
 {
  public ServerInstance Result { get; private set; }
  private readonly TextBox name=Ui.Text(),entry=Ui.Text(),work=Ui.Text(),args=Ui.Text("",true),env=Ui.Text("",true),runtimePath=Ui.Text(),runtimeVersion=Ui.Text(),framework=Ui.Text(),rollForward=Ui.Text(),stop=Ui.Text(),ready=Ui.Text(),mods=Ui.Text();
  private readonly ComboBox runtimeKind=Ui.Combo();
  private readonly TextBox storage=Ui.Text();
  private readonly bool creating;
  private readonly TextBox saveCommand=Ui.Text(),saveDone=Ui.Text(),savePause=Ui.Text(),saveResume=Ui.Text(),emergencyCommand=Ui.Text(),savePaths=Ui.Text("",true);
  private readonly NumericUpDown autoSave=new NumericUpDown{Minimum=0,Maximum=1440,Dock=DockStyle.Fill};
  private readonly NumericUpDown port=new NumericUpDown { Minimum=0,Maximum=65535,Dock=DockStyle.Fill },memory=new NumericUpDown { Minimum=256,Maximum=1048576,Increment=256,Dock=DockStyle.Fill },java=new NumericUpDown { Minimum=0,Maximum=99,Dock=DockStyle.Fill };
  private readonly CheckBox udp=new CheckBox { Text="UDP（未勾选为 TCP）",AutoSize=true };
  private readonly ServerInstance instance;
  private ServerEditor(ServerInstance source,bool creating)
  {
   this.creating=creating;
   instance=JsonConvert.DeserializeObject<ServerInstance>(JsonConvert.SerializeObject(source));
   Text="启动配置 · "+source.Name;Font=new Font("Microsoft YaHei UI",9);Width=800;Height=750;MinimumSize=new Size(680,550);StartPosition=FormStartPosition.CenterParent;BackColor=Ui.Background;Padding=new Padding(22);
   var fields=Ui.Fields();var scroll=new Panel { Dock=DockStyle.Fill,AutoScroll=true };scroll.Controls.Add(fields);
   var tip=Ui.Label("入口、工作目录和 Mod 目录相对于实例目录。参数每行一个，不需要额外添加引号。",true);tip.Dock=DockStyle.Top;tip.Height=48;tip.AutoSize=false;
   Ui.Field(fields,"实例名称",name);Ui.Field(fields,"服务器存放目录",creating?FolderPicker(storage):storage);storage.ReadOnly=!creating;storage.Text=creating?"":string.IsNullOrEmpty(source.StorageDirectory)?"默认 Data/Instances/"+source.Id:source.StorageDirectory;
   Ui.Field(fields,"目录说明",Ui.Label(creating?"可选父目录，创建时建立独立子目录；留空使用默认 Data。":"已有实例位置只读，不会因编辑配置而移动存档。",true));
   Ui.Field(fields,"启动入口",entry);Ui.Field(fields,"工作目录",work);Ui.Field(fields,"启动参数",args,92);Ui.Field(fields,"环境变量",env,76);
   Ui.Field(fields,"运行方式",runtimeKind);Ui.Field(fields,"运行时程序",Picker(runtimePath,"运行时程序|java.exe;dotnet.exe|所有文件|*.*"));Ui.Field(fields,"Java 主版本",java);Ui.Field(fields,".NET 版本",runtimeVersion);Ui.Field(fields,".NET Framework 名称",framework);Ui.Field(fields,".NET RollForward",rollForward);Ui.Field(fields,"端口（0 为未指定）",port);Ui.Field(fields,"端口协议",udp);Ui.Field(fields,"内存上限 / MB",memory);Ui.Field(fields,"正常停止命令",stop);Ui.Field(fields,"就绪日志正则",ready);Ui.Field(fields,"Mod 目录",mods);Ui.Field(fields,"保存命令",saveCommand);Ui.Field(fields,"保存完成日志正则",saveDone);Ui.Field(fields,"暂停自动保存命令",savePause);Ui.Field(fields,"恢复自动保存命令",saveResume);Ui.Field(fields,"不保存退出命令",emergencyCommand);Ui.Field(fields,"存档路径（每行一个）",savePaths,75);Ui.Field(fields,"定时保存 / 分钟（0关闭）",autoSave);
   var foot=Ui.Toolbar();foot.Dock=DockStyle.Bottom;
   var save=Ui.Button("保存配置",()=>Ui.Try(this,Save),true);var cancel=Ui.Button("取消",()=>DialogResult=DialogResult.Cancel);foot.Controls.Add(save);foot.Controls.Add(cancel);AcceptButton=save;CancelButton=cancel;
   Controls.Add(scroll);Controls.Add(tip);Controls.Add(foot);
   name.Text=instance.Name;entry.Text=instance.EntryPoint;work.Text=instance.WorkingDirectory;args.Lines=instance.Arguments.ToArray();env.Lines=instance.Environment.Select(x=>x.Key+"="+x.Value).ToArray();runtimePath.Text=instance.RuntimePath;runtimeKind.Items.AddRange(Enum.GetValues(typeof(RuntimeKind)).Cast<object>().ToArray());runtimeKind.SelectedItem=instance.Runtime.Kind;java.Value=instance.Runtime.JavaMajor;runtimeVersion.Text=instance.Runtime.Version;framework.Text=instance.Runtime.Framework;rollForward.Text=instance.Runtime.RollForward;port.Value=instance.Port;udp.Checked=instance.Udp;memory.Value=Math.Max(256,Math.Min(1048576,instance.MemoryMb));stop.Text=instance.StopCommand;ready.Text=instance.ReadyPattern;mods.Text=instance.ModDirectory;var profile=instance.Save??new SaveProfile();saveCommand.Text=profile.Command;saveDone.Text=profile.CompletedPattern;savePause.Text=profile.PauseCommand;saveResume.Text=profile.ResumeCommand;emergencyCommand.Text=profile.EmergencyCommand;savePaths.Lines=profile.Paths.ToArray();autoSave.Value=Math.Max(0,Math.Min(1440,instance.AutoSaveMinutes));Ui.ConfigureWindow(this);
  }
  internal static Control Picker(TextBox text,string filter)
  {
   var p=new TableLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,ColumnStyles={new ColumnStyle(SizeType.Percent,100),new ColumnStyle(SizeType.AutoSize)} };
   p.Controls.Add(text,0,0);p.Controls.Add(Ui.Button("浏览",()=> { using(var d=new OpenFileDialog { Filter=filter })if(d.ShowDialog()==DialogResult.OK)text.Text=d.FileName; }),1,0);return p;
  }
  internal static Control FolderPicker(TextBox text)
  {
   var row=new TableLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,ColumnStyles={new ColumnStyle(SizeType.Percent,100),new ColumnStyle(SizeType.AutoSize)} };
   row.Controls.Add(text,0,0);row.Controls.Add(Ui.Button("选择目录",()=> {using(var dialog=new FolderBrowserDialog{Description="选择服务器父目录，创建时会建立独立子目录。",SelectedPath=Directory.Exists(text.Text)?text.Text:""})if(dialog.ShowDialog()==DialogResult.OK)text.Text=dialog.SelectedPath;}),1,0);return row;
  }
  internal static string NewStorageDirectory(string parent,string instanceName)
  {
   if(string.IsNullOrWhiteSpace(parent))return "";
   parent=Path.GetFullPath(parent.Trim());if(!Directory.Exists(parent))throw new DirectoryNotFoundException("所选服务器父目录不存在，请选择已有目录。");
   var invalid=Path.GetInvalidFileNameChars();string safe=new string(instanceName.Where(c=>!invalid.Contains(c)&&c>31).ToArray()).Trim().TrimEnd('.');
   if(safe.Length>40)safe=safe.Substring(0,40).TrimEnd(' ','.');if(string.IsNullOrEmpty(safe))safe="Server";
   return Path.Combine(parent,safe+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
  }
  private void Save()
  {
   if(string.IsNullOrWhiteSpace(name.Text)||string.IsNullOrWhiteSpace(entry.Text))throw new InvalidOperationException("请填写实例名称和启动入口。");
   if(Path.IsPathRooted(entry.Text)||Path.IsPathRooted(work.Text)||Path.IsPathRooted(mods.Text))throw new InvalidOperationException("入口、工作目录和 Mod 目录必须填写实例目录内的相对路径。");
   var variables=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(string line in env.Lines.Where(x=>!string.IsNullOrWhiteSpace(x))) { int eq=line.IndexOf('=');if(eq<=0)throw new InvalidOperationException("环境变量使用每行 名称=值 的格式。");variables.Add(line.Substring(0,eq).Trim(),line.Substring(eq+1)); }
   if(!string.IsNullOrWhiteSpace(ready.Text))new System.Text.RegularExpressions.Regex(ready.Text);
   instance.Name=name.Text.Trim();if(creating)instance.StorageDirectory=NewStorageDirectory(storage.Text,instance.Name);instance.EntryPoint=entry.Text.Trim();instance.WorkingDirectory=string.IsNullOrWhiteSpace(work.Text)?".":work.Text.Trim();instance.Arguments=args.Lines.Where(x=>!string.IsNullOrEmpty(x)).ToList();instance.Environment=variables;instance.RuntimePath=runtimePath.Text.Trim();instance.Runtime.Kind=(RuntimeKind)runtimeKind.SelectedItem;instance.Runtime.JavaMajor=(int)java.Value;instance.Runtime.Version=runtimeVersion.Text.Trim();instance.Runtime.Framework=framework.Text.Trim();instance.Runtime.RollForward=rollForward.Text.Trim();instance.Port=(int)port.Value;instance.Udp=udp.Checked;instance.MemoryMb=(int)memory.Value;instance.StopCommand=stop.Text;instance.ReadyPattern=ready.Text;instance.ModDirectory=string.IsNullOrWhiteSpace(mods.Text)?"mods":mods.Text.Trim();if(!string.IsNullOrWhiteSpace(saveDone.Text))new System.Text.RegularExpressions.Regex(saveDone.Text);instance.Save=new SaveProfile{Command=saveCommand.Text.Trim(),CompletedPattern=saveDone.Text.Trim(),PauseCommand=savePause.Text.Trim(),ResumeCommand=saveResume.Text.Trim(),EmergencyCommand=emergencyCommand.Text.Trim(),Paths=savePaths.Lines.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>x.Trim()).ToList()};instance.AutoSaveMinutes=(int)autoSave.Value;InstanceRepository.Validate(instance);Result=instance;DialogResult=DialogResult.OK;
  }
  public static ServerInstance Edit(IWin32Window owner,ServerInstance instance,bool creating=false)
  { using(var f=new ServerEditor(instance,creating))return f.ShowDialog(owner)==DialogResult.OK?f.Result:null; }
 }
 internal sealed class CreateServerDialog : Form
 {
  private readonly AppServices services;
  private readonly TabControl tabs=new TabControl { Dock=DockStyle.Fill };
  private readonly ComboBox kind=Ui.Combo(),version=Ui.Combo(),loader=Ui.Combo();
  private readonly TextBox presetStorage=Ui.Text();
  private readonly TextBox presetName=Ui.Text("我的 Minecraft 服务器"),runtime=Ui.Text(),world=Ui.Text("World"),password=Ui.Text(),source=Ui.Text();
  private readonly NumericUpDown port=new NumericUpDown { Minimum=1,Maximum=65535,Value=25565,Dock=DockStyle.Fill },memory=new NumericUpDown { Minimum=256,Maximum=1048576,Increment=256,Value=2048,Dock=DockStyle.Fill },players=new NumericUpDown { Minimum=1,Maximum=255,Value=8,Dock=DockStyle.Fill };
  private readonly CheckBox eula=new CheckBox { Text="我已阅读并同意 Minecraft EULA",AutoSize=true };
  private readonly ListBox candidates=new ListBox { Dock=DockStyle.Fill,IntegralHeight=false,HorizontalScrollbar=true };
  private readonly Label notes=Ui.Label("选择官方服务端 ZIP、目录、JAR、EXE 或启动脚本，也可粘贴 HTTPS 文件直链。",true);
  private ImportDraft draft;
  public ServerInstance CreatedInstance { get; private set; }
  private ServerKind Kind => kind.SelectedItem is ServerKind?(ServerKind)kind.SelectedItem:ServerKind.Vanilla;
  public CreateServerDialog(AppServices services)
  {
   this.services=services;Text="创建服务器";Font=new Font("Microsoft YaHei UI",9);Width=820;Height=740;MinimumSize=new Size(700,570);StartPosition=FormStartPosition.CenterParent;Padding=new Padding(20);BackColor=Ui.Background;
   var presetTab=new TabPage("游戏预设") { Padding=new Padding(16),BackColor=Color.White };var customTab=new TabPage("通用导入") { Padding=new Padding(16),BackColor=Color.White };tabs.TabPages.Add(presetTab);tabs.TabPages.Add(customTab);
   var fields=Ui.Fields();var scroll=new Panel { Dock=DockStyle.Fill,AutoScroll=true };scroll.Controls.Add(fields);presetTab.Controls.Add(scroll);
   kind.Items.AddRange(new object[]{ServerKind.Vanilla,ServerKind.Fabric,ServerKind.Forge,ServerKind.Terraria,ServerKind.TModLoader});kind.Format += (s,e)=>e.Value=Ui.KindName((ServerKind)e.ListItem);kind.FormattingEnabled=true;
   Ui.Field(fields,"实例名称",presetName);Ui.Field(fields,"服务器存放目录",ServerEditor.FolderPicker(presetStorage));Ui.Field(fields,"目录说明",Ui.Label("可选父目录，创建时建立独立子目录；留空使用默认 Data。",true));Ui.Field(fields,"服务端类型",kind);Ui.Field(fields,"游戏 / 发布版本",WithButton(version,"读取版本",LoadVersions));Ui.Field(fields,"加载器版本",WithButton(loader,"读取加载器",LoadLoaders));Ui.Field(fields,"服务端端口",port);Ui.Field(fields,"内存上限 / MB",memory);Ui.Field(fields,"已有运行时（可选）",ServerEditor.Picker(runtime,"运行时程序|java.exe;dotnet.exe|所有文件|*.*"));Ui.Field(fields,"世界名称",world);Ui.Field(fields,"服务器密码",password);Ui.Field(fields,"最大玩家数",players);
   var eu=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill };eu.Controls.Add(eula);eu.Controls.Add(Ui.Button("阅读 EULA",()=>Ui.Try(this,()=>Ui.Open("https://www.minecraft.net/eula"))));Ui.Field(fields,"Minecraft 协议",eu);int eulaRow=fields.RowCount-1;var eulaLabel=fields.GetControlFromPosition(0,eulaRow);
   var help=Ui.Label("版本来自官方发布源。Win7 / 8.1 仅使用已验证兼容的游戏与运行时；已有 Java 可在上方指定。安装过程不会自动接受 EULA 或修改全局环境。",true);Ui.Field(fields,"说明",help);
   kind.SelectedIndexChanged += (s,e)=> { version.Items.Clear();loader.Items.Clear();bool mc=IsMinecraft(Kind);port.Value=mc?25565:7777;presetName.Text=mc?"我的 Minecraft 服务器":"我的泰拉瑞亚服务器";eula.Enabled=mc;eu.Visible=mc;eulaLabel.Visible=mc;fields.RowStyles[eulaRow].SizeType=mc?SizeType.AutoSize:SizeType.Absolute;fields.RowStyles[eulaRow].Height=0;if(!mc)eula.Checked=false;help.Text=mc?"版本来自官方发布源。Minecraft 需要阅读并同意 EULA；可指定已有 Java 或准备私有 Java。旧系统兼容性单独判断。":"版本来自官方发布源。将准备所选 Terraria / tModLoader 服务端和独立存档；游戏运行时与旧系统兼容性单独判断。";memory.Enabled=mc;world.Enabled=!mc;password.Enabled=!mc;players.Enabled=!mc;loader.Enabled=Kind==ServerKind.Fabric||Kind==ServerKind.Forge; };
   kind.SelectedIndex=0;
   var customLayout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,RowStyles={new RowStyle(SizeType.AutoSize),new RowStyle(SizeType.AutoSize),new RowStyle(SizeType.AutoSize),new RowStyle(SizeType.Percent,100),new RowStyle(SizeType.AutoSize)} };
   var sourceRow=Ui.Fields();Ui.Field(sourceRow,"包 / 目录 / URL",source);var browse=Ui.Toolbar();browse.Controls.Add(Ui.Button("选择文件",()=> { using(var d=new OpenFileDialog { Filter="服务端文件|*.zip;*.jar;*.exe;*.bat;*.cmd|所有文件|*.*" })if(d.ShowDialog(this)==DialogResult.OK)source.Text=d.FileName; }));browse.Controls.Add(Ui.Button("选择目录",()=> { using(var d=new FolderBrowserDialog())if(d.ShowDialog(this)==DialogResult.OK)source.Text=d.SelectedPath; }));browse.Controls.Add(Ui.Button("分析导入内容",()=>Ui.Try(this,Stage),true));
   notes.MaximumSize=new Size(700,0);customLayout.Controls.Add(sourceRow,0,0);customLayout.Controls.Add(browse,0,1);customLayout.Controls.Add(Ui.Label("选择启动入口；下一步可以补充运行时、参数、端口和停止命令。"),0,2);customLayout.Controls.Add(candidates,0,3);customLayout.Controls.Add(notes,0,4);customTab.Controls.Add(customLayout);
   var bottom=Ui.Toolbar();bottom.Dock=DockStyle.Bottom;bottom.Controls.Add(Ui.Button("创建服务器",()=>Ui.Try(this,Create),true));bottom.Controls.Add(Ui.Button("取消",()=>DialogResult=DialogResult.Cancel));Controls.Add(tabs);Controls.Add(bottom);Ui.ConfigureWindow(this);
  }
  private static bool IsMinecraft(ServerKind k)=>k==ServerKind.Vanilla||k==ServerKind.Fabric||k==ServerKind.Forge;
  private Control WithButton(Control c,string text,Action action)
  { var p=new TableLayoutPanel { ColumnCount=2,Dock=DockStyle.Fill,AutoSize=true,ColumnStyles={new ColumnStyle(SizeType.Percent,100),new ColumnStyle(SizeType.AutoSize)} };p.Controls.Add(c,0,0);p.Controls.Add(Ui.Button(text,()=>Ui.Try(this,action)),1,0);return p; }
  private void LoadVersions()
  {
   var list=ProgressDialog.Run(this,"读取官方版本",(p,c)=>services.Presets.GetVersionsAsync(Kind,c));version.Items.Clear();version.Items.AddRange(list.Cast<object>().ToArray());if(version.Items.Count>0)version.SelectedIndex=0;else throw new InvalidOperationException("官方源没有返回可安装版本，请使用通用导入。");
  }
  private void LoadLoaders()
  {
   if(Kind!=ServerKind.Fabric&&Kind!=ServerKind.Forge)throw new InvalidOperationException("当前服务端不需要独立加载器版本。");
   var v=version.SelectedItem as VersionOption;if(v==null)throw new InvalidOperationException("请先读取并选择游戏版本。");
   var list=ProgressDialog.Run(this,"读取加载器版本",(p,c)=>services.Presets.GetLoaderVersionsAsync(Kind,v.Id,c));loader.Items.Clear();loader.Items.AddRange(list.Cast<object>().ToArray());if(loader.Items.Count>0)loader.SelectedIndex=0;else throw new InvalidOperationException("没有找到匹配的加载器构建。");
  }
  private void Stage()
  {
   if(string.IsNullOrWhiteSpace(source.Text))throw new InvalidOperationException("请选择服务端文件、目录或填写 HTTPS 文件直链。");
   var next=ProgressDialog.Run(this,"分析服务端",(p,c)=>services.Importer.StageAsync(source.Text.Trim(),p,c));if(draft!=null)services.Importer.Discard(draft);draft=next;candidates.Items.Clear();candidates.Items.AddRange(draft.Candidates.Cast<object>().ToArray());if(candidates.Items.Count>0)candidates.SelectedIndex=0;
   notes.Text=string.Join(Environment.NewLine,draft.Notes);if(draft.Candidates.Count==0)notes.Text="没有识别到可执行入口。创建时可手动填写相对路径。"+Environment.NewLine+notes.Text;
  }
  private void Create()
  {
   if(tabs.SelectedIndex==0)
   {
    if(string.IsNullOrWhiteSpace(presetName.Text))throw new InvalidOperationException("请填写实例名称。");
    var v=version.SelectedItem as VersionOption;if(v==null)throw new InvalidOperationException("请先读取并选择版本。");
    var l=loader.SelectedItem as VersionOption;if((Kind==ServerKind.Fabric||Kind==ServerKind.Forge)&&l==null)throw new InvalidOperationException("请先选择加载器版本。");
    if(IsMinecraft(Kind)&&!eula.Checked)throw new InvalidOperationException("创建 Minecraft 服务端前需要阅读并同意 EULA。");
    var req=new CreateServerRequest {Name=presetName.Text.Trim(),StorageDirectory=ServerEditor.NewStorageDirectory(presetStorage.Text,presetName.Text.Trim()),Kind=Kind,GameVersion=v.Id,LoaderVersion=l?.Id??"",AcceptEula=eula.Checked,Port=(int)port.Value,MemoryMb=(int)memory.Value,RuntimePath=runtime.Text.Trim(),WorldName=world.Text.Trim(),Password=password.Text,MaxPlayers=(int)players.Value};
    CreatedInstance=ProgressDialog.Run(this,"安装服务器",(p,c)=>services.Presets.InstallAsync(req,p,c));
   }
   else
   {
    if(draft==null)throw new InvalidOperationException("请先分析导入内容。");
    var candidate=candidates.SelectedItem as EntryCandidate;if(candidate!=null&&!candidate.Compatible)throw new InvalidOperationException("所选入口不兼容当前 Windows，请选择 Windows 服务端或其他入口。");
    var s=new ServerInstance {Name="自定义服务器",Kind=ServerKind.Custom,EntryPoint=candidate?.RelativePath??"",Runtime=candidate?.Runtime??new RuntimeRequirement(),SourceUrl=draft.Source};
    var edited=ServerEditor.Edit(this,s,true);if(edited==null)return;
    CreatedInstance=ProgressDialog.Run(this,"创建导入实例",(p,c)=>services.Importer.CommitAsync(draft,edited,p,c));draft=null;
   }
   DialogResult=DialogResult.OK;
  }
  protected override void Dispose(bool disposing)
  { if(disposing&&draft!=null) { try { services.Importer.Discard(draft); }catch { }draft=null; }base.Dispose(disposing); }
 }
}
