using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Newtonsoft.Json;
using ServerWorkbench.Core;

namespace ServerWorkbench.Desktop
{
 public sealed class MainForm : Form
 {
  private readonly AppServices services;
  private readonly Panel content=new Panel { Dock=DockStyle.Fill,Padding=new Padding(26,22,26,18),BackColor=Ui.Background };
  private readonly Label pageTitle=Ui.Title("服务器"),subtitle=Ui.Label("把复杂的开服步骤，整理成一个工作台。",true),statusText=Ui.Label("就绪",true);
  private readonly Panel body=new Panel { Dock=DockStyle.Fill };
  private readonly Dictionary<string,Button> navigation=new Dictionary<string,Button>();
  private IReadOnlyList<ServerInstance> instances=new List<ServerInstance>();
  private string selectedId,currentPage="服务器";
  private DataGridView serverGrid,installedGrid,searchGrid;
  private TextBox logBox,queryBox,networkBox,tunnelBox;
  private Button tunnelInstall,tunnelConfigure,tunnelStart,tunnelStop,tunnelCopy;
  private ComboBox onlineVersions;
  private ListBox backupList,saveList;
  private ResourceChart resourceChart;
  private Label instanceStatus;
  private readonly NotifyIcon tray;
  private readonly System.Windows.Forms.Timer refreshTimer=new System.Windows.Forms.Timer { Interval=3000 };
  private bool exiting,refreshing,dpiConfigured;
  private ServerInstance Selected=>instances.FirstOrDefault(x=>x.Id==selectedId);
  public MainForm(AppServices services)
  {
   this.services=services;Text="开服工坊 · ServerWorkbench";Font=new Font("Microsoft YaHei UI",9);BackColor=Ui.Background;Size=new Size(1250,850);MinimumSize=new Size(1050,720);StartPosition=FormStartPosition.CenterScreen;
   var sidebar=new Panel { Dock=DockStyle.Left,Width=190,BackColor=Ui.Ink,Padding=new Padding(16,24,16,18) };
   var brand=new Label { Text="开服工坊",Font=new Font("Microsoft YaHei UI",20,FontStyle.Bold),ForeColor=Color.White,Dock=DockStyle.Top,Height=47 };
   var tagline=new Label { Text="SERVER WORKBENCH  /  0.1",Font=new Font("Segoe UI",8),ForeColor=Color.FromArgb(157,181,195),Dock=DockStyle.Top,Height=43 };
   var nav=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(0,14,0,0) };
   foreach(string name in new[]{"服务器","创建服务器","Mod 管理","备份与恢复","环境检测","联机指引","设置与说明"})
   {
    string captured=name;var b=new Button { Text=name,Width=158,Height=43,FlatStyle=FlatStyle.Flat,ForeColor=Color.FromArgb(208,220,230),BackColor=Ui.Ink,TextAlign=ContentAlignment.MiddleLeft,Padding=new Padding(12,0,0,0),Cursor=Cursors.Hand,Margin=new Padding(0,0,0,7) };b.FlatAppearance.BorderSize=0;b.Click+=(s,e)=>Ui.Try(this,()=> {if(captured=="创建服务器")CreateServer();else ShowPage(captured);});navigation[name]=b;nav.Controls.Add(b);
   }
   var sideBottom=new Label { Text="本机开服 · 数据由你掌握\n关闭窗口后继续在托盘运行",Dock=DockStyle.Bottom,Height=54,ForeColor=Color.FromArgb(157,181,195),Font=new Font("Microsoft YaHei UI",8) };
   var brandPanel=new Panel { Dock=DockStyle.Top,Height=90 };brandPanel.Controls.Add(tagline);brandPanel.Controls.Add(brand);sidebar.Controls.Add(nav);sidebar.Controls.Add(brandPanel);sidebar.Controls.Add(sideBottom);
   var heading=new Panel { Dock=DockStyle.Top,Height=88 };pageTitle.Location=new Point(0,0);subtitle.Location=new Point(1,44);heading.Controls.Add(pageTitle);heading.Controls.Add(subtitle);
   statusText.Dock=DockStyle.Bottom;statusText.Height=26;statusText.AutoSize=false;content.Controls.Add(body);content.Controls.Add(heading);content.Controls.Add(statusText);Controls.Add(content);Controls.Add(sidebar);
   var menu=new ContextMenuStrip();menu.Items.Add("打开开服工坊",null,(s,e)=>ShowFromTray());menu.Items.Add("紧急结束全部服务器（不保存）",null,(s,e)=>Ui.Try(this,()=>{if(MessageBox.Show(this,"跳过保存并结束所有运行中的服务端？","紧急结束",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes)foreach(var instance in instances.Where(x=>services.Processes.GetStatus(x).Running))services.Processes.Kill(instance);}));menu.Items.Add("退出并停止服务器",null,(s,e)=>Ui.Try(this,ExitApplication));
   tray=new NotifyIcon { Text="开服工坊 ServerWorkbench",Icon=SystemIcons.Application,ContextMenuStrip=menu,Visible=true };tray.DoubleClick+=(s,e)=>ShowFromTray();
   services.Processes.LogReceived+=ProcessLog;services.Processes.StatusChanged+=ProcessStatus;refreshTimer.Tick+=(s,e)=>RefreshStatus();refreshTimer.Start();FormClosing+=ClosingToTray;
   ReloadInstances();ShowPage("服务器");Ui.ConfigureWindow(this);dpiConfigured=true;
  }
  private void ReloadInstances()
  {
   instances=services.Repository.List();if(!instances.Any(x=>x.Id==selectedId))selectedId=instances.FirstOrDefault()?.Id;
  }
  private void ShowPage(string page)
  {
   currentPage=page;resourceChart=null;saveList=null;serverGrid=null;installedGrid=null;searchGrid=null;logBox=null;backupList=null;networkBox=null;tunnelBox=null;tunnelInstall=null;tunnelConfigure=null;tunnelStart=null;tunnelStop=null;tunnelCopy=null;instanceStatus=null;
   refreshing=true;try{foreach(Control old in body.Controls.Cast<Control>().ToArray())old.Dispose();}finally{refreshing=false;}pageTitle.Text=page;
   foreach(var n in navigation)n.Value.BackColor=n.Key==page?Color.FromArgb(48,73,91):Ui.Ink;
   switch(page)
   {
    case "服务器":subtitle.Text="一个列表管理所有实例，日志与控制台就在下面。";BuildServers();break;
    case "Mod 管理":subtitle.Text="安装前匹配版本与加载器；变更前停止服务器并备份。";BuildMods();break;
    case "备份与恢复":subtitle.Text="在停服状态保存完整实例，恢复前保留当前数据。";BuildBackups();break;
    case "环境检测":subtitle.Text="管理器、游戏与运行时各自有独立的兼容要求。";BuildEnvironment();break;
    case "联机指引":subtitle.Text="本机、局域网与可信中转节点；公网连通性需要异地实测。";BuildNetwork();break;
    default:subtitle.Text="本地数据、使用说明与初版边界。";BuildSettings();break;
   }
   // Page builders use the same 96-DPI dimensions as the initial form. Only
   // newly created page roots need scaling after the form's first auto-scale.
   if(dpiConfigured)
   {
    var factor=new SizeF(CurrentAutoScaleDimensions.Width/96f,CurrentAutoScaleDimensions.Height/96f);
    body.SuspendLayout();
    try {foreach(Control pageRoot in body.Controls)pageRoot.Scale(factor);}
    finally {body.ResumeLayout(true);}
   }
  }
  private static DataGridView Grid()
  {
   var g=new DataGridView { Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,AllowUserToResizeRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,GridColor=Color.FromArgb(230,235,240),EnableHeadersVisualStyles=false,RowTemplate={ Height=34 } };
   g.ColumnHeadersDefaultCellStyle.BackColor=Color.FromArgb(233,240,245);g.ColumnHeadersDefaultCellStyle.ForeColor=Ui.Ink;g.ColumnHeadersDefaultCellStyle.Font=new Font("Microsoft YaHei UI",9,FontStyle.Bold);g.ColumnHeadersHeight=38;g.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.DisableResizing;g.DefaultCellStyle.SelectionBackColor=Color.FromArgb(218,238,231);g.DefaultCellStyle.SelectionForeColor=Ui.Ink;g.DefaultCellStyle.Padding=new Padding(7,2,3,2);return g;
  }
  private void BuildServers()
  {
   var toolbar=Ui.Toolbar();toolbar.Controls.Add(Ui.Button("＋ 创建服务器",CreateServer,true));toolbar.Controls.Add(Ui.Button("刷新",()=>Ui.Try(this,()=> { ReloadInstances();ShowPage("服务器"); })));
   var split=new SplitContainer { Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,Size=new Size(900,650),SplitterDistance=230,Panel1MinSize=120,Panel2MinSize=280,BackColor=Ui.Background };
   serverGrid=Grid();serverGrid.Columns.Add("name","实例");serverGrid.Columns.Add("game","游戏 / 加载器");serverGrid.Columns.Add("version","版本");serverGrid.Columns.Add("state","状态");serverGrid.Columns.Add("port","端口");serverGrid.Columns.Add("cpu","CPU");serverGrid.Columns.Add("memory","内存");serverGrid.Columns[0].FillWeight=125;serverGrid.Columns[3].FillWeight=180;
   refreshing=true;
   foreach(var instance in instances)
   {
    var st=services.Processes.GetStatus(instance);int row=serverGrid.Rows.Add(instance.Name,Ui.KindName(instance.Kind),instance.GameVersion,st.Message,instance.Port>0?instance.Port+(instance.Udp?" / UDP":" / TCP"):"未指定");serverGrid.Rows[row].Tag=instance;
   }
   if(serverGrid.Rows.Count>0) { var row=serverGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(x=>((ServerInstance)x.Tag).Id==selectedId)??serverGrid.Rows[0];serverGrid.CurrentCell=row.Cells[0]; }
   refreshing=false;serverGrid.SelectionChanged+=(s,e)=> { if(refreshing||serverGrid.SelectedRows.Count==0)return;selectedId=((ServerInstance)serverGrid.SelectedRows[0].Tag).Id;LoadCurrentLog(); };
   var empty=Ui.Label("还没有服务器。点击“创建服务器”，从游戏预设或你自己的服务端包开始。",true);empty.Dock=DockStyle.Bottom;empty.Visible=instances.Count==0;empty.Height=42;empty.AutoSize=false;split.Panel1.Controls.Add(serverGrid);split.Panel1.Controls.Add(empty);
   var actions=Ui.Toolbar();actions.Controls.Add(Ui.Button("启动",()=>WithSelected(Start),true));actions.Controls.Add(Ui.Button("保存并关服",()=>WithSelected(Stop),true));actions.Controls.Add(Ui.Button("保存 / 回档点",()=>WithSelected(SaveNow)));actions.Controls.Add(Ui.Button("重启",()=>WithSelected(Restart)));var emergency=Ui.Button("紧急关服 · 不保存",()=>WithSelected(Kill));emergency.BackColor=Color.FromArgb(173,47,56);emergency.ForeColor=Color.White;actions.Controls.Add(emergency);actions.Controls.Add(Ui.Button("启动配置",()=>WithSelected(Edit)));actions.Controls.Add(Ui.Button("打开目录",()=>WithSelected(s=>Ui.Open(services.Paths.InstanceDirectory(s)))));actions.Controls.Add(Ui.Button("复制实例",()=>WithSelected(Clone)));actions.Controls.Add(Ui.Button("删除",()=>WithSelected(Delete)));
   logBox=new TextBox { Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Both,WordWrap=false,BackColor=Color.FromArgb(26,39,51),ForeColor=Color.FromArgb(217,232,225),Font=new Font("Consolas",9),BorderStyle=BorderStyle.None };
   var command=Ui.Text();var consoleRow=new TableLayoutPanel { Dock=DockStyle.Bottom,Height=43,ColumnCount=2,ColumnStyles={new ColumnStyle(SizeType.Percent,100),new ColumnStyle(SizeType.Absolute,105)},Padding=new Padding(0,6,0,0) };var send=Ui.Button("发送命令",()=>WithSelected(s=> { services.Processes.Send(s,command.Text);command.Clear(); }));consoleRow.Controls.Add(command,0,0);consoleRow.Controls.Add(send,1,0);command.KeyDown+=(s,e)=> {if(e.KeyCode==Keys.Enter) {e.SuppressKeyPress=true;send.PerformClick();}};
   instanceStatus=Ui.Label("",true);instanceStatus.Dock=DockStyle.Top;instanceStatus.AutoSize=false;instanceStatus.Height=26;
   resourceChart=new ResourceChart();split.Panel2.Controls.Add(logBox);split.Panel2.Controls.Add(resourceChart);split.Panel2.Controls.Add(consoleRow);split.Panel2.Controls.Add(instanceStatus);split.Panel2.Controls.Add(actions);body.Controls.Add(split);body.Controls.Add(toolbar);LoadCurrentLog();
  }
  private void WithSelected(Action<ServerInstance> action)=>Ui.Try(this,()=> {var instance=Selected;if(instance==null)throw new InvalidOperationException("请先创建或选择一个服务器实例。");action(instance);});
  private void RequireStopped(ServerInstance instance)
  {if(services.Processes.GetStatus(instance).Running)throw new InvalidOperationException("请先停止该服务器，再进行文件或配置变更。");}
  private void CreateServer()
  {using(var dialog=new CreateServerDialog(services))if(dialog.ShowDialog(this)==DialogResult.OK) {selectedId=dialog.CreatedInstance.Id;ReloadInstances();ShowPage("服务器");statusText.Text="服务器已创建，点击启动开始运行。";}}
  private void Start(ServerInstance instance)
  {ProgressDialog.Run(this,"启动服务器",(p,c)=>services.Processes.StartAsync(instance,p,c));RefreshStatus();LoadCurrentLog();}
  private void Stop(ServerInstance instance)
  {bool stopped=ProgressDialog.Run(this,"等待服务器保存并停止",(p,c)=>services.Saves.SaveAndStopAsync(instance,p,c));RefreshStatus();if(!stopped)MessageBox.Show(this,"服务器尚未停止。请检查控制台、停止命令或使用强制结束；强制结束可能丢失未保存进度。","等待停止超时",MessageBoxButtons.OK,MessageBoxIcon.Information);}
  private void SaveNow(ServerInstance instance)
  {ProgressDialog.Run(this,"保存并生成回档点",(p,c)=>services.Saves.SaveNowAsync(instance,p,c));RefreshStatus();}
  private void Restart(ServerInstance instance)
  {
   ProgressDialog.Run(this,"重启服务器",async (p,c)=> { if(services.Processes.GetStatus(instance).Running&&!await services.Saves.SaveAndStopAsync(instance,p,c))throw new InvalidOperationException("服务器未正常停止，本次重启已取消。");await services.Processes.StartAsync(instance,p,c); });RefreshStatus();LoadCurrentLog();
  }
  private void Kill(ServerInstance instance)
  {if(!services.Processes.GetStatus(instance).Running)return;if(MessageBox.Show(this,"跳过新的保存命令并结束服务端，未保存进度将丢失。游戏此前已写入磁盘的自动保存不会被撤销。继续？","紧急关服（不保存）",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)==DialogResult.Yes) {ProgressDialog.Run(this,"紧急结束服务器",(p,c)=>services.Saves.EmergencyStopAsync(instance));RefreshStatus();}}
  private void Edit(ServerInstance instance)
  {RequireStopped(instance);var draft=JsonConvert.DeserializeObject<ServerInstance>(JsonConvert.SerializeObject(instance));draft.Save=services.Saves.Profile(instance);var edited=ServerEditor.Edit(this,draft);if(edited!=null) {services.Repository.Save(edited);ReloadInstances();ShowPage(currentPage);}}
  private void Clone(ServerInstance instance)
  {
   RequireStopped(instance);string name=TextPrompt.Ask(this,"复制实例","为副本填写名称。",instance.Name+" 副本");if(name==null)return;if(string.IsNullOrWhiteSpace(name))throw new InvalidOperationException("实例名称不能为空。");
   var clone=JsonConvert.DeserializeObject<ServerInstance>(JsonConvert.SerializeObject(instance));clone.Id=Guid.NewGuid().ToString("N");clone.StorageDirectory="";clone.Name=name;clone.CreatedUtc=DateTime.UtcNow;
   string destination=services.Paths.InstanceDirectory(clone);
   try {ProgressDialog.Run(this,"复制服务器实例",(p,c)=>Task.Run(()=>services.Transfers.CopyTree(services.Paths.InstanceDirectory(instance),destination,c),c));var copiedIdentity=Path.Combine(destination,".workbench-instance.json");if(File.Exists(copiedIdentity))File.Delete(copiedIdentity);services.Repository.Save(clone);selectedId=clone.Id;ReloadInstances();ShowPage("服务器");}
   catch {if(Directory.Exists(destination))Directory.Delete(destination,true);throw;}
  }
  private void Delete(ServerInstance instance)
  {RequireStopped(instance);if(MessageBox.Show(this,"删除“"+instance.Name+"”及其世界、配置和 Mod？已有备份会保留。","删除实例",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;services.Repository.Delete(instance.Id);ReloadInstances();ShowPage("服务器");}
  private void LoadCurrentLog()
  {if(logBox==null)return;var selected=Selected;resourceChart?.Reset();logBox.Text=selected==null?"选择实例后，这里会显示服务端输出。":string.Join(Environment.NewLine,services.Processes.ReadLog(selected));logBox.SelectionStart=logBox.TextLength;logBox.ScrollToCaret();if(instanceStatus!=null)instanceStatus.Text=selected==null?"未选择实例":selected.Name+"  ·  "+services.Processes.GetStatus(selected).Message;}
  private void ProcessLog(object sender,ServerLogEventArgs e)
  {
   if(IsDisposed||!IsHandleCreated)return;try {BeginInvoke((Action)(()=> {if(logBox==null||e.InstanceId!=selectedId)return;if(logBox.TextLength>500000)logBox.Text=logBox.Text.Substring(logBox.TextLength-300000);logBox.AppendText(e.Line+Environment.NewLine);}));}catch(InvalidOperationException) { }
  }
  private void ProcessStatus(object sender,EventArgs e)
  {if(IsDisposed||!IsHandleCreated)return;try {BeginInvoke((Action)RefreshStatus);}catch(InvalidOperationException) { }}
  private void RefreshStatus()
  {
   if(IsDisposed)return;int running=instances.Count(x=>services.Processes.GetStatus(x).Running);statusText.Text=instances.Count+" 个实例  ·  "+running+" 个运行中  ·  数据："+services.Paths.Root;
   var metrics=new Dictionary<string,ResourceSample>();
   foreach(var s in instances)if(services.Processes.GetStatus(s).Running)metrics[s.Id]=services.Processes.GetResources(s);
   if(serverGrid!=null)foreach(DataGridViewRow row in serverGrid.Rows) {var s=(ServerInstance)row.Tag;row.Cells[3].Value=services.Processes.GetStatus(s).Message;ResourceSample m;if(metrics.TryGetValue(s.Id,out m)){row.Cells[5].Value=string.IsNullOrEmpty(m.Error)?m.CpuPercent.ToString("F1")+"%":"不可用";row.Cells[6].Value=string.IsNullOrEmpty(m.Error)?(m.MemoryBytes/1048576.0).ToString("F0")+" MB":"不可用";}else{row.Cells[5].Value="—";row.Cells[6].Value="—";}}
   if(resourceChart!=null&&Selected!=null){ResourceSample sample;resourceChart.Add(metrics.TryGetValue(Selected.Id,out sample)?sample:new ResourceSample());}

   if(instanceStatus!=null&&Selected!=null)instanceStatus.Text=Selected.Name+"  ·  "+services.Processes.GetStatus(Selected).Message+(string.IsNullOrEmpty(services.Saves.LastError(Selected))?"":"  ·  保存提示："+services.Saves.LastError(Selected));
   if(currentPage=="联机指引"&&networkBox!=null)RefreshNetwork();
  }
  private ComboBox InstanceChooser(Action changed)
  {
   var combo=Ui.Combo();combo.Width=330;combo.Dock=DockStyle.None;combo.MinimumSize=new Size(300,28);combo.FormattingEnabled=true;combo.Format+=(s,e)=>e.Value=((ServerInstance)e.ListItem).Name+"  /  "+Ui.KindName(((ServerInstance)e.ListItem).Kind);combo.Items.AddRange(instances.Cast<object>().ToArray());if(Selected!=null)combo.SelectedItem=Selected;
   combo.SelectedIndexChanged+=(s,e)=> {selectedId=(combo.SelectedItem as ServerInstance)?.Id;changed();};return combo;
  }
  private void BuildMods()
  {
   var chooser=Ui.Toolbar();chooser.Controls.Add(Ui.Label("目标实例"));chooser.Controls.Add(InstanceChooser(()=> {LoadInstalledMods();ClearSearch();}));
   var tabs=new TabControl { Dock=DockStyle.Fill };var local=new TabPage("已安装与本地导入") { Padding=new Padding(10),BackColor=Color.White };var online=new TabPage("Modrinth 在线搜索") { Padding=new Padding(10),BackColor=Color.White };tabs.TabPages.Add(local);tabs.TabPages.Add(online);
   installedGrid=Grid();installedGrid.Columns.Add("name","名称");installedGrid.Columns.Add("version","版本");installedGrid.Columns.Add("enabled","状态");installedGrid.Columns.Add("file","文件");
   var tools=Ui.Toolbar();tools.Controls.Add(Ui.Button("导入文件",()=>WithSelected(s=>ImportLocalMod(s,false)),true));tools.Controls.Add(Ui.Button("导入目录",()=>WithSelected(s=>ImportLocalMod(s,true))));tools.Controls.Add(Ui.Button("启用 / 禁用",()=>WithMod((s,m)=> {ProgressDialog.Run(this,"变更 Mod 状态",(p,c)=>services.Mods.SetEnabledAsync(s,m,!m.Enabled,c));ReloadMods();})));tools.Controls.Add(Ui.Button("移除",()=>WithMod((s,m)=> {if(MessageBox.Show(this,"移除 Mod“"+m.Name+"”？","移除 Mod",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes) {ProgressDialog.Run(this,"移除 Mod",(p,c)=>services.Mods.RemoveAsync(s,m,c));ReloadMods();}})));tools.Controls.Add(Ui.Button("刷新",ReloadMods));
   var info=Ui.Label("普通目录型 Mod 通过移入或移出目录启用；tModLoader 同时维护 enabled.json。原版 Minecraft 不支持加载 Mod。",true);info.Dock=DockStyle.Bottom;info.Height=48;info.AutoSize=false;local.Controls.Add(installedGrid);local.Controls.Add(info);local.Controls.Add(tools);
   queryBox=Ui.Text();var searchRow=new TableLayoutPanel { ColumnCount=2,Dock=DockStyle.Top,Height=44,ColumnStyles={new ColumnStyle(SizeType.Percent,100),new ColumnStyle(SizeType.Absolute,100)} };var search=Ui.Button("搜索",SearchMods,true);searchRow.Controls.Add(queryBox,0,0);searchRow.Controls.Add(search,1,0);queryBox.KeyDown+=(s,e)=> {if(e.KeyCode==Keys.Enter) {e.SuppressKeyPress=true;search.PerformClick();}};
   searchGrid=Grid();searchGrid.Columns.Add("name","项目");searchGrid.Columns.Add("description","简介");searchGrid.Columns.Add("side","服务端支持");searchGrid.Columns[1].FillWeight=250;
   onlineVersions=Ui.Combo();onlineVersions.Width=340;onlineVersions.Dock=DockStyle.None;var onlineActions=Ui.Toolbar();onlineActions.Dock=DockStyle.Bottom;onlineActions.Controls.Add(Ui.Button("读取所选项目版本",ReadModVersions));onlineActions.Controls.Add(onlineVersions);onlineActions.Controls.Add(Ui.Button("安装所选版本",InstallOnlineMod,true));
   var onlineHelp=Ui.Label("在线安装支持 Fabric / Forge；按当前实例筛选版本，先检查依赖和冲突，再下载。客户端也需要的 Mod 请同步安装到游戏客户端。",true);onlineHelp.Dock=DockStyle.Top;onlineHelp.Height=48;onlineHelp.AutoSize=false;online.Controls.Add(searchGrid);online.Controls.Add(onlineActions);online.Controls.Add(onlineHelp);online.Controls.Add(searchRow);searchGrid.SelectionChanged+=(s,e)=>{if(!refreshing&&onlineVersions!=null&&!onlineVersions.IsDisposed)onlineVersions.Items.Clear();};body.Controls.Add(tabs);body.Controls.Add(chooser);LoadInstalledMods();
  }
  private void LoadInstalledMods()
  {if(installedGrid==null)return;installedGrid.Rows.Clear();if(Selected==null)return;foreach(var m in Selected.Mods) {int n=installedGrid.Rows.Add(m.Name,m.VersionId,m.Enabled?"已启用":"已禁用",m.FileName);installedGrid.Rows[n].Tag=m;}}
  private void ReloadMods() {ReloadInstances();LoadInstalledMods();}
  private void ClearSearch() {searchGrid?.Rows.Clear();onlineVersions?.Items.Clear();}
  private void WithMod(Action<ServerInstance,InstalledMod> action)=>WithSelected(s=> {RequireStopped(s);if(installedGrid==null||installedGrid.SelectedRows.Count==0)throw new InvalidOperationException("请选择一个已安装 Mod。");action(s,(InstalledMod)installedGrid.SelectedRows[0].Tag);});
  private void ImportLocalMod(ServerInstance instance,bool directory)
  {
   RequireStopped(instance);string path;
   if(directory) {using(var d=new FolderBrowserDialog { Description="选择 Mod 目录；仅导入内容，不修改原目录。" }) {if(d.ShowDialog(this)!=DialogResult.OK)return;path=d.SelectedPath;}}
   else {using(var d=new OpenFileDialog { Filter="Mod 与清单|*.jar;*.tmod;enabled.json|所有文件|*.*" }) {if(d.ShowDialog(this)!=DialogResult.OK)return;path=d.FileName;}}
   string internalName="";
   if(instance.Kind==ServerKind.TModLoader&&!directory&&Path.GetExtension(path).Equals(".tmod",StringComparison.OrdinalIgnoreCase)) {internalName=TextPrompt.Ask(this,"tModLoader 内部名称","如已知 Mod 内部名称请填写；可留空由文件元数据识别，不能识别时会提示。不要使用显示名称。");if(internalName==null)return;}
   ProgressDialog.Run(this,"导入本地 Mod",(p,c)=>services.Mods.ImportLocalAsync(instance,path,internalName,p,c));ReloadMods();
  }
  private bool RequireOnlineInstance()
  {if(Selected==null)throw new InvalidOperationException("请先选择实例。");if(Selected.Kind!=ServerKind.Fabric&&Selected.Kind!=ServerKind.Forge)throw new InvalidOperationException("Modrinth 在线安装目前支持 Fabric 和 Forge。其他实例请使用本地导入。");return true;}
  private void SearchMods()=>Ui.Try(this,()=> {RequireOnlineInstance();var results=ProgressDialog.Run(this,"搜索 Modrinth",(p,c)=>services.Mods.SearchAsync(Selected,queryBox.Text.Trim(),c));searchGrid.Rows.Clear();onlineVersions.Items.Clear();foreach(var m in results) {int n=searchGrid.Rows.Add(m.Name,m.Description,m.ServerSupport);searchGrid.Rows[n].Tag=m;}});
  private void ReadModVersions()=>Ui.Try(this,()=> {RequireOnlineInstance();if(searchGrid.SelectedRows.Count==0)throw new InvalidOperationException("请先搜索并选择一个项目。");var m=(ModSearchResult)searchGrid.SelectedRows[0].Tag;var list=ProgressDialog.Run(this,"读取兼容版本",(p,c)=>services.Mods.GetVersionsAsync(Selected,m.Id,c));onlineVersions.Items.Clear();onlineVersions.Items.AddRange(list.Cast<object>().ToArray());if(onlineVersions.Items.Count>0)onlineVersions.SelectedIndex=0;else throw new InvalidOperationException("没有找到与当前实例匹配的 Mod 版本。");});
  private void InstallOnlineMod()=>WithSelected(s=> {RequireOnlineInstance();RequireStopped(s);var v=onlineVersions.SelectedItem as VersionOption;if(v==null)throw new InvalidOperationException("请先读取并选择 Mod 版本。");ProgressDialog.Run(this,"安装 Mod 与必需依赖",(p,c)=>services.Mods.InstallAsync(s,v.Id,p,c));ReloadMods();});
  private void BuildBackups()
  {
   var chooser=Ui.Toolbar();chooser.Controls.Add(Ui.Label("目标实例"));chooser.Controls.Add(InstanceChooser(LoadBackups));
   var tools=Ui.Toolbar();tools.Controls.Add(Ui.Button("创建停服备份",()=>WithSelected(s=> {RequireStopped(s);ProgressDialog.Run(this,"备份实例",(p,c)=>services.Backups.CreateAsync(s,p,c));LoadBackups();}),true));tools.Controls.Add(Ui.Button("恢复所选备份",()=>WithSelected(s=> {RequireStopped(s);var backup=backupList.SelectedItem as BackupInfo;if(backup==null)throw new InvalidOperationException("请选择一个备份。");if(MessageBox.Show(this,"将当前实例恢复到所选备份。恢复前会另存当前数据。继续？","恢复备份",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;ProgressDialog.Run(this,"恢复实例",(p,c)=>services.Backups.RestoreAsync(s,backup,p,c));ReloadInstances();LoadBackups();})));tools.Controls.Add(Ui.Button("打开备份目录",()=>Ui.Try(this,()=> {Directory.CreateDirectory(services.Paths.Backups);Ui.Open(services.Paths.Backups);})));
   backupList=new ListBox { Dock=DockStyle.Fill,IntegralHeight=false,BackColor=Color.White,BorderStyle=BorderStyle.None,ItemHeight=34 };
   var tabs=new TabControl{Dock=DockStyle.Fill};var full=new TabPage("完整实例备份");full.Controls.Add(backupList);tabs.TabPages.Add(full);
   var saves=new TabPage("最近两次存档 · 回档点");saveList=new ListBox{Dock=DockStyle.Fill,IntegralHeight=false,BackColor=Color.White,BorderStyle=BorderStyle.None};
   var saveTools=Ui.Toolbar();saveTools.Controls.Add(Ui.Button("创建停服存档回档点",()=>WithSelected(s=>{RequireStopped(s);ProgressDialog.Run(this,"生成存档回档点",(p,c)=>services.Saves.CaptureStoppedAsync(s,p,c));LoadBackups();})));
   saveTools.Controls.Add(Ui.Button("恢复所选存档（回档）",()=>WithSelected(s=>{RequireStopped(s);var point=saveList.SelectedItem as SaveSnapshotInfo;if(point==null)throw new InvalidOperationException("请选择回档点。");if(MessageBox.Show(this,"恢复该存档？当前实例会先生成完整备份，配置与Mod不随存档回滚。","回档",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;ProgressDialog.Run(this,"恢复存档",(p,c)=>services.Saves.RestoreAsync(s,point,p,c));ReloadInstances();LoadBackups();}),true));saveTools.Controls.Add(Ui.Button("刷新回档点",LoadBackups));
   var saveHelp=Ui.Label("仅保留最近两次成功回档点。预设默认每5分钟请求保存并备份，也在确认游戏保存后补建回档点；不能识别的游戏自动保存不会被冒充为已备份。",true);saveHelp.Dock=DockStyle.Bottom;saveHelp.Height=60;saveHelp.AutoSize=false;saves.Controls.Add(saveList);saves.Controls.Add(saveHelp);saves.Controls.Add(saveTools);tabs.TabPages.Add(saves);
   var info=Ui.Label("完整备份包含世界、配置、Mod；存档回档点只恢复配置的世界目录。恢复前均另存当前完整实例。",true);info.Dock=DockStyle.Bottom;info.Height=45;info.AutoSize=false;body.Controls.Add(tabs);body.Controls.Add(info);body.Controls.Add(tools);body.Controls.Add(chooser);LoadBackups();
  }
  private void LoadBackups() {if(backupList==null)return;backupList.Items.Clear();saveList?.Items.Clear();if(Selected!=null){backupList.Items.AddRange(services.Backups.List(Selected).Cast<object>().ToArray());saveList?.Items.AddRange(services.Saves.List(Selected).Cast<object>().ToArray());}}
  private TextBox InformationBox()=>new TextBox { Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,ForeColor=Ui.Ink,BorderStyle=BorderStyle.None,Font=new Font("Microsoft YaHei UI",10) };
  private void BuildEnvironment()
  {
   var text=InformationBox();var tools=Ui.Toolbar();tools.Controls.Add(Ui.Button("重新检测",()=>text.Text=EnvironmentReport(),true));tools.Controls.Add(Ui.Button("导入运行时 ZIP",()=>Ui.Try(this,ImportRuntime)));tools.Controls.Add(Ui.Button("为所选实例指定环境",()=>WithSelected(Edit)));tools.Controls.Add(Ui.Button("打开运行时目录",()=>Ui.Try(this,()=> {Directory.CreateDirectory(services.Paths.Runtimes);Ui.Open(services.Paths.Runtimes);})));
   var links=Ui.Toolbar();links.Controls.Add(Ui.Button(".NET Framework 官方说明",()=>Ui.Try(this,()=>Ui.Open("https://learn.microsoft.com/dotnet/framework/install/"))));links.Controls.Add(Ui.Button("VC 运行库官方说明",()=>Ui.Try(this,()=>Ui.Open("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist"))));links.Controls.Add(Ui.Button("Java 官方发行版",()=>Ui.Try(this,()=>Ui.Open("https://adoptium.net/temurin/releases/"))));body.Controls.Add(text);body.Controls.Add(links);body.Controls.Add(tools);text.Text=EnvironmentReport();
  }
  private string EnvironmentReport()
  {
   var lines=new List<string>{"系统与管理器","", "Windows 版本："+Environment.OSVersion.VersionString,"系统架构："+(Environment.Is64BitOperatingSystem?"64 位":"32 位"),"进程架构："+(Environment.Is64BitProcess?"64 位":"32 位"),"数据目录："+services.Paths.Root,""};
   try {using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64))using(var key=root.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))lines.Add(".NET Framework Release："+(key?.GetValue("Release")?.ToString()??"未检测到"));}catch(Exception ex){lines.Add(".NET Framework 检测："+ex.Message);}
   foreach(string arch in new[]{"x64","x86"})try {using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry32))using(var key=root.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\"+arch))lines.Add("VC 2015–2022 "+arch+"："+(key?.GetValue("Version")?.ToString()??"未检测到；仅在游戏需要时安装"));}catch(Exception ex){lines.Add("VC "+arch+" 检测："+ex.Message);}
   lines.AddRange(new[]{"","运行环境的处理方式","","• Minecraft 根据游戏与加载器版本选择 Java，保存到私有运行时目录。","• 有完整 runtimeconfig.json 的 .NET 程序按声明准备环境。","• Win7 / 8.1 不自动安装未经验证的运行时。请导入兼容 ZIP 或选择已有 java.exe / dotnet.exe。","• .NET Framework 和 VC 运行库属于系统组件；按游戏要求通过微软官方安装程序安装。","• 64 位 Windows 上的 32 位服务端，仍可能需要 x86 VC 运行库。","• 当前最新版 VC 或 .NET 不一定支持旧 Windows，请阅读所选版本的系统要求。","","运行时目录内的内容只供本软件启动服务端使用，不修改全局 PATH。"});return string.Join(Environment.NewLine,lines);
  }
  private void ImportRuntime()
  {
   string file;using(var d=new OpenFileDialog { Filter="运行时 ZIP|*.zip" }) {if(d.ShowDialog(this)!=DialogResult.OK)return;file=d.FileName;}
   string target=Path.Combine(services.Paths.Runtimes,"imported-"+Guid.NewGuid().ToString("N").Substring(0,10));
   try {ProgressDialog.Run(this,"导入运行时",(p,c)=>Task.Run(()=>services.Transfers.ExtractZip(file,target,c),c));Ui.Open(target);MessageBox.Show(this,"已导入。请在实例的“启动配置 → 运行时程序”中选择此目录内的 java.exe 或 dotnet.exe。"+Environment.NewLine+target,"运行时已导入",MessageBoxButtons.OK,MessageBoxIcon.Information);}
   catch {if(Directory.Exists(target))Directory.Delete(target,true);throw;}
  }
  private void BuildNetwork()
  {
   var chooser=Ui.Toolbar();chooser.Controls.Add(Ui.Label("目标实例"));chooser.Controls.Add(InstanceChooser(RefreshNetwork));chooser.Controls.Add(Ui.Button("刷新检测",RefreshNetwork,true));chooser.Controls.Add(Ui.Button("复制连接地址",()=>Ui.Try(this,()=> {if(Selected==null||Selected.Port==0)throw new InvalidOperationException("请先选择已指定端口的实例。");Clipboard.SetText(LocalAddresses().FirstOrDefault()+":"+Selected.Port);statusText.Text="已复制局域网地址。";})));
   var tabs=new TabControl{Dock=DockStyle.Fill};var local=new TabPage("本机与局域网"){Padding=new Padding(10),BackColor=Color.White};var tunnel=new TabPage("frp 内网穿透"){Padding=new Padding(10),BackColor=Color.White};tabs.TabPages.Add(local);tabs.TabPages.Add(tunnel);
   networkBox=InformationBox();local.Controls.Add(networkBox);tunnelBox=InformationBox();
   var actions=Ui.Toolbar();tunnelInstall=Ui.Button("准备 frp 组件",()=>Ui.Try(this,()=>{var client=RequireTunnels();ProgressDialog.Run(this,"准备官方 frp 组件",(p,c)=>client.InstallClientAsync(p,c));RefreshNetwork();}),true);
   tunnelConfigure=Ui.Button("穿透配置",()=>WithSelected(s=>{var client=RequireTunnels();if(client.GetStatus(s).Running)throw new InvalidOperationException("请先停止本实例的穿透，再修改节点配置。");if(FrpTunnelDialog.Edit(this,client,s))RefreshNetwork();}));
   tunnelStart=Ui.Button("启动穿透",()=>WithSelected(s=>{var client=RequireTunnels();ProgressDialog.Run(this,"连接可信中转节点",(p,c)=>client.StartAsync(s,p,c));RefreshNetwork();}),true);
   tunnelStop=Ui.Button("停止穿透",()=>WithSelected(s=>{RequireTunnels().Stop(s);RefreshNetwork();}));
   tunnelCopy=Ui.Button("复制中转地址",()=>WithSelected(s=>{var state=RequireTunnels().GetStatus(s);if(!state.Ready||string.IsNullOrWhiteSpace(state.PublicEndpoint))throw new InvalidOperationException("本实例尚未完成中转端口注册。");Clipboard.SetText(state.PublicEndpoint);statusText.Text="已复制中转地址；公网可达性仍需朋友实际连接验证。";}));
   actions.Controls.Add(tunnelInstall);actions.Controls.Add(tunnelConfigure);actions.Controls.Add(tunnelStart);actions.Controls.Add(tunnelStop);actions.Controls.Add(tunnelCopy);actions.Controls.Add(Ui.Button("中转部署说明",()=>Ui.Try(this,OpenTunnelGuide)));
   tunnel.Controls.Add(tunnelBox);tunnel.Controls.Add(actions);body.Controls.Add(tabs);body.Controls.Add(chooser);RefreshNetwork();
  }
  private FrpTunnelService RequireTunnels()
  {if(services.Tunnels==null)throw new InvalidOperationException("当前会话未启用 Windows 安全凭据保存，请从正式桌面程序启动。");return services.Tunnels;}
  private static void OpenTunnelGuide()
  {
   var file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"内网穿透.md");if(!File.Exists(file))file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"docs","内网穿透.md");
   if(!File.Exists(file))throw new FileNotFoundException("当前目录中未找到内网穿透说明，请查看源码 docs/内网穿透.md。");Ui.Open(file);
  }
  private static string[] LocalAddresses()
  {try {return NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address.ToString()).Distinct().ToArray();}catch{return new[]{"127.0.0.1"};}}
  private void RefreshNetwork()=>Ui.Try(this,()=>
  {
   if(networkBox==null)return;var lines=new List<string>{"本机与局域网","","本机测试地址：127.0.0.1","局域网 IPv4："+string.Join("、",LocalAddresses()),""};var instance=Selected;
   if(instance!=null) {lines.Add("实例："+instance.Name);lines.Add("进程状态："+services.Processes.GetStatus(instance).Message);if(instance.Port>0) {var ip=IPGlobalProperties.GetIPGlobalProperties();bool listening=instance.Udp?ip.GetActiveUdpListeners().Any(x=>x.Port==instance.Port):ip.GetActiveTcpListeners().Any(x=>x.Port==instance.Port);lines.Add("端口："+instance.Port+(instance.Udp?" / UDP":" / TCP"));lines.Add("本机监听："+(listening?"检测到端口监听（不代表一定属于本实例）":"未检测到；先启动服务端并检查日志"));}else lines.Add("未指定端口，可在启动配置中补充。");}
   lines.AddRange(new[]{"","连接步骤","","1. 在开服电脑上，用游戏连接 127.0.0.1 与实例端口。","2. 同一个局域网的朋友使用上方对应网卡的局域网 IPv4 地址。","3. 检查 Windows 防火墙入站规则，允许实际运行服务端的程序和端口；网络类型需与当前网络一致。","4. 没有公网 IP 时，可到“frp 内网穿透”页配置自己的可信公网中转节点，或另行使用虚拟局域网。","5. 运营商 NAT、双层路由和无公网地址可能阻止直接连接；端口转发不能解决所有情况。","","Minecraft 默认 TCP 25565；Terraria / tModLoader 默认 TCP 7777。其他游戏需按其文档确认 TCP / UDP。","","本软件仅检测本机监听，没有测试公网可达性。请由异地朋友实际尝试连接；检测到端口监听不等于外网已打通。"});SetNetworkReport(networkBox,string.Join(Environment.NewLine,lines));RefreshTunnelReport(instance);
  });
  private void RefreshTunnelReport(ServerInstance instance)
  {
   if(tunnelBox==null)return;var client=services.Tunnels;var lines=new List<string>{"frp 内网穿透","","需要一台有公网 IP 的 frps 中转节点，或可信服务商提供的兼容节点。本软件不内置公共共享节点。","先准备组件，再保存节点配置；游戏就绪后单独点击“启动穿透”。保存配置或重启软件不会自动开放转发。",""};
   bool available=client!=null&&client.IsSupported;
   tunnelInstall.Enabled=available;tunnelConfigure.Enabled=available&&instance!=null&&!instance.Udp&&instance.Port>0;tunnelStart.Enabled=false;tunnelStop.Enabled=false;tunnelCopy.Enabled=false;
   if(client==null)lines.Add("当前会话未启用 Windows 安全凭据保存；正式桌面入口使用当前用户 DPAPI 保存 Token。");
   else
   {
    bool installed=client.ClientInstalled;lines.Add("客户端：frp "+client.Version+(installed?" · 已准备":" · 尚未准备"));if(!client.IsSupported)lines.Add(client.UnsupportedReason);
    if(instance==null)lines.Add("请先创建或选择游戏实例。");
    else
    {
     var state=client.GetStatus(instance);var game=services.Processes.GetStatus(instance);
     lines.Add("目标实例："+instance.Name);lines.Add("固定本机目标：127.0.0.1:"+instance.Port+" / TCP");lines.Add("状态："+state.Message);
     if(instance.Udp)lines.Add("当前实例使用 UDP；本版穿透仅支持 TCP。");
     if(!string.IsNullOrEmpty(state.PublicEndpoint))lines.Add("配置的中转地址（公网未验证）："+state.PublicEndpoint);
     tunnelConfigure.Enabled=available&&!state.Running&&!instance.Udp&&instance.Port>0;tunnelStart.Enabled=available&&installed&&!state.Running&&game.Running&&game.Ready&&!instance.Udp&&instance.Port>0;
     tunnelStop.Enabled=state.Running;tunnelCopy.Enabled=state.Ready&&!string.IsNullOrEmpty(state.PublicEndpoint);
     lines.Add("Token："+(client.HasSavedToken(instance)?"已保存为当前用户加密数据":"尚未配置"));
     if(!game.Ready)lines.Add("先让游戏达到就绪状态，才能启动穿透。");
     lines.AddRange(new[]{"","穿透日志（已过滤凭据）",""});lines.AddRange(client.ReadLog(instance).Reverse().Take(100).Reverse());
    }
   }
   lines.AddRange(new[]{"","节点端口注册成功只代表中转配置被接受，不代表朋友已能从外网加入。请从异地实际验证。","TLS 校验保护开服电脑到中转节点的连接。请信任节点运营者；普通游戏 TCP 在节点到玩家这段未必加密。","停止游戏或退出管理器会停止关联穿透。停止穿透不会关闭游戏。"});SetNetworkReport(tunnelBox,string.Join(Environment.NewLine,lines));
  }
  private static void SetNetworkReport(TextBox box,string text)
  {if(box.Text==text)return;int selection=box.SelectionStart;box.Text=text;box.SelectionStart=Math.Min(selection,box.TextLength);}
  private void BuildSettings()
  {
   var text=InformationBox();text.Text=string.Join(Environment.NewLine,new[]{"开服工坊 ServerWorkbench 0.1","","当前数据目录",services.Paths.Root,"","文件归属","每个实例独立保存程序、世界、Mod 和配置。下载缓存与运行时集中保存，删除实例不会主动删除备份。","","支持范围","管理器目标：Windows 7 SP1 / 8.1 / 10 / 11，64 位。目标兼容性与已完成的实机验证分别记录在发行说明。","游戏、Java、.NET、Mod 与新版 frp 组件可能要求更高系统版本；管理器能打开不代表所有组件都能运行。","","初版边界","支持 Windows 原生服务端与明确的运行时声明。不会执行导入包中的安装器来猜测环境。","frp TCP 穿透需要自行提供可信公网中转节点；不内置共享节点或开放代理。","不包含 Docker、WSL、SteamCMD、Workshop 在线下载、远程面板、自动更新或系统服务。","","操作提示","• 参数编辑采用每行一个参数，不额外添加引号。","• Mod 和备份操作前先停止实例。","• 关闭窗口会隐藏到系统托盘；托盘菜单可真正退出并停止服务器。","• 移动整个便携目录后，已手动指定的绝对运行时路径可能需要重新选择。","• 穿透 Token 绑定当前 Windows 用户，换用户或换电脑后需要重新填写。","","数据目录切换","切换在下次启动生效，不会自动搬迁旧存档。需要迁移时，先退出软件，再将旧 Data 的全部内容复制到新目录。"});
   var tools=Ui.Toolbar();tools.Controls.Add(Ui.Button("打开数据目录",()=>Ui.Try(this,()=>Ui.Open(services.Paths.Root)),true));tools.Controls.Add(Ui.Button("下次启动切换目录",()=>Ui.Try(this,()=> {using(var d=new FolderBrowserDialog { Description="选择下次启动使用的数据目录（不会自动迁移现有数据）。",SelectedPath=services.Paths.Root })if(d.ShowDialog(this)==DialogResult.OK) {Program.SaveDataRoot(d.SelectedPath);MessageBox.Show(this,"已保存。下次启动使用新目录；当前会话继续使用原目录。","目录设置",MessageBoxButtons.OK,MessageBoxIcon.Information);}})));tools.Controls.Add(Ui.Button("打开使用说明",()=>Ui.Try(this,()=> {string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"使用说明.md");if(!File.Exists(file))file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"README.md");if(!File.Exists(file))throw new FileNotFoundException("便携目录中未找到使用说明，请查看源码目录 docs。");Ui.Open(file);})));body.Controls.Add(text);body.Controls.Add(tools);
  }
  private void ShowFromTray() {Show();WindowState=FormWindowState.Normal;Activate();}
  private void ClosingToTray(object sender,FormClosingEventArgs e)
  {if(exiting)return;if(e.CloseReason==CloseReason.WindowsShutDown) {exiting=true;return;}e.Cancel=true;Hide();}
  private void ExitApplication()
  {
   var running=instances.Where(x=>services.Processes.GetStatus(x).Running).ToList();
   if(running.Count>0)
   {
    var remaining=ProgressDialog.Run(this,"退出前正常停止服务器",async (p,c)=> {var list=new List<ServerInstance>();foreach(var s in running) {p.Report(new WorkProgress("等待 "+s.Name+" 保存并停止…"));if(!await services.Saves.SaveAndStopAsync(s,p,c))list.Add(s);}return list;});
    if(remaining.Count>0) {if(MessageBox.Show(this,"以下服务器未正常停止："+Environment.NewLine+string.Join(Environment.NewLine,remaining.Select(x=>x.Name))+Environment.NewLine+"强制结束后退出？选择“否”将取消退出。","仍有服务器运行",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;foreach(var s in remaining)services.Processes.Kill(s);}
   }
   exiting=true;tray.Visible=false;Close();
  }
  protected override void Dispose(bool disposing)
  {if(disposing) {refreshTimer.Stop();refreshTimer.Dispose();tray.Visible=false;tray.Dispose();services.Processes.LogReceived-=ProcessLog;services.Processes.StatusChanged-=ProcessStatus;}base.Dispose(disposing);}
 }
}
