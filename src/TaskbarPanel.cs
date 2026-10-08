using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms=System.Windows.Forms;
namespace NodeLens {
 public sealed class TaskbarPanel : Forms.Form {
  [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string c,string n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p,IntPtr a,string c,string n);
  [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr h,IntPtr p);
  [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
  [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetStyle(IntPtr h,int i);
  [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetStyle(IntPtr h,int i,IntPtr v);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int hgt,uint flags);
  readonly Forms.Timer timer=new Forms.Timer();
  readonly Forms.ToolTip tip=new Forms.ToolTip();
  Snapshot latest=new Snapshot();
  public TaskbarPanel(Action floating,Action settings,Action exit) {
   FormBorderStyle=Forms.FormBorderStyle.None;ShowInTaskbar=false;BackColor=Color.FromArgb(24,30,32);ForeColor=Color.White;DoubleBuffered=true;Opacity=.4;
   var menu=new Forms.ContextMenuStrip();menu.Items.Add("切回桌面悬浮窗",null,(s,e)=>floating());menu.Items.Add("设置",null,(s,e)=>settings());menu.Items.Add("退出 NodeLens",null,(s,e)=>exit());ContextMenuStrip=menu;
   timer.Interval=2000;timer.Tick+=(s,e)=>Place();
  }
  protected override bool ShowWithoutActivation {get{return true;}}
  public bool Attach(){Show(); if(!Place()){Hide();return false;}timer.Start();return true;}
  bool Place(){
   IntPtr bar=FindWindow("Shell_TrayWnd",null);Rect r;
   if(bar==IntPtr.Zero||!GetWindowRect(bar,out r)||r.Right-r.Left<r.Bottom-r.Top)return false;
   if(GetParent(Handle)!=bar){long style=GetStyle(Handle,-16).ToInt64();SetStyle(Handle,-16,new IntPtr((style&~0x80000000L)|0x40000000L));SetParent(Handle,bar);if(GetParent(Handle)!=bar)return false;}
   float scale;using(var g=CreateGraphics())scale=g.DpiX/96f;
   int width;using(var g=CreateGraphics())using(var font=new Font("Microsoft YaHei UI",9,FontStyle.Regular))width=NodeWidth(g,font,scale)+(int)(224*scale);
   int height=r.Bottom-r.Top;
   IntPtr start=FindWindowEx(bar,IntPtr.Zero,"Start",null);Rect sr;
   if(start!=IntPtr.Zero&&GetWindowRect(start,out sr)&&sr.Left-r.Left>200)width=Math.Min(width,sr.Left-r.Left-20);
   SetWindowPos(Handle,IntPtr.Zero,8,0,width,height,0x0010|0x0040);return true;
  }
  public void UpdateData(Snapshot s){bool changed=latest.Node!=s.Node;latest=s;if(changed&&IsHandleCreated)Place();tip.SetToolTip(this,s.Client+" · "+s.Node+"\n"+s.State+"\n右键切回悬浮窗或设置");Invalidate();}
  int NodeWidth(Graphics g,Font font,float scale){return Math.Min((int)(123*scale),Forms.TextRenderer.MeasureText(g,latest.Node,font,new Size(int.MaxValue,int.MaxValue),Forms.TextFormatFlags.SingleLine|Forms.TextFormatFlags.NoPrefix).Width+(int)(6*scale));}
  protected override void OnPaint(Forms.PaintEventArgs e){
   base.OnPaint(e);float scale=e.Graphics.DpiX/96f;
   using(var font=new Font("Microsoft YaHei UI",9,FontStyle.Regular)){
    string d="↓ "+string.Join(" ",Units.Rate(latest.Download)),u="↑ "+string.Join(" ",Units.Rate(latest.Upload));
    string delay=latest.Delay.HasValue?Math.Round(latest.Delay.Value)+" ms":"— ms";
    int rate=(int)(76*scale),lat=(int)(64*scale),pad=(int)(4*scale),node=Math.Min(NodeWidth(e.Graphics,font,scale),ClientSize.Width-rate*2-lat-pad*2);
    Draw(e.Graphics,latest.Node,font,Color.White,new Rectangle(pad,0,Math.Max(30,node),Height));
    int x=pad+node;Draw(e.Graphics,delay,font,Color.LightGray,new Rectangle(x,0,lat,Height));x+=lat;
    Draw(e.Graphics,d,font,Color.FromArgb(133,229,194),new Rectangle(x,0,rate,Height));x+=rate;
    Draw(e.Graphics,u,font,Color.FromArgb(231,195,148),new Rectangle(x,0,rate,Height));
   }
  }
  static void Draw(Graphics g,string s,Font f,Color c,Rectangle r){Forms.TextRenderer.DrawText(g,s,f,r,c,Forms.TextFormatFlags.VerticalCenter|Forms.TextFormatFlags.SingleLine|Forms.TextFormatFlags.EndEllipsis|Forms.TextFormatFlags.NoPrefix);}
  protected override void Dispose(bool disposing){if(disposing){timer.Dispose();tip.Dispose();}base.Dispose(disposing);}
 }
}
