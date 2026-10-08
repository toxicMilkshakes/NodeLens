using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace NodeLens {
    public static class Json {
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 }; }
        public static Dictionary<string, object> Parse(string text) { return Serializer().Deserialize<Dictionary<string, object>>(text); }
        public static string Text(Dictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : ""; }
        public static Dictionary<string, object> Obj(Dictionary<string, object> o, string key) { object v; return o != null && o.TryGetValue(key, out v) ? v as Dictionary<string, object> : null; }
        public static double? Number(Dictionary<string, object> o, string key) { double v; return double.TryParse(Text(o, key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0 ? (double?)v : null; }
    }
    public sealed class Settings {
        public string Source { get; set; }
        public string Endpoint { get; set; }
        public string EncryptedSecret { get; set; }
        public string ConfigPath { get; set; }
        public string Group { get; set; }
        public bool Topmost { get; set; }
        public bool Compact { get; set; }
        public bool Taskbar { get; set; }
        public bool SilentStart { get; set; }
        public bool HideTray { get; set; }
        public bool ProbeEnabled { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public Settings() { Source = "auto"; Endpoint = ""; EncryptedSecret = ""; ConfigPath = ""; Group = ""; Topmost = true; ProbeEnabled = true; Left = -10000; Top = -10000; }
        [ScriptIgnore] public string Secret {
            get { try { return string.IsNullOrEmpty(EncryptedSecret) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedSecret), null, DataProtectionScope.CurrentUser)); } catch { return ""; } }
            set { EncryptedSecret = string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)); }
        }
        public static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NodeLens", "settings.json"); } }
        public static Settings Load() { try { return Json.Serializer().Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings(); } catch { return new Settings(); } }
        public void Save() {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, Json.Serializer().Serialize(this), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null); else File.Move(temp, FilePath);
        }
    }
    public sealed class Snapshot {
        public string Client = "自动识别";
        public string State = "正在查找客户端";
        public string Node = "等待连接";
        public string Detail = "启动 VPN 客户端后自动读取";
        public string SpeedSource = "尚无数据";
        public string LatencySource = "未测量";
        public string LatencyKey = "";
        public string ProbeNode = "";
        public string Mode = "";
        public double? Download, Upload, Delay;
        public bool Connected, IsDemo;
        public List<string> Groups = new List<string>();
        public string Group = "";
        public DateTime Updated = DateTime.UtcNow;
        public Endpoint Connection;
        public string ProxyAddress;
        public string ProbeUrl = "https://www.gstatic.com/generate_204";
        public IPAddress BindAddress;
    }
    public sealed class Endpoint {
        public string Address, Secret, Client;
        public bool IsPipe { get { return Address.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase); } }
        public static string Normalize(string input) {
            input = (input ?? "").Trim();
            if (input.Length == 0) return "";
            if (input.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase)) {
                string name = input.Substring(7);
                if (!Regex.IsMatch(name, @"^[\w.\-]{1,240}$")) throw new ArgumentException("命名管道名称无效");
                return "pipe://" + name;
            }
            if (input.Contains("pipe\\")) {
                string name = input.Substring(input.LastIndexOf("pipe\\", StringComparison.Ordinal) + 5).TrimStart('\\');
                return Normalize("pipe://" + name);
            }
            if (!input.Contains("://")) input = "http://" + input;
            Uri u;
            if (!Uri.TryCreate(input, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https") || u.UserInfo.Length > 0 || u.AbsolutePath != "/" || u.Query.Length > 0 || u.Fragment.Length > 0) throw new ArgumentException("请输入本机接口地址，例如 http://127.0.0.1:9097");
            IPAddress ip;
            bool local = u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(u.Host.Trim('[',']'), out ip) && IPAddress.IsLoopback(ip));
            if (!local) throw new ArgumentException("只支持本机回环接口，避免把接口密钥发往外部地址");
            return u.GetLeftPart(UriPartial.Authority);
        }
    }
    public static class Discovery {
        public static string Scalar(string line) {
            string value = line.Trim();
            if (value.StartsWith("\"")) {
                var match = Regex.Match(value, "^\"(?:\\\\.|[^\"])*\"");
                if (!match.Success) return "";
                try { return Json.Serializer().Deserialize<string>(match.Value); } catch { return ""; }
            }
            if (value.StartsWith("'")) {
                var match = Regex.Match(value, "^'((?:''|[^'])*)'");
                return match.Success ? match.Groups[1].Value.Replace("''", "'") : "";
            }
            value = Regex.Replace(value, @"\s+#.*$", "").Trim();
            return value == "null" || value == "~" ? "" : value;
        }
        public static List<Endpoint> FromFile(string file, string client) {
            var result = new List<Endpoint>();
            try {
                if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024) return result;
                string address = "", pipe = "", secret = "";
                string text = File.ReadAllText(file);
                if (text.TrimStart().StartsWith("{")) {
                    var root = Json.Parse(text);
                    var api = Json.Obj(Json.Obj(root, "experimental"), "clash_api");
                    if (api != null) { address = Json.Text(api, "external_controller"); secret = Json.Text(api, "secret"); }
                } else {
                    foreach (string line in text.Split('\n')) {
                        var m = Regex.Match(line, @"^(external-controller|external-controller-pipe|secret):\s*(.*)$");
                        if (!m.Success) continue;
                        string v = Scalar(m.Groups[2].Value);
                        if (m.Groups[1].Value == "secret") secret = v;
                        else if (m.Groups[1].Value == "external-controller-pipe") pipe = v;
                        else address = v;
                    }
                }
                if (!string.IsNullOrEmpty(pipe)) { try { result.Add(new Endpoint { Address = Endpoint.Normalize(pipe), Secret = "", Client = client }); } catch { } }
                if (!string.IsNullOrEmpty(address)) {
                    address = Regex.Replace(address, @"^(?:0\.0\.0\.0|\*):", "127.0.0.1:");
                    address = address.Replace("[::]:", "[::1]:");
                    try { result.Add(new Endpoint { Address = Endpoint.Normalize(address), Secret = secret, Client = client }); } catch { }
                }
            } catch { }
            return result;
        }
        public static List<Endpoint> Candidates(Settings settings) {
            var list = new List<Endpoint>();
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string client = settings.Source == "fatcat" ? "肥猫云 Lite" : "Clash Verge";
            if (!string.IsNullOrWhiteSpace(settings.Endpoint)) {
                list.Add(new Endpoint { Address = Endpoint.Normalize(settings.Endpoint), Secret = settings.Secret, Client = client });
                return list;
            }
            if (!string.IsNullOrWhiteSpace(settings.ConfigPath)) return FromFile(settings.ConfigPath, client);
            if (settings.Source == "fatcat") {
                list.AddRange(FromFile(Path.Combine(app, "fcclient", "fcclient", "config.yaml"), "肥猫云 Lite"));
                // The Flutter preferences are read only; no subscription/profile contents are used.
                try {
                    string prefs = Path.Combine(app, "fcclient", "fcclient", "shared_preferences.json");
                    var outer = Json.Parse(File.ReadAllText(prefs));
                    var inner = Json.Parse(Json.Text(outer, "flutter.config"));
                    var patch = Json.Obj(inner, "patchClashConfig");
                    string address = Json.Text(patch, "external-controller");
                    if (address.Length > 0) list.Add(new Endpoint { Address = Endpoint.Normalize(address), Secret = Json.Text(patch, "secret"), Client = "肥猫云 Lite" });
                } catch { }
            } else {
                // Service mode can use a different pipe from the saved sidecar configuration.
                try {
                    foreach (string pipePath in Directory.GetFiles(@"\\.\pipe\")) {
                        string name = pipePath.Substring(pipePath.LastIndexOf('\\') + 1);
                        if (Regex.IsMatch(name, @"^verge-mihomo-(?:production|sidecar-release)-[a-zA-Z0-9-]+$"))
                            list.Add(new Endpoint { Address = Endpoint.Normalize("pipe://" + name), Secret = "", Client = "Clash Verge" });
                    }
                } catch { }
                foreach (string folder in new[] { "io.github.clash-verge-rev.clash-verge-rev", "io.github.clash-verge.clash-verge" })
                    foreach (string name in new[] { "clash-verge.yaml", "config.yaml" }) list.AddRange(FromFile(Path.Combine(app, folder, name), "Clash Verge"));
                foreach (string folder in new[] { "clash", "mihomo" }) list.AddRange(FromFile(Path.Combine(user, ".config", folder, "config.yaml"), "Clash / Mihomo"));
                foreach (int port in new[] { 9097, 9090, 9095 }) list.Add(new Endpoint { Address = "http://127.0.0.1:" + port, Secret = "", Client = "Clash / Mihomo" });
            }
            return list.GroupBy(e => e.Address + "\n" + e.Secret).Select(g => g.First()).ToList();
        }
        public static bool Running(string name) { try { return Process.GetProcessesByName(name).Length > 0; } catch { return false; } }
        public static NetworkInterface NordAdapter() {
            try { return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && (n.Name + " " + n.Description).IndexOf("Nord", StringComparison.OrdinalIgnoreCase) >= 0 && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)); } catch { return null; }
        }
        public static string FatCatProxy() {
            if (!Running("fcclientCore")) return null;
            try {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")) {
                    if (Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 1) return null;
                    string server = Convert.ToString(key.GetValue("ProxyServer", ""));
                    if (server.Contains(";")) {
                        var m = Regex.Match(server, @"(?:^|;)https?=([^;]+)");
                        if (!m.Success) return null;
                        server = m.Groups[1].Value;
                    }
                    string address = Endpoint.Normalize(server);
                    var prefs = Json.Parse(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fcclient", "fcclient", "shared_preferences.json")));
                    var patch = Json.Obj(Json.Parse(Json.Text(prefs, "flutter.config")), "patchClashConfig");
                    var port = new Uri(address).Port;
                    if (Json.Number(patch, "mixed-port") == port || Json.Number(patch, "port") == port) return address;
                }
            } catch { }
            return null;
        }
        public static string FatCatTestUrl() {
            const string fallback = "https://cp.cloudflare.com/generate_204";
            try {
                var prefs = Json.Parse(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fcclient", "fcclient", "shared_preferences.json")));
                var app = Json.Obj(Json.Parse(Json.Text(prefs, "flutter.config")), "appSettingProps");
                Uri url;
                if (Uri.TryCreate(Json.Text(app,"testUrl"),UriKind.Absolute,out url) && (url.Scheme=="http"||url.Scheme=="https") && url.UserInfo.Length==0 && url.Query.Length==0 && url.Fragment.Length==0 && url.AbsolutePath=="/generate_204" && (url.Host=="cp.cloudflare.com"||url.Host=="www.gstatic.com") && url.IsDefaultPort) return url.AbsoluteUri;
            } catch { }
            return fallback;
        }
    }
    public sealed class ApiException : Exception {
        public int Status;
        public ApiException(int status) : base("HTTP " + status) { Status = status; }
    }
    public sealed class ApiResponse : IDisposable {
        public Stream Body;
        public Action Close;
        public Timer Timer;
        public void Arm(int milliseconds) { Timer.Change(milliseconds, Timeout.Infinite); }
        public void Dispose() { if (Timer != null) Timer.Dispose(); if (Close != null) { try { Close(); } catch { } } }
    }
    public sealed class ChunkedStream : Stream {
        readonly Stream inner;
        long remaining;
        bool end, afterChunk;
        public ChunkedStream(Stream source) { inner = source; }
        public static string Line(Stream stream, int maximum) {
            var bytes = new List<byte>();
            while (true) {
                int b = stream.ReadByte();
                if (b == -1) { if (bytes.Count == 0) return null; throw new EndOfStreamException(); }
                if (b == 10) return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add((byte)b);
                if (bytes.Count > maximum) throw new IOException("Line exceeds limit");
            }
        }
        public override int Read(byte[] buffer, int offset, int count) {
            if (count == 0 || end) return 0;
            if (remaining == 0) {
                if (afterChunk && Line(inner, 4) != "") throw new IOException("Invalid chunk terminator");
                string header = Line(inner, 128);
                long size;
                if (header == null || !long.TryParse(header.Split(';')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size) || size < 0 || size > 8 * 1024 * 1024) throw new IOException("Invalid chunk length");
                if (size == 0) {
                    for (int trailer = 0; ; trailer++) {
                        string line = Line(inner, 8192);
                        if (line == "") break;
                        if (line == null || trailer > 64) throw new IOException("Invalid HTTP trailers");
                    }
                    end = true; return 0;
                }
                remaining = size; afterChunk = true;
            }
            int read = inner.Read(buffer, offset, (int)Math.Min(count, remaining));
            if (read == 0) throw new EndOfStreamException();
            remaining -= read; return read;
        }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long a, SeekOrigin b) { throw new NotSupportedException(); }
        public override void SetLength(long a) { throw new NotSupportedException(); }
        public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
    }
    public static class Api {
        public static ApiResponse Open(Endpoint endpoint, string path, int timeout) {
            if (!path.StartsWith("/") || path.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("Invalid API path");
            if (!endpoint.IsPipe) {
                var request = (HttpWebRequest)WebRequest.Create(Endpoint.Normalize(endpoint.Address) + path);
                request.Proxy = null; request.Timeout = timeout; request.ReadWriteTimeout = timeout; request.AllowAutoRedirect = false; request.KeepAlive = false;
                if (!string.IsNullOrEmpty(endpoint.Secret)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + endpoint.Secret;
                try {
                    var response = (HttpWebResponse)request.GetResponse();
                    if ((int)response.StatusCode != 200) { response.Dispose(); throw new ApiException((int)response.StatusCode); }
                    var handle = new ApiResponse { Body = response.GetResponseStream(), Close = () => { request.Abort(); response.Dispose(); } };
                    handle.Timer = new Timer(s => { try { request.Abort(); } catch { } }, null, timeout, Timeout.Infinite);
                    return handle;
                } catch (WebException e) {
                    var response = e.Response as HttpWebResponse;
                    if (response != null) { int status = (int)response.StatusCode; response.Dispose(); throw new ApiException(status); }
                    throw;
                }
            }
            var pipe = new NamedPipeClientStream(".", endpoint.Address.Substring(7), PipeDirection.InOut, PipeOptions.Asynchronous);
            var result = new ApiResponse { Close = () => pipe.Dispose() };
            result.Timer = new Timer(s => { try { pipe.Dispose(); } catch { } }, null, timeout, Timeout.Infinite);
            try {
                pipe.Connect(Math.Min(timeout, 600));
                byte[] request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\nAccept: application/json\r\n\r\n");
                pipe.Write(request, 0, request.Length); pipe.Flush();
                string statusLine = ChunkedStream.Line(pipe, 1024);
                int status;
                if (statusLine == null || !int.TryParse(statusLine.Split(' ').ElementAtOrDefault(1), out status)) throw new IOException("Invalid HTTP response");
                if (status != 200) throw new ApiException(status);
                bool chunked = false;
                for (int i = 0; ; i++) {
                    string line = ChunkedStream.Line(pipe, 8192);
                    if (line == "") break;
                    if (line == null || i > 64) throw new IOException("Invalid HTTP headers");
                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) && line.ToLowerInvariant().Contains("chunked")) chunked = true;
                }
                result.Body = chunked ? (Stream)new ChunkedStream(pipe) : pipe;
                return result;
            } catch { result.Dispose(); throw; }
        }
        public static Dictionary<string, object> Get(Endpoint endpoint, string path, int timeout) {
            using (var response = Open(endpoint, path, timeout))
            using (var reader = new StreamReader(response.Body, Encoding.UTF8)) {
                var text = new StringBuilder(); var chars = new char[4096]; int count;
                while ((count = reader.Read(chars, 0, chars.Length)) > 0) { text.Append(chars, 0, count); if (text.Length > 8 * 1024 * 1024) throw new IOException("Response exceeds limit"); }
                return Json.Parse(text.ToString());
            }
        }
    }
    public sealed class NodeSelection {
        public string Name, Group, Explanation;
        public double? Delay;
        public string DelayNote = "无延时记录";
        public bool CanProbe;
        public List<string> Groups;
    }
    public static class Routing {
        public static NodeSelection Select(Dictionary<string, object> proxies, string preferred, string mode) {
            var groups = proxies.Where(p => p.Value is Dictionary<string, object> && ((Dictionary<string, object>)p.Value).ContainsKey("all")).Select(p => p.Key).ToList();
            var output = new NodeSelection { Groups = groups, Name = "未找到节点", Group = "", Explanation = "请选择关注的策略组" };
            if (mode.Equals("direct", StringComparison.OrdinalIgnoreCase)) { output.Name = "DIRECT · 直连"; output.Explanation = "客户端处于直连模式"; return output; }
            string selected = !string.IsNullOrEmpty(preferred) && proxies.ContainsKey(preferred) ? preferred : null;
            if (selected == null && mode.Equals("global", StringComparison.OrdinalIgnoreCase) && groups.Contains("GLOBAL")) selected = "GLOBAL";
            if (selected == null) selected = groups.FirstOrDefault(g => g != "GLOBAL" && Regex.IsMatch(g, "节点选择|代理选择|代理|PROXY|Proxy|手动|默认"));
            if (selected == null) selected = groups.FirstOrDefault(g => g != "GLOBAL" && Json.Text(Json.Obj(proxies, g), "type").Equals("Selector", StringComparison.OrdinalIgnoreCase));
            if (selected == null) selected = groups.FirstOrDefault(g => g != "GLOBAL") ?? groups.FirstOrDefault();
            if (selected == null) { selected = proxies.Keys.FirstOrDefault(g => g != "DIRECT" && g != "REJECT" && g != "COMPATIBLE"); }
            if (selected == null) return output;
            output.Group = groups.Contains(selected) ? selected : "";
            var seen = new HashSet<string>();
            while (true) {
                if (!seen.Add(selected)) { output.Name = "策略组循环引用"; output.Explanation = "无法确定实际节点"; return output; }
                object value;
                if (!proxies.TryGetValue(selected, out value)) { output.Name = selected; output.Explanation = "节点详情暂不可用"; return output; }
                var p = value as Dictionary<string, object>;
                if (p == null) return output;
                if (Json.Text(p, "type").Equals("LoadBalance", StringComparison.OrdinalIgnoreCase)) { output.Name = selected + " · 多节点"; output.Explanation = "负载均衡没有唯一出口节点"; return output; }
                string next = Json.Text(p, "now");
                if (next.Length > 0) { selected = next; continue; }
                output.Name = selected;
                output.Explanation = output.Group.Length > 0 ? "关注策略组 · " + output.Group : "代理节点";
                output.CanProbe = !new[] { "DIRECT", "REJECT", "PASS", "REJECT-DROP" }.Contains(Json.Text(p, "type").ToUpperInvariant());
                object history;
                if (p.TryGetValue("history", out history) && history is IEnumerable) {
                    var last = ((IEnumerable)history).Cast<object>().OfType<Dictionary<string, object>>().LastOrDefault();
                    if (last != null) {
                        double? delay = Json.Number(last, "delay");
                        DateTimeOffset time;
                        if (DateTimeOffset.TryParse(Json.Text(last, "time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time) && DateTimeOffset.UtcNow - time < TimeSpan.FromMinutes(5) && time <= DateTimeOffset.UtcNow.AddMinutes(1)) {
                            output.Delay = delay > 0 ? delay : null;
                            output.DelayNote = delay > 0 ? "客户端记录 · " + time.LocalDateTime.ToString("HH:mm:ss") : "最近一次探测失败";
                        } else output.DelayNote = "记录已过期，等待探测";
                    }
                }
                return output;
            }
        }
    }
    public static class Units {
        public static string[] Rate(double? value) {
            if (!value.HasValue) return new[] { "—", "B/s" };
            double v = Math.Max(0, value.Value); string[] units = { "B/s", "KB/s", "MB/s", "GB/s" }; int i = 0;
            while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
            return new[] { v.ToString(v < 10 && i > 0 ? "0.00" : v < 100 && i > 0 ? "0.0" : "0", CultureInfo.InvariantCulture), units[i] };
        }
    }
}
