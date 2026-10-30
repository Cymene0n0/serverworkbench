using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ServerWorkbench.Core;

namespace ServerWorkbench.Desktop
{
 internal static class Ui
 {
  public static readonly Color Ink = Color.FromArgb(25,42,58);
  public static readonly Color Muted = Color.FromArgb(103,118,132);
  public static readonly Color Accent = Color.FromArgb(26,122,101);
  public static readonly Color Background = Color.FromArgb(244,247,250);
  public static void ConfigureWindow(Form form)
  {
   // Set the design baseline before enabling DPI scaling. Reversing these setters
   // lets WinForms replace the baseline with the current DPI before scaling controls.
   form.AutoScaleDimensions=new SizeF(96,96);form.AutoScaleMode=AutoScaleMode.Dpi;
   form.Shown+=(s,e)=> {
    var area=Screen.FromControl(form).WorkingArea;
    form.MinimumSize=new Size(Math.Min(form.MinimumSize.Width,area.Width),Math.Min(form.MinimumSize.Height,area.Height));
    form.Size=new Size(Math.Min(form.Width,area.Width),Math.Min(form.Height,area.Height));
    form.Location=new Point(Math.Max(area.Left,Math.Min(form.Left,area.Right-form.Width)),Math.Max(area.Top,Math.Min(form.Top,area.Bottom-form.Height)));
   };
  }
  public static Button Button(string text, Action click, bool primary = false)
  {
   var b = new Button { Text=text, AutoSize=true, Height=34, MinimumSize=new Size(80,34), Padding=new Padding(12,4,12,4), FlatStyle=FlatStyle.Flat, Cursor=Cursors.Hand, Margin=new Padding(0,0,8,6), BackColor=primary?Accent:Color.White, ForeColor=primary?Color.White:Ink };
   b.FlatAppearance.BorderColor=primary?Accent:Color.FromArgb(213,222,230);
   b.Click += (s,e)=>click(); return b;
  }
  public static Label Label(string text, bool muted=false) => new Label { Text=text, AutoSize=true, ForeColor=muted?Muted:Ink, Margin=new Padding(0,5,0,8), MaximumSize=new Size(1000,0) };
  public static Label Title(string text) => new Label { Text=text, AutoSize=true, Font=new Font("Microsoft YaHei UI",19,FontStyle.Bold), ForeColor=Ink, Margin=new Padding(0,0,0,8) };
  public static TextBox Text(string text="", bool multiline=false) => new TextBox { Text=text??"", Dock=DockStyle.Fill, Multiline=multiline, Height=multiline?82:28, ScrollBars=multiline?ScrollBars.Vertical:ScrollBars.None, BorderStyle=BorderStyle.FixedSingle, Margin=new Padding(0,3,0,8) };
  public static ComboBox Combo() => new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList, Dock=DockStyle.Fill, Margin=new Padding(0,3,0,8) };
  public static TableLayoutPanel Fields() => new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, ColumnCount=2, Padding=new Padding(0,4,0,0), ColumnStyles={ new ColumnStyle(SizeType.AutoSize),new ColumnStyle(SizeType.Percent,100) } };
  public static void Field(TableLayoutPanel table,string title,Control value,int height=0)
  {
   int row=table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
   table.Controls.Add(Label(title),0,row); table.Controls.Add(value,1,row);
   if(height>0) { value.Height=height; value.MinimumSize=new Size(0,height); }
  }
  public static FlowLayoutPanel Toolbar() => new FlowLayoutPanel { AutoSize=true, Dock=DockStyle.Top, WrapContents=true, Margin=new Padding(0), Padding=new Padding(0,4,0,8) };
  public static void Error(IWin32Window owner,Exception ex)
  {
   if(ex is OperationCanceledException) return;
   MessageBox.Show(owner,ex.GetBaseException().Message,"操作未完成",MessageBoxButtons.OK,MessageBoxIcon.Warning);
  }
  public static void Try(IWin32Window owner,Action action) { try { action(); } catch(Exception ex) { Error(owner,ex); } }
  public static string KindName(ServerKind kind)
  {
   switch(kind) { case ServerKind.Vanilla:return "Minecraft 原版";case ServerKind.Fabric:return "Minecraft Fabric";case ServerKind.Forge:return "Minecraft Forge";case ServerKind.Terraria:return "泰拉瑞亚";case ServerKind.TModLoader:return "tModLoader";default:return "自定义服务端"; }
  }
  public static void Open(string path) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute=true }); }
 }
 internal sealed class ProgressDialog : Form
 {
  private readonly ProgressBar bar=new ProgressBar { Dock=DockStyle.Top, Height=10, Style=ProgressBarStyle.Marquee };
  private readonly TextBox messages=new TextBox { Dock=DockStyle.Fill, ReadOnly=true, Multiline=true, ScrollBars=ScrollBars.Vertical, BackColor=Color.White, BorderStyle=BorderStyle.FixedSingle };
  private readonly Button cancel;
  private readonly CancellationTokenSource cancellation=new CancellationTokenSource();
  private bool complete;
  private ProgressDialog(string title)
  {
   Text=title; Font=new Font("Microsoft YaHei UI",9); Width=670;Height=350;MinimumSize=new Size(540,300); StartPosition=FormStartPosition.CenterParent; ShowInTaskbar=false; Padding=new Padding(18); BackColor=Ui.Background;
   cancel=Ui.Button("取消任务",()=> { cancellation.Cancel();cancel.Enabled=false;cancel.Text="正在取消…"; });
   var bottom=Ui.Toolbar();bottom.Dock=DockStyle.Bottom;bottom.Controls.Add(cancel);
   var space=new Panel { Dock=DockStyle.Top,Height=12 };
   Controls.Add(messages);Controls.Add(space);Controls.Add(bar);Controls.Add(bottom);
   FormClosing += (s,e)=> { if(!complete) { e.Cancel=true;cancellation.Cancel();cancel.Enabled=false;cancel.Text="正在取消…"; } };Ui.ConfigureWindow(this);
  }
  public static T Run<T>(IWin32Window owner,string title,Func<IProgress<WorkProgress>,CancellationToken,Task<T>> operation)
  {
   T result=default(T);Exception error=null;
   using(var dialog=new ProgressDialog(title))
   {
    dialog.Shown += async (s,e)=>
    {
     var progress=new Progress<WorkProgress>(p=>
     {
      if(dialog.IsDisposed) return;
      if(p.Percent.HasValue) { dialog.bar.Style=ProgressBarStyle.Continuous;dialog.bar.Value=Math.Max(0,Math.Min(100,(int)p.Percent.Value)); } else dialog.bar.Style=ProgressBarStyle.Marquee;
      if(!string.IsNullOrWhiteSpace(p.Message)) { if(dialog.messages.TextLength>70000) dialog.messages.Text=dialog.messages.Text.Substring(dialog.messages.TextLength-40000);dialog.messages.AppendText(p.Message+Environment.NewLine); }
     });
     try { result=await Task.Run(()=>operation(progress,dialog.cancellation.Token)); }
     catch(Exception ex) { error=ex; }
     finally { dialog.complete=true;dialog.Close(); }
    };
    dialog.ShowDialog(owner);
   }
   if(error!=null) throw error;return result;
  }
  public static void Run(IWin32Window owner,string title,Func<IProgress<WorkProgress>,CancellationToken,Task> operation)
  { Run(owner,title,async (p,c)=> { await operation(p,c);return true; }); }
  protected override void Dispose(bool disposing) { if(disposing)cancellation.Dispose();base.Dispose(disposing); }
 }
 internal sealed class TextPrompt : Form
 {
  public string Value => value.Text.Trim();
  private readonly TextBox value=Ui.Text();
  private TextPrompt(string title,string help,string initial)
  {
   Text=title;Width=610;Height=210;Font=new Font("Microsoft YaHei UI",9);StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;MinimizeBox=false;MaximizeBox=false;Padding=new Padding(18);BackColor=Ui.Background;
   value.Text=initial??"";value.Dock=DockStyle.Top;
   var helpLabel=Ui.Label(help);helpLabel.Dock=DockStyle.Top;helpLabel.AutoSize=false;helpLabel.Height=42;
   var buttons=Ui.Toolbar();buttons.Dock=DockStyle.Bottom;
   var ok=Ui.Button("确定",()=>DialogResult=DialogResult.OK,true);var no=Ui.Button("取消",()=>DialogResult=DialogResult.Cancel);buttons.Controls.Add(ok);buttons.Controls.Add(no);AcceptButton=ok;CancelButton=no;
   Controls.Add(value);Controls.Add(helpLabel);Controls.Add(buttons);Ui.ConfigureWindow(this);
  }
  public static string Ask(IWin32Window owner,string title,string help,string initial="")
  { using(var f=new TextPrompt(title,help,initial)) return f.ShowDialog(owner)==DialogResult.OK?f.Value:null; }
 }
}
