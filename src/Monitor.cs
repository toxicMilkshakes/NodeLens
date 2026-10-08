using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NodeLens {
    public sealed class Monitor : IDisposable {
        readonly Settings settings;
        readonly Action<Snapshot> publish;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        Endpoint endpoint;
        DateTime discoveryAt = DateTime.MinValue;
        string apiError = "未发现可用的本地接口";
        string activeSource = "";
        CancellationTokenSource trafficStop;
        readonly object gate = new object();
        double? up, down;
        DateTime trafficAt = DateTime.MinValue;
        string probeKey = "", probeNote = "";
        double? probeDelay;
        DateTime probeAt = DateTime.MinValue, probeStarted = DateTime.MinValue;
        bool probing;
        long nordDown, nordUp;
        double nordAt;
        string nordId;
        readonly Stopwatch clock = Stopwatch.StartNew();
        int demoTick;
        readonly bool demo;
        public Monitor(Settings options, Action<Snapshot> onSnapshot, bool isDemo) { settings = options; publish = onSnapshot; demo = isDemo; }
        public void Start() { Task.Run((Func<Task>)Run); }
        public void ForceProbe() { lock (gate) probeStarted = DateTime.MinValue; }
        public void Rediscover() { endpoint = null; discoveryAt = DateTime.MinValue; }
        public void Dispose() { stop.Cancel(); if (trafficStop != null) trafficStop.Cancel(); }
        async Task Run() {
            while (!stop.IsCancellationRequested) {
                try {
                    Snapshot s;
                    if (demo) s = Demo(demoTick++);
                    else {
                        string source = settings.Source;
                        if (source == "auto") source = Discovery.NordAdapter() != null ? "nord" : Discovery.Running("fcclient") ? "fatcat" : "clash";
                        if (source != activeSource) { activeSource = source; endpoint = null; discoveryAt = DateTime.MinValue; if (trafficStop != null) trafficStop.Cancel(); }
                        s = source == "nord" ? Nord() : Clash(source);
                        if (!stop.IsCancellationRequested) ApplyProbe(s);
                    }
                    if (!stop.IsCancellationRequested) publish(s);
                } catch {
                    if (!stop.IsCancellationRequested) publish(new Snapshot { State = "读取暂时中断", Detail = "稍后自动重试；可在设置中检查数据来源" });
                }
                try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
        Snapshot Clash(string source) {
            if (endpoint == null && DateTime.UtcNow - discoveryAt > TimeSpan.FromSeconds(12)) {
                discoveryAt = DateTime.UtcNow;
                var options = new Settings { Source = source, Endpoint = settings.Endpoint, EncryptedSecret = settings.EncryptedSecret, ConfigPath = settings.ConfigPath };
                foreach (var candidate in Discovery.Candidates(options)) {
                    if (stop.IsCancellationRequested) return new Snapshot();
                    try {
                        var proxies = Api.Get(candidate, "/proxies", 900);
                        if (Json.Obj(proxies, "proxies") == null) continue;
                        endpoint = candidate; StartTraffic(candidate); break;
                    } catch (ApiException e) { if (e.Status == 401 || e.Status == 403) apiError = "接口需要正确的密钥，请在设置中填写"; }
                    catch { }
                }
            }
            if (endpoint != null) {
                try {
                    var connection = endpoint;
                    var proxies = Json.Obj(Api.Get(connection, "/proxies", 2000), "proxies");
                    if (proxies == null) throw new IOException("Invalid proxy response");
                    string mode = "";
                    try { mode = Json.Text(Api.Get(connection, "/configs", 1200), "mode"); } catch { }
                    var selected = Routing.Select(proxies, settings.Group, mode);
                    var s = new Snapshot { Client = connection.Client, Connected = true, State = "接口已连接", Node = selected.Name, Group = selected.Group, Groups = selected.Groups, Detail = selected.Explanation, SpeedSource = "客户端总速率 · 包含客户端统计的直连流量", Delay = selected.Delay, LatencySource = selected.DelayNote, Connection = connection, Mode = mode, LatencyKey = connection.Address + "|" + selected.Name, ProbeNode = selected.CanProbe ? selected.Name : "" };
                    if (source == "fatcat") s.ProbeUrl = Discovery.FatCatTestUrl();
                    s.State = "后台采集中";
                    lock (gate) {
                        if (DateTime.UtcNow - trafficAt < TimeSpan.FromSeconds(4)) { s.Download = down; s.Upload = up; }
                        else s.SpeedSource = "流量流暂不可用 · 正在重连";
                    }
                    return s;
                } catch {
                    endpoint = null; discoveryAt = DateTime.MinValue;
                    if (trafficStop != null) trafficStop.Cancel();
                    lock (gate) { up = down = null; trafficAt = DateTime.MinValue; }
                }
            }
            if (source == "fatcat") {
                return new Snapshot { Client = "肥猫云 Lite", State = "等待后台接口", Node = "尚未接通数据", Detail = "需要启用肥猫云本地接口；无需保持主窗口打开", SpeedSource = "接口未接通 · 不显示缓存或估算速率" };
            }
            return new Snapshot { Client = "Clash Verge", State = "等待客户端接口", Detail = apiError, SpeedSource = "启动客户端后自动重试" };
        }
        void StartTraffic(Endpoint connection) {
            if (trafficStop != null) trafficStop.Cancel();
            trafficStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var cancellation = trafficStop;
            lock (gate) { up = down = null; trafficAt = DateTime.MinValue; }
            Task.Run(async () => {
                while (!cancellation.IsCancellationRequested) {
                    try {
                        using (var response = Api.Open(connection, "/traffic", 6000))
                        using (cancellation.Token.Register(response.Dispose)) {
                            while (!cancellation.IsCancellationRequested) {
                                response.Arm(6000);
                                string line = ChunkedStream.Line(response.Body, 16384);
                                if (line == null) break;
                                if (line.Length == 0) continue;
                                var values = Json.Parse(line);
                                var download = Json.Number(values, "down"); var upload = Json.Number(values, "up");
                                if (!download.HasValue || !upload.HasValue) throw new IOException("Missing traffic values");
                                lock (gate) { if (!cancellation.IsCancellationRequested) { down = download; up = upload; trafficAt = DateTime.UtcNow; } }
                            }
                        }
                    } catch { }
                    lock (gate) { if (!cancellation.IsCancellationRequested) { up = down = null; trafficAt = DateTime.MinValue; } }
                    try { await Task.Delay(1500, cancellation.Token); } catch { break; }
                }
            });
        }
        Snapshot Nord() {
            var adapter = Discovery.NordAdapter();
            if (adapter == null) { nordId = null; return new Snapshot { Client = "NordVPN", State = "未检测到活动隧道", Node = "NordVPN 未连接", Detail = "连接 NordVPN 后自动读取专用网卡速率", SpeedSource = "NordVPN 专用网卡" }; }
            var s = new Snapshot { Client = "NordVPN", Node = "节点名称暂不支持", Detail = "后台读取隧道速率；此版本尚未接通节点名称" };
            s.Connected = true; s.State = "检测到活动隧道"; s.SpeedSource = "隧道网卡 · " + adapter.Name;
            s.BindAddress = adapter.GetIPProperties().UnicastAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            var stats = adapter.GetIPv4Statistics(); double now = clock.Elapsed.TotalSeconds;
            if (nordId == adapter.Id && now > nordAt && now - nordAt < 10 && stats.BytesReceived >= nordDown && stats.BytesSent >= nordUp) {
                s.Download = (stats.BytesReceived - nordDown) / (now - nordAt); s.Upload = (stats.BytesSent - nordUp) / (now - nordAt);
            } else { s.Download = s.Upload = null; }
            nordId = adapter.Id; nordDown = stats.BytesReceived; nordUp = stats.BytesSent; nordAt = now;
            s.LatencyKey = "nord|" + adapter.Id + "|" + s.Node;
            return s;
        }
        void ApplyProbe(Snapshot s) {
            bool possible = (s.Connection != null && !string.IsNullOrEmpty(s.ProbeNode)) || s.ProxyAddress != null || s.BindAddress != null;
            if (!possible || !settings.ProbeEnabled) return;
            lock (gate) {
                if (s.LatencyKey == probeKey && DateTime.UtcNow - probeAt < TimeSpan.FromSeconds(45)) { s.Delay = probeDelay; s.LatencySource = probeNote; }
                if (probing || (s.LatencyKey == probeKey && DateTime.UtcNow - probeStarted < TimeSpan.FromSeconds(30))) return;
                probing = true; probeStarted = DateTime.UtcNow; string key = s.LatencyKey;
                Task.Run(() => {
                    double? measured = null; string note = "探测失败 · ";
                    string kind = s.Connection != null ? "节点 HTTP" : s.ProxyAddress != null ? "代理 HTTP" : "隧道 HTTP";
                    try {
                        if (s.Connection != null) {
                            measured = Json.Number(Api.Get(s.Connection, "/proxies/" + Uri.EscapeDataString(s.ProbeNode) + "/delay?timeout=6000&url=" + Uri.EscapeDataString(s.ProbeUrl), 7000), "delay");
                            if (measured <= 0) measured = null;
                        } else measured = ProbeHttp(s.ProxyAddress, s.BindAddress, s.ProbeUrl);
                        note = measured.HasValue ? kind + " · " + DateTime.Now.ToString("HH:mm:ss") : kind + " · 无结果";
                    } catch { note = kind + " · 探测失败"; }
                    lock (gate) { probeDelay = measured; probeNote = note; probeKey = key; probeAt = DateTime.UtcNow; probing = false; }
                });
            }
        }
        static double ProbeHttp(string proxy, IPAddress bind, string url) {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = proxy == null ? null : new WebProxy(proxy);
            request.AllowAutoRedirect = false; request.KeepAlive = false; request.Timeout = 7000; request.ReadWriteTimeout = 7000;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            if (bind != null) request.ServicePoint.BindIPEndPointDelegate = (sp, remote, retry) => new IPEndPoint(bind, 0);
            request.ConnectionGroupName = Guid.NewGuid().ToString("N");
            var timer = Stopwatch.StartNew();
            try {
                using (var response = (HttpWebResponse)request.GetResponse()) {
                    if ((int)response.StatusCode != 204) throw new IOException("Unexpected probe response");
                    return timer.Elapsed.TotalMilliseconds;
                }
            } finally { request.ServicePoint.CloseConnectionGroup(request.ConnectionGroupName); request.ServicePoint.BindIPEndPointDelegate = null; }
        }
        public static Snapshot Demo(int tick) {
            return new Snapshot { Client = "Clash Verge", Connected = true, IsDemo = true, State = "演示数据", Node = "香港 HK-09", Group = "节点选择", Groups = new List<string> { "节点选择", "自动选择", "GLOBAL" }, Detail = "关注策略组 · 节点选择", SpeedSource = "演示数据 · 非实际网络流量", Download = 12400000 + Math.Sin(tick * .6) * 2200000, Upload = 846000 + Math.Cos(tick * .8) * 120000, Delay = 32, LatencySource = "节点 HTTP · 演示", Mode = "rule" };
        }
    }
}
