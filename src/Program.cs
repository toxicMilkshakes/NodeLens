using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace NodeLens {
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            ServicePointManager.DefaultConnectionLimit=12;
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            bool demo=args.Contains("--demo"), snapshot=args.Contains("--snapshot"), live=args.Contains("--snapshot-live");
            Mutex single=null;
            if(!demo&&!snapshot&&!live) {
                bool created; single=new Mutex(true,"Local\\NodeLens.Desktop."+Environment.UserName,out created);
                if(!created) { single.Dispose();return 0; }
            }
            try {
                var app=new Application {ShutdownMode=ShutdownMode.OnMainWindowClose};
                var settings=snapshot||demo||live ? new Settings() : Settings.Load();
                if(args.Contains("--silent")){settings.SilentStart=true;settings.Taskbar=true;}
                if(args.Contains("--compact"))settings.Compact=true;
                var widget=new Widget(settings,demo||snapshot,snapshot||live);
                app.MainWindow=widget;
                app.DispatcherUnhandledException+=(s,e)=>{e.Handled=true;MessageBox.Show("读取暂时中断。请关闭并重新打开 NodeLens；连接设置和 VPN 状态不会被改动。","NodeLens");};
                if(snapshot||live) {
                    int index=Array.IndexOf(args,snapshot?"--snapshot":"--snapshot-live");
                    if(index+1>=args.Length)throw new ArgumentException("Missing snapshot output path");
                    string file=Path.GetFullPath(args[index+1]);
                    widget.Left=-10000;widget.Top=-10000;widget.Topmost=false;
                    widget.Loaded+=(s,e)=>{
                        if(snapshot)widget.Dispatcher.BeginInvoke(new Action(()=>{widget.SaveImage(file);widget.Close();}),DispatcherPriority.ApplicationIdle);
                        else {
                            settings.ProbeEnabled=false;
                            Monitor test=null;
                            test=new Monitor(settings,state=>widget.Dispatcher.BeginInvoke(new Action(()=>{widget.Update(state);widget.SaveImage(file);test.Dispose();widget.Close();})),false);
                            test.Start();
                        }
                    };
                }
                app.Run(widget);return 0;
            } catch(Exception ex) {
                if(snapshot||live)File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"render-error.txt"),ex.ToString());
                else MessageBox.Show("NodeLens 未能启动："+ex.Message,"NodeLens");
                return 1;
            } finally {if(single!=null)single.Dispose();}
        }
    }
}
