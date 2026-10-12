using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NodeLens {
    public sealed class SparkGraph : FrameworkElement {
        readonly List<Tuple<DateTime, double?, double?>> values = new List<Tuple<DateTime, double?, double?>>();
        public void Add(double? down, double? up) { values.Add(Tuple.Create(DateTime.UtcNow, down, up)); values.RemoveAll(v => v.Item1 < DateTime.UtcNow.AddSeconds(-60)); InvalidateVisual(); }
        public void Clear() { values.Clear(); InvalidateVisual(); }
        public void Seed() { for (int i = 0; i < 60; i++) values.Add(Tuple.Create(DateTime.UtcNow.AddSeconds(i-59), (double?)(7e6 + Math.Sin(i*.38)*2.6e6 + (i%17 < 4 ? 3.5e6 : 0)), (double?)(1e6 + Math.Sin(i*.48)*5e5))); InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc) {
            double w = ActualWidth, h = ActualHeight;
            var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(39, 56, 61)), .6);
            for (int i = 1; i <= 3; i++) dc.DrawLine(gridPen, new Point(0,h*i/3), new Point(w,h*i/3));
            if (values.Count < 2) return;
            double max = Math.Max(1000, values.Max(v => Math.Max(v.Item2 ?? 0, v.Item3 ?? 0))) * 1.12;
            DrawSeries(dc, w, h, max, false, Color.FromRgb(128,235,195));
            DrawSeries(dc, w, h, max, true, Color.FromRgb(232,194,144));
        }
        void DrawSeries(DrawingContext dc, double w, double h, double max, bool up, Color color) {
            var points = new List<Point>(); DateTime now = DateTime.UtcNow;
            Action flush = () => {
                if (points.Count > 1) {
                    var geo = new StreamGeometry(); using (var ctx = geo.Open()) { ctx.BeginFigure(points[0], false, false); ctx.PolyLineTo(points.Skip(1).ToArray(), true, false); } geo.Freeze();
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(color),1.6),geo);
                }
                points.Clear();
            };
            DateTime previous = DateTime.MinValue;
            foreach (var value in values) {
                var n = up ? value.Item3 : value.Item2;
                if (!n.HasValue || value.Item1 - previous > TimeSpan.FromSeconds(5)) flush();
                if (n.HasValue) points.Add(new Point(Math.Max(0,w*(1-(now-value.Item1).TotalSeconds/60)), h-2 - Math.Max(0,n.Value)/max*(h-4)));
                previous = value.Item1;
            }
            flush();
        }
    }
    public sealed class Widget : Window {
        readonly Settings settings;
        readonly bool demo, snapshotMode;
        readonly Border surface;
        readonly SparkGraph graph = new SparkGraph();
        TaskbarPanel taskbar;
        DispatcherTimer taskbarRetry;
        Monitor monitor;
        Snapshot latest = new Snapshot();
        bool closing;
        int generation;
        public Widget(Settings options, bool isDemo, bool snapshot) {
            settings = options; demo = isDemo; snapshotMode = snapshot;
            Title = "NodeLens · VPN 桌面插件"; Width = 390; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true; Background = Brushes.Transparent;
            FontFamily = new FontFamily("Microsoft YaHei UI"); Topmost = settings.Topmost; ShowInTaskbar = false;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NodeLens.Widget.xaml")) surface = (Border)XamlReader.Load(stream);
            Content = surface; ((Border)surface.FindName("GraphHost")).Child = graph;
            surface.PreviewMouseLeftButtonDown += DragSurface;
            ((Grid)surface.FindName("DragBar")).ToolTip = "按住小窗的文字或空白区域拖动";
            B("PinButton").Click += (s,e) => { settings.Topmost = !settings.Topmost; Topmost = settings.Topmost; UpdatePin(); Save(); };
            B("CompactButton").Click += (s,e) => { settings.Compact = !settings.Compact; UpdateCompact(); Save(); };
            B("HideButton").Click += (s,e) => Hide();
            B("SettingsButton").Click += (s,e) => OpenSettings();
            B("ClientButton").Click += (s,e) => SourceMenu();
            B("OpenClientButton").Click += (s,e) => ShowClient();
            B("ProbeButton").Click += (s,e) => { if (monitor != null) monitor.ForceProbe(); };
            ContextMenu = new ContextMenu(); AddMenu(ContextMenu, "显示完整窗口", () => { settings.Compact = false; UpdateCompact(); }); AddMenu(ContextMenu,"显示在任务栏",EnableTaskbar); AddMenu(ContextMenu,"设置",OpenSettings); AddMenu(ContextMenu,"退出 NodeLens",Close);
            Loaded += (s,e) => {
                if (snapshotMode) return;
                if (settings.Left > -9000 && settings.Top > -9000) { Left = settings.Left; Top = settings.Top; }
                else { Left = SystemParameters.WorkArea.Right-ActualWidth-24; Top = SystemParameters.WorkArea.Bottom-ActualHeight-24; }
                ClampPosition(); if(settings.SilentStart&&settings.Taskbar)Hide(); Restart(); if(settings.Taskbar) Dispatcher.BeginInvoke(new Action(EnableTaskbar));
            };
            LocationChanged += (s,e) => { if (IsLoaded && !snapshotMode) { settings.Left = Left; settings.Top = Top; } };
            Closing += (s,e) => { closing = true; if(taskbarRetry!=null)taskbarRetry.Stop(); generation++; if (monitor != null) monitor.Dispose(); if(taskbar!=null)taskbar.Dispose(); Save(); };
            UpdatePin(); UpdateCompact();
            if(!snapshotMode&&!demo&&settings.SilentStart&&settings.Taskbar){Opacity=0;ShowActivated=false;}
            if (demo) { graph.Seed(); Update(Monitor.Demo(0)); }
        }
        void DragSurface(object sender, MouseButtonEventArgs e) {
            var target = e.OriginalSource as DependencyObject;
            while (target != null && target != surface) {
                if (target is System.Windows.Controls.Primitives.ButtonBase) return;
                var content = target as FrameworkContentElement;
                target = content != null ? content.Parent : VisualTreeHelper.GetParent(target);
            }
            if (e.LeftButton != MouseButtonState.Pressed) return;
            e.Handled = true;
            try {
                DragMove();
                settings.Left = Left; settings.Top = Top;
                Save();
            } catch (InvalidOperationException) { }
        }
        Button B(string name) { return (Button)surface.FindName(name); }
        TextBlock T(string name) { return (TextBlock)surface.FindName(name); }
        void AddMenu(ContextMenu menu, string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (s,e) => action(); menu.Items.Add(item); }
        void Save() { if (!snapshotMode && !demo) { try { settings.Save(); } catch { } } }
        void ClampPosition() {
            var source = PresentationSource.FromVisual(this); double scaleX = source == null ? 1 : source.CompositionTarget.TransformToDevice.M11; double scaleY = source == null ? 1 : source.CompositionTarget.TransformToDevice.M22;
            bool visible = Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle((int)(Left*scaleX),(int)(Top*scaleY), (int)(ActualWidth*scaleX),(int)(ActualHeight*scaleY))));
            if (!visible) { Left=SystemParameters.WorkArea.Right-ActualWidth-24; Top=SystemParameters.WorkArea.Bottom-ActualHeight-24; }
        }
        void EnableTaskbar() {
            if(taskbar==null||taskbar.IsDisposed)taskbar=new TaskbarPanel(()=>Dispatcher.Invoke(new Action(DisableTaskbar)),()=>Dispatcher.Invoke(new Action(OpenSettings)),()=>Dispatcher.Invoke(new Action(Close)));
            if(taskbar.Attach()){if(taskbarRetry!=null)taskbarRetry.Stop();settings.Taskbar=true;taskbar.UpdateData(latest);Hide();Save();}
            else {
                if(taskbarRetry==null){taskbarRetry=new DispatcherTimer {Interval=TimeSpan.FromSeconds(3)};taskbarRetry.Tick+=(s,e)=>EnableTaskbar();}
                taskbarRetry.Start();
            }
        }
        void DisableTaskbar(){if(taskbarRetry!=null)taskbarRetry.Stop();settings.Taskbar=false;if(taskbar!=null){taskbar.Dispose();taskbar=null;}Restore();Save();}
        public void Restore() { UpdateCompact(); Show(); WindowState = WindowState.Normal; ClampPosition(); Activate(); }
        void UpdatePin() { B("PinButton").Foreground = settings.Topmost ? new SolidColorBrush(Color.FromRgb(133,229,194)) : new SolidColorBrush(Color.FromRgb(130,150,155)); }
        void UpdateCompact() {
            ((StackPanel)surface.FindName("FullPanel")).Visibility = settings.Compact ? Visibility.Collapsed : Visibility.Visible;
            ((StackPanel)surface.FindName("MiniPanel")).Visibility = settings.Compact ? Visibility.Visible : Visibility.Collapsed;
            ((Grid)surface.FindName("DragBar")).Visibility = settings.Compact ? Visibility.Collapsed : Visibility.Visible;
            SizeToContent = settings.Compact ? SizeToContent.WidthAndHeight : SizeToContent.Height;
            Width = settings.Compact ? double.NaN : 390;
            surface.Padding = settings.Compact ? new Thickness(12,7,12,7) : new Thickness(21,17,21,16);
            surface.CornerRadius = new CornerRadius(settings.Compact ? 9 : 20);
            Opacity = settings.Compact ? 0.4 : 1.0;
            B("CompactButton").Content = settings.Compact ? "展开" : "收起";
        }

        void Restart() {
            if (monitor != null) monitor.Dispose(); graph.Clear(); if (demo) graph.Seed();
            int current = ++generation;
            monitor = new Monitor(settings, s => Dispatcher.BeginInvoke(new Action(() => { if (!closing && generation == current) Update(s); })), demo);
            monitor.Start();
        }
        void SourceMenu() {
            var menu = new ContextMenu();
            string[] keys = { "auto", "clash", "fatcat", "nord" }; string[] labels = { "自动识别", "Clash Verge", "肥猫云 Lite", "NordVPN" };
            for (int i=0;i<keys.Length;i++) { string key=keys[i]; var item=new MenuItem { Header=labels[i], IsCheckable=true, IsChecked=settings.Source==key }; item.Click += (s,e) => { settings.Source=key; Save(); Restart(); }; menu.Items.Add(item); }
            B("ClientButton").ContextMenu = menu; menu.PlacementTarget=B("ClientButton"); menu.IsOpen=true;
        }
        public void Update(Snapshot value) {
            latest = value;
            if(taskbar!=null&&!taskbar.IsDisposed)taskbar.UpdateData(value);
            var down = Units.Rate(value.Download); var up = Units.Rate(value.Upload);
            T("ClientName").Text=value.Client; T("StateText").Text=value.IsDemo ? "演示数据" : value.State;
            T("NodeText").Text=value.Node; T("NodeText").ToolTip=value.Node; T("DetailText").Text=value.Detail; T("DetailText").ToolTip=value.Detail;
            T("DownValue").Text=down[0]; T("DownUnit").Text=down[1]; T("UpValue").Text=up[0]; T("UpUnit").Text=up[1];
            string delay=value.Delay.HasValue ? Math.Round(value.Delay.Value).ToString()+" ms" : "— ms";
            T("DelayText").Text=delay; T("LatencyNote").Text=value.LatencySource; T("LatencyNote").ToolTip=value.LatencySource+"。测试目标："+value.ProbeUrl+"；HTTP 往返时间，不等同于 ICMP ping。";
            B("ProbeButton").ToolTip="探测目标："+value.ProbeUrl+"。点击立即测量；不是带宽测速。";
            T("DataNote").Text=value.SpeedSource; T("DataNote").ToolTip=value.SpeedSource+"。KB / MB 使用十进制单位。";
            T("MiniNode").Text=value.Node; T("MiniNode").ToolTip=value.Detail; T("MiniDown").Text="↓ "+down[0]+" "+down[1]; T("MiniUp").Text="↑ "+up[0]+" "+up[1]; T("MiniDelay").Text=delay;
            T("MiniNote").Text=value.IsDemo ? "演示数据 · 非实际网络流量" : value.Client+" · "+value.State;
            ((System.Windows.Shapes.Ellipse)surface.FindName("StatusDot")).Fill = new SolidColorBrush(value.IsDemo ? Color.FromRgb(229,193,126) : value.Connected ? Color.FromRgb(128,235,195) : Color.FromRgb(137,155,160));
            graph.Add(value.Download,value.Upload);
            B("ProbeButton").IsEnabled=settings.ProbeEnabled && !value.IsDemo && (value.Connection != null && !string.IsNullOrEmpty(value.ProbeNode) || value.BindAddress != null);
        }
        void OpenSettings() {
            var dialog = new SettingsWindow(settings,latest.Groups) { Owner=this };
            if (dialog.ShowDialog()==true) { Save(); Restart(); }
        }
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr handle,int command);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr handle);
        void ShowClient() {
            string process=latest.Client.Contains("肥猫") ? "fcclient" : latest.Client=="NordVPN" ? "NordVPN" : "clash-verge";
            var app=Process.GetProcessesByName(process).FirstOrDefault(p=>p.MainWindowHandle!=IntPtr.Zero);
            if(app!=null) { ShowWindow(app.MainWindowHandle,9); SetForegroundWindow(app.MainWindowHandle); if(monitor!=null) monitor.Rediscover(); }
            else MessageBox.Show(this,"请从开始菜单或系统托盘打开 "+latest.Client+" 的主窗口。连接后小窗会自动读取。","显示客户端",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        public void SaveImage(string path) {
            UpdateLayout(); var size = new Size(ActualWidth, ActualHeight); surface.Measure(size); surface.Arrange(new Rect(size)); surface.UpdateLayout();
            const double scale=2;
            var image=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth*scale),(int)Math.Ceiling(ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
            image.Render(surface); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using(var file=File.Create(path)) encoder.Save(file);
        }
    }
    public sealed class SettingsWindow : Window {
        readonly Settings settings;
        public SettingsWindow(Settings options,List<string> groups) {
            settings=options; Title="NodeLens 设置"; Width=460; SizeToContent=SizeToContent.Height; ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner;
            Background=new SolidColorBrush(Color.FromRgb(20,29,34)); Foreground=Brushes.White; FontFamily=new FontFamily("Microsoft YaHei UI"); MaxHeight=Math.Max(400,SystemParameters.WorkArea.Height-60);
            var stack=new StackPanel { Margin=new Thickness(24) }; Content=new ScrollViewer {Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            stack.Children.Add(new TextBlock { Text="连接与采集",FontSize=21,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,6) });
            stack.Children.Add(new TextBlock { Text="自动发现本机客户端，也可指定本地接口。",Foreground=Brushes.LightGray,FontSize=12,Margin=new Thickness(0,0,0,17) });
            Label(stack,"数据来源");
            var source=new ComboBox { ItemsSource=new[] {"自动识别","Clash Verge","肥猫云 Lite","NordVPN"},SelectedIndex=Array.IndexOf(new[] {"auto","clash","fatcat","nord"},settings.Source),Padding=new Thickness(6) }; stack.Children.Add(source);
            Label(stack,"关注策略组（仅接口模式；不会切换 VPN 节点）");
            var group=new ComboBox { IsEditable=true,ItemsSource=groups,Text=settings.Group,Padding=new Thickness(6) }; stack.Children.Add(group);
            Label(stack,"本地接口地址（留空自动发现；或 pipe://管道名）");
            var address=new TextBox { Text=settings.Endpoint,Padding=new Thickness(7) }; stack.Children.Add(address);
            Label(stack,"接口密钥（仅在本机加密保存）");
            var secret=new PasswordBox { Password=settings.Secret,Padding=new Thickness(7) }; stack.Children.Add(secret);
            Label(stack,"配置文件（可选，支持 Clash YAML / sing-box JSON）");
            var fileGrid=new Grid(); fileGrid.ColumnDefinitions.Add(new ColumnDefinition()); fileGrid.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
            var file=new TextBox { Text=settings.ConfigPath,Padding=new Thickness(7) }; var browse=new Button { Content="选择",Padding=new Thickness(10,5,10,5),Margin=new Thickness(7,0,0,0) }; Grid.SetColumn(browse,1); fileGrid.Children.Add(file);fileGrid.Children.Add(browse);stack.Children.Add(fileGrid);
            browse.Click+=(s,e)=>{var picker=new Microsoft.Win32.OpenFileDialog {Filter="配置文件|*.yaml;*.yml;*.json|所有文件|*.*"};if(picker.ShowDialog()==true)file.Text=picker.FileName;};
            var probe=new CheckBox { Content="每 30 秒探测一次 HTTP 延时",IsChecked=settings.ProbeEnabled,Margin=new Thickness(0,17,0,8),Foreground=Brushes.White };stack.Children.Add(probe);
            var startup=new CheckBox { Content="开机自启动（登录后显示在任务栏）",Margin=new Thickness(0,4,0,8),Foreground=Brushes.White };stack.Children.Add(startup);
            string startupError=null;
            try { startup.IsChecked=StartupRegistration.IsEnabled(); }
            catch(Exception ex) { startup.IsEnabled=false;startupError="无法读取自启动设置："+ex.Message; }
            stack.Children.Add(new TextBlock { Text="勾选后点击保存生效；请将程序保留在固定目录。",FontSize=11,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8) });
            var note=new TextBlock { Text="Clash / 肥猫云：通过本地接口读取节点和速率。\n接口接通后，可关闭主窗口并保持后台连接。\nNordVPN：专用网卡速率，节点名称尚未支持。\n插件不再截图或识别客户端窗口。\n延时使用 Cloudflare / Google 的 204 页面；\n具体目标可悬停查看，不做带宽测速。",TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brushes.LightGray,LineHeight=20,Margin=new Thickness(0,3,0,15) };stack.Children.Add(note);
            var error=new TextBlock { Text=startupError,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightSalmon,Margin=new Thickness(0,0,0,10) };stack.Children.Add(error);
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};stack.Children.Add(buttons);
            var cancel=new Button {Content="取消",IsCancel=true,Padding=new Thickness(20,8,20,8),Margin=new Thickness(0,0,8,0)};buttons.Children.Add(cancel);
            var save=new Button {Content="保存并重连",IsDefault=true,Padding=new Thickness(20,8,20,8)};buttons.Children.Add(save);
            save.Click+=(s,e)=>{
                try {
                    string endpoint=Endpoint.Normalize(address.Text);
                    if(file.Text.Trim().Length>0 && !File.Exists(file.Text.Trim()))throw new ArgumentException("配置文件不存在");
                    if(source.SelectedIndex<0)throw new ArgumentException("请选择数据来源");
                    if(secret.Password.IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("接口密钥不能包含换行");
                    if(startup.IsEnabled) {
                        try { StartupRegistration.SetEnabled(startup.IsChecked==true); }
                        catch(Exception ex) { throw new InvalidOperationException("无法保存自启动设置："+ex.Message,ex); }
                    }
                    settings.Source=new[]{"auto","clash","fatcat","nord"}[source.SelectedIndex];settings.Endpoint=endpoint;settings.Secret=secret.Password;settings.ConfigPath=file.Text.Trim();settings.Group=group.Text.Trim();settings.ProbeEnabled=probe.IsChecked==true;
                    DialogResult=true;
                } catch(Exception ex) {error.Text=ex.Message;}
            };
        }
        static void Label(StackPanel panel,string label) {panel.Children.Add(new TextBlock {Text=label,FontSize=11,Foreground=Brushes.LightGray,Margin=new Thickness(0,12,0,6)});}
    }
}
