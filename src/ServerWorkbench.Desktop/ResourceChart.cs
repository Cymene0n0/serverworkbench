using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using ServerWorkbench.Core;
namespace ServerWorkbench.Desktop
{
 internal sealed class ResourceChart : Control
 {
  private readonly Queue<ResourceSample> history=new Queue<ResourceSample>();
  public ResourceChart(){DoubleBuffered=true;Height=145;Dock=DockStyle.Top;BackColor=Color.White;Margin=new Padding(0,8,0,8);}
  public void Reset(){history.Clear();Invalidate();}
  public void Add(ResourceSample sample){history.Enqueue(sample);while(history.Count>180)history.Dequeue();Invalidate();}
  protected override void OnPaint(PaintEventArgs e)
  {
   base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;var values=history.ToArray();var latest=values.LastOrDefault();
   using(var title=new Font("Microsoft YaHei UI",9,FontStyle.Bold))using(var small=new Font("Microsoft YaHei UI",8))using(var ink=new SolidBrush(Ui.Ink))using(var muted=new SolidBrush(Ui.Muted)) {
    if(latest==null){g.DrawString("资源监视 · 选择并启动一个服务器",title,muted,12,10);return;}
    string note=string.IsNullOrEmpty(latest.Error)?latest.ProcessCount==0?"未运行 · 保留历史资源曲线":"进程组 "+latest.ProcessCount+" 个进程 · 运行 "+latest.Uptime.ToString(@"dd\.hh\:mm\:ss"):latest.Error;
    g.DrawString(note,Font,muted,12,8);
    int half=Math.Max(100,(Width-42)/2);var cpu=new RectangleF(12,52,half,70);var memory=new RectangleF(30+half,52,half,70);
    bool available=string.IsNullOrEmpty(latest.Error);
    g.DrawString("CPU  "+(available?latest.CpuPercent.ToString("F1")+"%（整机占比）":"不可用"),title,ink,12,30);
    g.DrawString("内存  "+(available?(latest.MemoryBytes/1048576.0).ToString("F0")+" MB":"不可用"),title,ink,30+half,30);
    DrawGraph(g,cpu,values.Select(x=>string.IsNullOrEmpty(x.Error)?x.CpuPercent:double.NaN).ToArray(),100,Ui.Accent);
    DrawGraph(g,memory,values.Select(x=>string.IsNullOrEmpty(x.Error)?x.MemoryBytes/1048576.0:double.NaN).ToArray(),Math.Max(256,values.Where(x=>string.IsNullOrEmpty(x.Error)).Select(x=>x.MemoryBytes/1048576.0).DefaultIfEmpty(0).Max()*1.2),Color.FromArgb(60,112,180));
    g.DrawString("最近 9 分钟 · 每 3 秒采样；包含启动脚本及其所属游戏进程",small,muted,12,124);
   }
  }
  private static void DrawGraph(Graphics g,RectangleF box,double[] values,double maximum,Color color)
  {
   using(var grid=new Pen(Color.FromArgb(232,238,242)))for(int i=0;i<4;i++)g.DrawLine(grid,box.Left,box.Top+i*box.Height/3,box.Right,box.Top+i*box.Height/3);
   if(values.Length<2)return;
   using(var pen=new Pen(color,2))for(int i=1;i<values.Length;i++)if(!double.IsNaN(values[i-1])&&!double.IsNaN(values[i]))
    g.DrawLine(pen,new PointF(box.Left+box.Width*(i-1)/179f,box.Bottom-(float)Math.Max(0,Math.Min(1,values[i-1]/maximum))*box.Height),new PointF(box.Left+box.Width*i/179f,box.Bottom-(float)Math.Max(0,Math.Min(1,values[i]/maximum))*box.Height));
  }
 }
}
