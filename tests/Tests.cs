using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NodeLens;

class Tests {
    static int passed, failed;
    static readonly List<string> results = new List<string>();
    static void Test(string name, Action test) { try { test(); passed++;results.Add("PASS "+name); } catch(Exception e) {failed++;results.Add("FAIL "+name+": "+e.Message);} }
    static void Equal(object a,object b) { if(!object.Equals(a,b))throw new Exception("Expected "+b+", got "+a); }
    static void Assert(bool condition) { if(!condition)throw new Exception("Assertion failed"); }
    static void Throws(Action action) { bool threw=false;try{action();}catch{threw=true;}Assert(threw); }
    static Dictionary<string,object> Proxies() {return Json.Parse(@"{""GLOBAL"":{""type"":""Selector"",""now"":""DIRECT"",""all"":[]},""节点选择"":{""type"":""Selector"",""now"":""自动选择"",""all"":[]},""自动选择"":{""type"":""URLTest"",""now"":""HK-09"",""all"":[]},""HK-09"":{""type"":""Trojan"",""history"":[]},""DIRECT"":{""type"":""Direct""}}");}
    static string ReadHeaders(Stream stream) {var b=new StringBuilder();for(int i=0;i<80;i++){var line=ChunkedStream.Line(stream,8192);if(line==null||line=="")return b.ToString();b.AppendLine(line);}throw new IOException();}
    static string Http(string body,bool chunked) {return "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n"+(chunked?"Transfer-Encoding: chunked":"Content-Length: "+Encoding.UTF8.GetByteCount(body))+"\r\nConnection: close\r\n\r\n"+(chunked?Encoding.UTF8.GetByteCount(body).ToString("x")+"\r\n"+body+"\r\n0\r\n\r\n":body);}
    static void Write(Stream stream,string value){byte[] b=Encoding.UTF8.GetBytes(value);stream.Write(b,0,b.Length);stream.Flush();}
    static int Main(string[] args) {
        Test("Nested groups resolve to leaf node",()=>{var s=Routing.Select(Proxies(),"","rule");Equal(s.Name,"HK-09");Equal(s.Group,"节点选择");Assert(s.CanProbe);});
        Test("Named provider selector precedes fallback group",()=>{var p=Json.Parse("{\"故障转移\":{\"type\":\"Fallback\",\"all\":[],\"now\":\"剩余流量\"},\"良心云\":{\"type\":\"Selector\",\"all\":[],\"now\":\"SG01\"},\"SG01\":{\"type\":\"Trojan\"}}");Equal(Routing.Select(p,"","rule").Name,"SG01");});
        Test("Global mode follows GLOBAL",()=>Equal(Routing.Select(Proxies(),"","global").Name,"DIRECT"));
        Test("Direct mode does not claim a VPN node",()=>{var s=Routing.Select(Proxies(),"节点选择","direct");Equal(s.Name,"DIRECT · 直连");Assert(!s.CanProbe);});
        Test("Explicit watched group preserved",()=>Equal(Routing.Select(Proxies(),"GLOBAL","rule").Name,"DIRECT"));
        Test("Cyclic groups stop safely",()=>{var p=Proxies();Json.Obj(p,"自动选择")["now"]="节点选择";Assert(!Routing.Select(p,"","rule").CanProbe);});
        Test("Load balancing never invents a single node",()=>{var p=Proxies();Json.Obj(p,"自动选择")["type"]="LoadBalance";Assert(Routing.Select(p,"","rule").Name.Contains("多节点"));});
        Test("Unknown leaf has no fabricated latency",()=>{var p=Proxies();Json.Obj(p,"自动选择")["now"]="Missing";Assert(!Routing.Select(p,"","rule").Delay.HasValue);});
        Test("Stale latency history discarded",()=>{var p=Proxies();Json.Obj(p,"HK-09")["history"]=new[]{new Dictionary<string,object>{{"time",DateTime.UtcNow.AddHours(-2).ToString("o")},{"delay",32}}};Assert(!Routing.Select(p,"","rule").Delay.HasValue);});
        Test("Recent failed delay is unknown, not 0 ms",()=>{var p=Proxies();Json.Obj(p,"HK-09")["history"]=new[]{new Dictionary<string,object>{{"time",DateTime.UtcNow.ToString("o")},{"delay",0}}};Assert(!Routing.Select(p,"","rule").Delay.HasValue);});
        Test("Recent successful latency retained",()=>{var p=Proxies();Json.Obj(p,"HK-09")["history"]=new[]{new Dictionary<string,object>{{"time",DateTime.UtcNow.ToString("o")},{"delay",32}}};Equal(Routing.Select(p,"","rule").Delay,(double?)32);});
        Test("Loopback addresses accepted",()=>{Equal(Endpoint.Normalize("127.0.0.1:9090"),"http://127.0.0.1:9090");Assert(IPAddress.Parse(new Uri(Endpoint.Normalize("http://[::1]:9090")).Host.Trim('[',']')).Equals(IPAddress.IPv6Loopback));});
        Test("External credential destinations rejected",()=>{Throws(()=>Endpoint.Normalize("https://example.com"));Throws(()=>Endpoint.Normalize("http://127.0.0.1@example.com"));Throws(()=>Endpoint.Normalize("http://127.0.0.1:9090/path"));Throws(()=>Endpoint.Normalize("file:///C:/secret"));});
        Test("Local Windows pipes normalized",()=>{Equal(Endpoint.Normalize(@"\\.\pipe\verge-test"),"pipe://verge-test");Equal(Endpoint.Normalize(@"\\\\.\\pipe\\verge-test"),"pipe://verge-test");Throws(()=>Endpoint.Normalize("pipe://../name"));});
        Test("YAML secret comments and quotes parsed",()=>{Equal(Discovery.Scalar("'a#b''c' # comment"),"a#b'c");Equal(Discovery.Scalar("abc # comment"),"abc");Equal(Discovery.Scalar("\"a\\\"b\" # comment"),"a\"b");});
        Test("DPAPI protects persisted secret",()=>{var s=new Settings();s.Secret="test-value";Assert(!s.EncryptedSecret.Contains("test-value"));Equal(s.Secret,"test-value");Assert(!Json.Serializer().Serialize(s).Contains("test-value"));});
        Test("Invalid numbers are not traffic",()=>{Assert(!Json.Number(Json.Parse("{\"up\":-1}"),"up").HasValue);Assert(!Json.Number(Json.Parse("{\"up\":\"NaN\"}"),"up").HasValue);});
        Test("Zero speed differs from missing data",()=>{Equal(Units.Rate(0)[0],"0");Equal(Units.Rate(null)[0],"—");Equal(Units.Rate(1250000)[0],"1.25");});
        Test("Chunked UTF8 stream handles fragmented Unicode",()=>{string body="{\"node\":\"香港\"}\n";byte[] b=Encoding.UTF8.GetBytes(body);var raw=new MemoryStream();Write(raw,"5;extension=value\r\n");raw.Write(b,0,5);Write(raw,"\r\n"+(b.Length-5).ToString("x")+"\r\n");raw.Write(b,5,b.Length-5);Write(raw,"\r\n0\r\n\r\n");raw.Position=0;using(var reader=new StreamReader(new ChunkedStream(raw)))Equal(reader.ReadToEnd(),body);});
        Test("Malformed chunks rejected",()=>{var stream=new ChunkedStream(new MemoryStream(Encoding.ASCII.GetBytes("bad_size\r\n")));Throws(()=>stream.ReadByte());});
        Test("Oversized stream records rejected",()=>Throws(()=>ChunkedStream.Line(new MemoryStream(Encoding.ASCII.GetBytes(new string('a',100))),16)));
        Test("Active Verge service pipe discovered without saved configuration",()=>{string name="verge-mihomo-production-"+Guid.NewGuid().ToString("N");using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut)){Assert(Discovery.Candidates(new Settings{Source="clash"}).Any(e=>e.Address=="pipe://"+name));}});
        Test("Named pipe HTTP body and UTF8 parsed",()=>PipeTest(false));
        Test("Named pipe chunked response parsed",()=>PipeTest(true));
        Test("HTTP API sends bearer authentication locally",()=>{
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;string headers="";
            var server=Task.Run(()=>{using(var tcp=listener.AcceptTcpClient())using(var stream=tcp.GetStream()){headers=ReadHeaders(stream);Write(stream,Http("{\"version\":\"test\"}",false));}});
            try{var result=Api.Get(new Endpoint{Address="http://127.0.0.1:"+port,Secret="test-token"},"/version",2000);Equal(Json.Text(result,"version"),"test");Assert(server.Wait(3000));Assert(headers.Contains("Authorization: Bearer test-token"));Assert(headers.StartsWith("GET /version "));}finally{listener.Stop();}
        });
        Test("Authentication failures surfaced without response leakage",()=>{
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
            var server=Task.Run(()=>{using(var tcp=listener.AcceptTcpClient())using(var stream=tcp.GetStream()){ReadHeaders(stream);Write(stream,"HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");}});
            try{bool unauthorized=false;try{Api.Get(new Endpoint{Address="http://127.0.0.1:"+port},"/proxies",1000);}catch(ApiException e){unauthorized=e.Status==401;}Assert(unauthorized);server.Wait(2000);}finally{listener.Stop();}
        });
        Test("Clash background traffic clears on disconnect",()=>MonitorIntegration("clash"));
        Test("FatCat background traffic clears on disconnect without capturing windows",()=>MonitorIntegration("fatcat"));
        Test("Production assembly has no window capture or OCR code",()=>Assert(typeof(NodeLens.Monitor).Assembly.GetTypes().All(t=>!t.Name.Contains("Ocr")&&!t.Name.Contains("Capture"))));
        if(args.Length>0)File.WriteAllText(args[0],string.Join(Environment.NewLine,results)+Environment.NewLine+"Passed: "+passed+"; Failed: "+failed);
        Console.WriteLine("Passed: "+passed+"; Failed: "+failed);foreach(var r in results.Where(r=>r.StartsWith("FAIL")))Console.WriteLine(r);return failed==0?0:1;
    }
    static void PipeTest(bool chunked){
        string name="nodelens-test-"+Guid.NewGuid().ToString("N");
        using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)){
            var server=Task.Run(()=>{pipe.WaitForConnection();string request=ReadHeaders(pipe);Assert(request.StartsWith("GET /proxies "));Write(pipe,Http("{\"node\":\"香港\"}",chunked));pipe.WaitForPipeDrain();pipe.Disconnect();});
            var data=Api.Get(new Endpoint{Address="pipe://"+name},"/proxies",2500);Equal(Json.Text(data,"node"),"香港");Assert(server.Wait(3000));
        }
    }
    static void MonitorIntegration(string source){
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
        bool serve=true;var clients=new List<TcpClient>();var events=new List<Snapshot>();
        var server=Task.Run(()=>{while(serve){try{var tcp=listener.AcceptTcpClient();lock(clients)clients.Add(tcp);Task.Run(()=>{try{using(tcp)using(var stream=tcp.GetStream()){string path=ReadHeaders(stream).Split(' ')[1];if(path=="/proxies")Write(stream,Http("{\"proxies\":"+Json.Serializer().Serialize(Proxies())+"}",false));else if(path=="/configs")Write(stream,Http("{\"mode\":\"rule\"}",false));else if(path=="/traffic"){Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n");for(int i=0;i<40&&serve;i++){Write(stream,"{\"up\":1234,\"down\":5678}\n");Thread.Sleep(200);}}}}catch{}});}catch{break;}}});
        var monitor=new NodeLens.Monitor(new Settings{Source=source,Endpoint="http://127.0.0.1:"+port,ProbeEnabled=false},s=>{lock(events)events.Add(s);},false);
        try{
            monitor.Start();var clock=Stopwatch.StartNew();bool received=false;
            while(clock.ElapsedMilliseconds<7000){lock(events)received=events.Any(s=>s.Download==5678&&s.Upload==1234&&s.Node=="HK-09");if(received)break;Thread.Sleep(50);}Assert(received);
            serve=false;listener.Stop();lock(clients)foreach(var tcp in clients)tcp.Close();DateTime stopped=DateTime.UtcNow;
            clock.Restart();bool cleared=false;while(clock.ElapsedMilliseconds<7000){lock(events)cleared=events.Any(s=>s.Updated>=stopped&&!s.Download.HasValue&&!s.Connected);if(cleared)break;Thread.Sleep(50);}Assert(cleared);
        }finally{monitor.Dispose();serve=false;listener.Stop();lock(clients)foreach(var tcp in clients)tcp.Close();}
    }
}
