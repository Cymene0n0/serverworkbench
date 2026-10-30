using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using ServerWorkbench.Core;
using ServerWorkbench.Desktop;
internal static class Program
{
 static int passed,failed;
 static readonly Assembly Desktop=typeof(MainForm).Assembly;
 static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static object Field(object value,string name)=>value.GetType().GetField(name,Private).GetValue(value);
 static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
 static void Test(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+" "+e);}}
 [STAThread] static int Main()
 {
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  var root=Path.Combine(Path.GetTempPath(),"ServerWorkbench-form-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
  Console.WriteLine("Headless form construction/event/layout tests. Hidden handles only; no windows shown, no actual click or visual-layout certification.");
  using(var services=AppBootstrap.Create(root)) {
   var creator=Desktop.GetType("ServerWorkbench.Desktop.CreateServerDialog");
   Test("Only Minecraft types retain an EULA row; Terraria types collapse it",()=>{
    using(var form=(Form)Activator.CreateInstance(creator,new object[]{services})) {
     var kind=(ComboBox)Field(form,"kind");var checkbox=(CheckBox)Field(form,"eula");
     var label=Descendants(form).OfType<Label>().Single(x=>x.Text=="Minecraft 协议");var fields=(TableLayoutPanel)label.Parent;int row=fields.GetPositionFromControl(label).Row;
     foreach(var game in new[]{ServerKind.Vanilla,ServerKind.Fabric,ServerKind.Forge,ServerKind.Terraria,ServerKind.TModLoader}) {
      kind.SelectedItem=game;bool minecraft=game==ServerKind.Vanilla||game==ServerKind.Fabric||game==ServerKind.Forge;
      Require(checkbox.Enabled==minecraft,"Incorrect EULA enabled state: "+game);
      Require(minecraft?fields.RowStyles[row].SizeType==SizeType.AutoSize:fields.RowStyles[row].SizeType==SizeType.Absolute&&fields.RowStyles[row].Height==0,"Incorrect EULA row size: "+game);
     }
     kind.SelectedItem=ServerKind.Vanilla;checkbox.Checked=true;kind.SelectedItem=ServerKind.Terraria;Require(!checkbox.Checked,"Hidden Minecraft consent must be cleared");
    }
   });
   Test("Preset form exposes a storage-directory input and chooser",()=>{
    using(var form=(Form)Activator.CreateInstance(creator,new object[]{services})) {var storage=(TextBox)Field(form,"presetStorage");Require(Descendants(form).Contains(storage)&&!storage.ReadOnly,"Missing editable storage input");Require(Descendants(form).OfType<Button>().Any(x=>x.Text=="选择目录"),"Missing directory chooser");Require(Descendants(form).OfType<Label>().Any(x=>x.Text=="服务器存放目录"),"Missing directory label");}
   });
   var editor=Desktop.GetType("ServerWorkbench.Desktop.ServerEditor");
   Test("New-import directory changes create a unique child without touching parent files",()=>{
    var parent=Path.Combine(root,"中文 父目录");Directory.CreateDirectory(parent);var keep=Path.Combine(parent,"keep.txt");File.WriteAllText(keep,"parent data");
    var helper=editor.GetMethod("NewStorageDirectory",BindingFlags.Static|BindingFlags.NonPublic);var first=(string)helper.Invoke(null,new object[]{parent,"测试 服务器"});var second=(string)helper.Invoke(null,new object[]{parent,"测试 服务器"});
    Require(first!=second&&Path.GetDirectoryName(first)==parent,"Must create unique child under selected parent");Require(!Directory.Exists(first)&&File.ReadAllText(keep)=="parent data","Validation must not mutate user parent files");
    using(var form=(Form)Activator.CreateInstance(editor,Private|BindingFlags.CreateInstance,null,new object[]{new ServerInstance{EntryPoint="test.exe"},true},null))Require(!((TextBox)Field(form,"storage")).ReadOnly,"New import storage must be editable");
   });
   Test("Existing-instance storage location is displayed read-only",()=>{
    var source=new ServerInstance{EntryPoint="test.exe",StorageDirectory=Path.Combine(root,"existing server")};
    using(var form=(Form)Activator.CreateInstance(editor,Private|BindingFlags.CreateInstance,null,new object[]{source,false},null)){var storage=(TextBox)Field(form,"storage");Require(storage.ReadOnly&&storage.Text==source.StorageDirectory,"Existing storage must not be implicitly moved");}
   });
   Test("Main window and sidebar follow current DPI and retain the entire brand text",()=>{
    using(var form=new MainForm(services)) {
     PrepareHiddenLayout(form);
     float scale=form.CurrentAutoScaleDimensions.Width/96f;
     var brand=Descendants(form).OfType<Label>().Single(x=>x.Text=="开服工坊");var sidebar=brand.Parent.Parent;
     int frameWidth=form.Width-form.ClientSize.Width;
     // Native handle creation can cap a top-level window at MaxWindowTrackSize
     // on a small runner desktop, even though our hidden test never raises Shown.
     // Use an independent plain Form with the design dimensions to observe that
     // OS constraint; the main form must still match its DPI-scaled client width.
     using(var probe=new Form{Font=new Font("Microsoft YaHei UI",9),Size=new Size(1250,850),MinimumSize=new Size(1050,720),StartPosition=FormStartPosition.CenterScreen}) {
      probe.AutoScaleDimensions=new SizeF(96,96);probe.AutoScaleMode=AutoScaleMode.Dpi;
      PrepareHiddenLayout(probe);
      Console.WriteLine("DPI="+form.CurrentAutoScaleDimensions.Width+" main="+form.Size+" client="+form.ClientSize+" probe="+probe.Size+" probeClient="+probe.ClientSize+" unboundedClientWidth="+(int)Math.Round((1250-frameWidth)*scale)+" workingArea="+Screen.FromControl(form).WorkingArea+" maxTrack="+SystemInformation.MaxWindowTrackSize);
      Require(Math.Abs(form.ClientSize.Width-probe.ClientSize.Width)<=2,"Main window client width did not match the DPI-scaled design with native window constraints");
     }
     Require(Math.Abs(sidebar.Width-(int)Math.Round(190*scale))<=1,"Sidebar width did not follow design DPI");
     var text=TextRenderer.MeasureText(brand.Text,brand.Font,Size.Empty,TextFormatFlags.NoPadding|TextFormatFlags.SingleLine);
     Require(brand.ClientSize.Width>=text.Width+brand.Padding.Horizontal&&brand.ClientSize.Height>=text.Height+brand.Padding.Vertical,"Brand text is clipped at "+form.CurrentAutoScaleDimensions.Width+" DPI: available="+brand.ClientSize+", measured="+text);
     Console.WriteLine("DPI="+form.CurrentAutoScaleDimensions.Width+" main="+form.Size+" sidebar="+sidebar.Width+" brand="+brand.ClientSize+" text="+text);
     // Exercise the real working-area handler without displaying a window.
     var area=Screen.FromControl(form).WorkingArea;var beforeSize=form.Size;var beforeMinimum=form.MinimumSize;
     typeof(Form).GetMethod("OnShown",Private).Invoke(form,new object[]{EventArgs.Empty});LayoutChildren(form);
     var expectedSize=new Size(Math.Min(beforeSize.Width,area.Width),Math.Min(beforeSize.Height,area.Height));
     var expectedMinimum=new Size(Math.Min(beforeMinimum.Width,area.Width),Math.Min(beforeMinimum.Height,area.Height));
     Console.WriteLine("Hidden Shown fit: before="+beforeSize+" after="+form.Size+" minimum="+form.MinimumSize+" area="+area+" visible="+form.Visible);
     Require(!form.Visible,"Working-area event test must not display a window");
     Require(form.MinimumSize==expectedMinimum,"Minimum size was not clamped to the current working area");
     Require(form.Size==expectedSize&&area.Contains(form.Bounds),"Shown handler did not fit the main window inside its working area");
     Require(Math.Abs(sidebar.Width-(int)Math.Round(190*scale))<=1,"Fitting the window changed the sidebar DPI scale");
     Require(brand.ClientSize.Width>=text.Width+brand.Padding.Horizontal&&brand.ClientSize.Height>=text.Height+brand.Padding.Vertical,"Working-area fit clipped the brand text");
    }
   });
   Test("Preset picker and version buttons reserve their measured text and padding",()=>{
    using(var form=(Form)Activator.CreateInstance(creator,new object[]{services})) {
     PrepareHiddenLayout(form);
     var buttons=Descendants(form).OfType<Button>().Where(x=>x.Parent is TableLayoutPanel&&(x.Text=="读取版本"||x.Text=="读取加载器"||x.Text=="选择目录"||x.Text=="浏览")).ToArray();Require(buttons.Length==4,"Expected all four preset picker/version buttons");
     foreach(var button in buttons) {
      var text=TextRenderer.MeasureText(button.Text,button.Font,Size.Empty,TextFormatFlags.SingleLine);
      Require(button.ClientSize.Width>=text.Width+button.Padding.Horizontal,"Button text is clipped: "+button.Text+", available="+button.ClientSize+", text="+text+", padding="+button.Padding);
      Require(button.Width>=button.GetPreferredSize(Size.Empty).Width,"Button is narrower than its preferred width: "+button.Text);
      var panel=(TableLayoutPanel)button.Parent;int column=panel.GetPositionFromControl(button).Column;
      Require(panel.GetColumnWidths()[column]>=button.Width+button.Margin.Horizontal,"Picker cell clips button: "+button.Text);
      Console.WriteLine(button.Text+" width="+button.Width+" preferred="+button.GetPreferredSize(Size.Empty).Width+" cell="+panel.GetColumnWidths()[column]);
     }
    }
   });
   Test("Page switches and repeated refreshes preserve DPI geometry and font points",()=>{
    using(var form=new MainForm(services)) {
     PrepareHiddenLayout(form);
     var initialButton=Descendants(form).OfType<Button>().Single(x=>x.Text=="保存并关服");
     Size buttonSize=initialButton.Size,formSize=form.Size;Padding buttonPadding=initialButton.Padding,buttonMargin=initialButton.Margin;
     float buttonPoints=initialButton.Font.SizeInPoints;var initialSplit=Descendants(form).OfType<SplitContainer>().Single();
     Size panelMinima=new Size(initialSplit.Panel1MinSize,initialSplit.Panel2MinSize);int splitterWidth=initialSplit.SplitterWidth;
     var showPage=typeof(MainForm).GetMethod("ShowPage",Private);int checkedServers=0;
     foreach(string page in new[]{"服务器","设置与说明","服务器","Mod 管理","服务器","备份与恢复","服务器","环境检测","服务器","联机指引","服务器","服务器"}) {
      showPage.Invoke(form,new object[]{page});PrepareHiddenLayout(form);
      Require(form.Size==formSize,"Page rebuilding must not rescale the form");
      Require(form.Font.SizeInPoints==9f,"Page rebuilding must not scale font points");
      if(page!="服务器")continue;
      var button=Descendants(form).OfType<Button>().Single(x=>x.Text=="保存并关服");var split=Descendants(form).OfType<SplitContainer>().Single();
      Require(button.Size==buttonSize&&button.Padding==buttonPadding&&button.Margin==buttonMargin,"Toolbar density changed after page rebuild #"+checkedServers);
      Require(button.Font.SizeInPoints==buttonPoints,"Button font points changed after page rebuild");
      Require(split.Panel1MinSize==panelMinima.Width&&split.Panel2MinSize==panelMinima.Height&&split.SplitterWidth==splitterWidth,"Splitter constraints changed after page rebuild #"+checkedServers);
      checkedServers++;
     }
     Console.WriteLine("Rebuilt servers="+checkedServers+" button="+buttonSize+" fontPt="+buttonPoints+" panelMinima="+panelMinima+" splitter="+splitterWidth);
    }
   });
   Test("frp page exposes explicit controls and never starts without a ready TCP game",()=>{
    using(var secured=AppBootstrap.Create(Path.Combine(root,"tunnel-page"),new NoSecrets())) {
     var instance=new ServerInstance{Name="TUNNEL FORM TEST",Port=7777,EntryPoint="fixture.exe"};secured.Repository.Save(instance);
     using(var form=new MainForm(secured)) {
      typeof(MainForm).GetMethod("ShowPage",Private).Invoke(form,new object[]{"联机指引"});PrepareHiddenLayout(form);
      foreach(var title in new[]{"准备 frp 组件","穿透配置","启动穿透","停止穿透","复制中转地址","中转部署说明"})Require(Descendants(form).OfType<Button>().Any(x=>x.Text==title),"Missing tunnel action: "+title);
      Require(!Descendants(form).OfType<Button>().Single(x=>x.Text=="启动穿透").Enabled,"Stopped game must not be exposed");
     }
    }
   });
   Test("frp dialog masks credentials, shows a fixed loopback target and requires one CA",()=>{
    using(var secured=AppBootstrap.Create(Path.Combine(root,"tunnel-dialog"),new NoSecrets())) {
     var instance=new ServerInstance{Name="TUNNEL DIALOG TEST",Port=7777,EntryPoint="fixture.exe"};var type=Desktop.GetType("ServerWorkbench.Desktop.FrpTunnelDialog");
     using(var form=(Form)Activator.CreateInstance(type,Private|BindingFlags.CreateInstance,null,new object[]{secured.Tunnels,instance},null)) {
      PrepareHiddenLayout(form);var secret=(TextBox)Field(form,"token");var remote=(NumericUpDown)Field(form,"remotePort");
      Require(secret.UseSystemPasswordChar&&secret.MaxLength==128&&secret.Text=="","Token must be masked and initially blank");Require(remote.Minimum==1024,"Unsupported privileged remote ports must not be selectable");
      Require(Descendants(form).OfType<Label>().Any(x=>x.Text.StartsWith("127.0.0.1:7777 / TCP")),"Expected fixed loopback game target");
      Require(!Descendants(form).OfType<TextBox>().Any(x=>x.Text.Contains("127.0.0.1:7777")),"Local target must not be editable");
      Require(((TextBox)Field(form,"fingerprint")).ReadOnly,"CA fingerprint must be read-only");
     }
    }
   });
  }
  Console.WriteLine("RESULT passed="+passed+" failed="+failed);return failed==0?0:1;
 }
 static void PrepareHiddenLayout(Form form)
 {
  // Construct native handles without Show/ShowDialog so layout includes TabPage controls.
  var handle=form.Handle;CreateChildHandles(form);LayoutChildren(form);
 }
 sealed class NoSecrets:ISecretProtector{public string Protect(string value)=>throw new InvalidOperationException("No secret write expected in form construction tests.");public string Unprotect(string value)=>throw new InvalidOperationException("No secret read expected in form construction tests.");}
 static void CreateChildHandles(Control root){foreach(Control control in root.Controls){var handle=control.Handle;CreateChildHandles(control);}}
 static void LayoutChildren(Control root){root.PerformLayout();foreach(Control control in root.Controls)LayoutChildren(control);}
 static System.Collections.Generic.IEnumerable<Control> Descendants(Control root){foreach(Control control in root.Controls){yield return control;foreach(var child in Descendants(control))yield return child;}}
}
