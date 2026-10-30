using System;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ServerWorkbench.Core;

namespace ServerWorkbench.Desktop
{
 internal sealed class FrpTunnelDialog : Form
 {
  private readonly FrpTunnelService tunnels;
  private readonly ServerInstance instance;
  private readonly TextBox host=Ui.Text(),serverName=Ui.Text(),caFile=Ui.Text(),token=Ui.Text(),fingerprint=Ui.Text("",true);
  private readonly NumericUpDown serverPort=new NumericUpDown{Minimum=1,Maximum=65535,Value=7000,Dock=DockStyle.Fill};
  private readonly NumericUpDown remotePort=new NumericUpDown{Minimum=1024,Maximum=65535,Value=7777,Dock=DockStyle.Fill};
  private FrpTunnelDialog(FrpTunnelService tunnels,ServerInstance instance)
  {
   this.tunnels=tunnels;this.instance=instance;
   Text="frp 穿透配置 · "+instance.Name;Font=new Font("Microsoft YaHei UI",9);Width=820;Height=720;MinimumSize=new Size(680,570);StartPosition=FormStartPosition.CenterParent;BackColor=Ui.Background;Padding=new Padding(22);
   var settings=tunnels.Load(instance)??new FrpTunnelSettings{RemotePort=instance.Port>0?instance.Port:7777};
   host.Text=settings.ServerAddress??"";serverName.Text=settings.ServerName??"";caFile.Text=settings.TrustedCaFile??"";serverPort.Value=Math.Max(1,Math.Min(65535,settings.ServerPort));remotePort.Value=Math.Max(1024,Math.Min(65535,settings.RemotePort));
   token.UseSystemPasswordChar=true;token.MaxLength=128;fingerprint.ReadOnly=true;fingerprint.WordWrap=true;
   var fields=Ui.Fields();var scroll=new Panel{Dock=DockStyle.Fill,AutoScroll=true};scroll.Controls.Add(fields);
   Ui.Field(fields,"本机目标（固定）",Ui.Label("127.0.0.1:"+instance.Port+" / TCP · "+instance.Name));
   Ui.Field(fields,"中转节点地址",host);Ui.Field(fields,"中转控制端口",serverPort);Ui.Field(fields,"朋友连接的端口",remotePort);
   Ui.Field(fields,"证书中的节点名称",serverName);Ui.Field(fields,"名称说明",Ui.Label("填写节点证书匹配的域名或 IP，由节点提供者告知。",true));
   var picker=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=2,ColumnStyles={new ColumnStyle(SizeType.Percent,100),new ColumnStyle(SizeType.AutoSize)}};
   picker.Controls.Add(caFile,0,0);picker.Controls.Add(Ui.Button("选择 CA 证书",()=>{using(var dialog=new OpenFileDialog{Title="选择可信节点提供的 CA 证书（PEM）",Filter="PEM 证书|*.pem;*.crt|所有文件|*.*"})if(dialog.ShowDialog(this)==DialogResult.OK)caFile.Text=dialog.FileName;}),1,0);
   Ui.Field(fields,"可信 CA 证书",picker);Ui.Field(fields,"CA 的 SHA256 指纹",fingerprint,50);
   Ui.Field(fields,"节点 Token（新值）",token);
   Ui.Field(fields,"Token 说明",Ui.Label(tunnels.HasSavedToken(instance)?"已有本机加密 Token；留空保留，填写新值则替换。":"首次配置请填写节点 Token（32–128 位字母、数字、- 或 _，建议 64 位随机十六进制）。",true));
   Ui.Field(fields,"连接方式",Ui.Label("始终使用 TLS 并校验节点证书；只转发所选游戏的本机 TCP 端口。",true));
   var tip=Ui.Label("需要你自己的公网 VPS 或可信服务商提供的 frps 节点。\n保存配置不会启动穿透；Token 仅供当前 Windows 用户解密。请向节点提供者核对 CA 指纹。",true);tip.Dock=DockStyle.Top;tip.AutoSize=false;tip.Height=74;
   var bottom=Ui.Toolbar();bottom.Dock=DockStyle.Bottom;var save=Ui.Button("保存配置",()=>Ui.Try(this,Save),true);var cancel=Ui.Button("取消",()=>DialogResult=DialogResult.Cancel);bottom.Controls.Add(save);bottom.Controls.Add(cancel);AcceptButton=save;CancelButton=cancel;
   Controls.Add(scroll);Controls.Add(tip);Controls.Add(bottom);caFile.TextChanged+=(s,e)=>UpdateFingerprint();UpdateFingerprint();Ui.ConfigureWindow(this);
  }
  private void UpdateFingerprint()
  {
   try
   {
    if(caFile.Text.StartsWith(@"\\",StringComparison.Ordinal)||caFile.Text.StartsWith("//",StringComparison.Ordinal)){fingerprint.Text="请先将 CA 公钥证书保存到本地磁盘。";return;}
    if(!File.Exists(caFile.Text)||new FileInfo(caFile.Text).Length>128*1024){fingerprint.Text="尚未选择可读取的 PEM CA 证书。";return;}
    var text=File.ReadAllText(caFile.Text);if(text.IndexOf("PRIVATE KEY",StringComparison.OrdinalIgnoreCase)>=0){fingerprint.Text="文件含有私钥，请改选仅包含公开 CA 证书的文件。";return;}var matches=Regex.Matches(text,@"-----BEGIN CERTIFICATE-----\s*(.*?)\s*-----END CERTIFICATE-----",RegexOptions.Singleline);
    if(matches.Count!=1){fingerprint.Text="请选择只含单张 CA 公钥证书的 PEM 文件。";return;}var match=matches[0];
    var der=Convert.FromBase64String(Regex.Replace(match.Groups[1].Value,@"\s","") );if(System.Security.Cryptography.X509Certificates.X509Certificate2.GetCertContentType(der)!=System.Security.Cryptography.X509Certificates.X509ContentType.Cert){fingerprint.Text="不接受 PFX、PKCS7 或私钥容器。";return;}using(var sha=SHA256.Create())fingerprint.Text=BitConverter.ToString(sha.ComputeHash(der)).Replace("-","");
   }
   catch(IOException){fingerprint.Text="证书文件暂不可读取。";}catch(UnauthorizedAccessException){fingerprint.Text="没有读取证书的权限。";}catch(FormatException){fingerprint.Text="证书的 PEM 内容无效。";}catch(CryptographicException){fingerprint.Text="证书格式无效。";}
  }
  private void Save()
  {
   tunnels.Save(instance,new FrpTunnelSettings{ServerAddress=host.Text.Trim(),ServerPort=(int)serverPort.Value,ServerName=serverName.Text.Trim(),RemotePort=(int)remotePort.Value,TrustedCaFile=caFile.Text.Trim()},token.Text);
   token.Clear();DialogResult=DialogResult.OK;
  }
  public static bool Edit(IWin32Window owner,FrpTunnelService tunnels,ServerInstance instance)
  {using(var dialog=new FrpTunnelDialog(tunnels,instance))return dialog.ShowDialog(owner)==DialogResult.OK;}
  protected override void Dispose(bool disposing){if(disposing)token.Clear();base.Dispose(disposing);}
 }
}
