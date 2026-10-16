// Shelfnote Feedback: a tiny local web server for the retail feedback kiosk.
// Serves the embedded page, stores responses as JSON in Documents\Shelfnote Feedback,
// and opens the app in an Edge app window. Tablets on the same network can open the kiosk too.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

static class Shelfnote
{
    const int BasePort = 8787;
    const int MaxBody = 64 * 1024;

    static readonly object Gate = new object();
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static string dataDir, responsesFile, settingsFile;
    static List<Dictionary<string, object>> responses;
    static Dictionary<string, object> settings;
    static byte[] page;
    static int port;

    static int Main(string[] args)
    {
        Console.Title = "Shelfnote Feedback";
        bool noBrowser = args.Contains("--no-browser");

        // If Shelfnote is already running, just open it again.
        if (IsRunning(BasePort))
        {
            Console.WriteLine("Shelfnote Feedback is already running. Opening it...");
            OpenApp("http://localhost:" + BasePort + "/#dash");
            Thread.Sleep(1500);
            return 0;
        }

        dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Shelfnote Feedback");
        Directory.CreateDirectory(dataDir);
        responsesFile = Path.Combine(dataDir, "responses.json");
        settingsFile = Path.Combine(dataDir, "settings.json");
        responses = Load<List<Dictionary<string, object>>>(responsesFile) ?? new List<Dictionary<string, object>>();
        settings = Load<Dictionary<string, object>>(settingsFile);

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html"))
        using (var ms = new MemoryStream()) { s.CopyTo(ms); page = ms.ToArray(); }

        TcpListener listener = null;
        for (port = BasePort; port < BasePort + 20; port++)
        {
            try { listener = new TcpListener(IPAddress.Any, port); listener.Start(); break; }
            catch (SocketException) { listener = null; }
        }
        if (listener == null)
        {
            Console.WriteLine("Couldn't find a free network port between {0} and {1}. Close other programs and try again.", BasePort, BasePort + 19);
            Console.ReadKey(true);
            return 1;
        }

        string local = "http://localhost:" + port + "/";
        Console.WriteLine();
        Console.WriteLine("  SHELFNOTE FEEDBACK is running");
        Console.WriteLine("  -----------------------------");
        Console.WriteLine("  On this PC:        " + local);
        foreach (var u in LanUrls()) Console.WriteLine("  Store tablets:     " + u + "#kiosk");
        Console.WriteLine("  Responses saved in " + dataDir);
        Console.WriteLine();
        Console.WriteLine("  Keep this window open while the store is collecting feedback.");
        Console.WriteLine("  Close it to stop Shelfnote.");
        Console.WriteLine();

        if (!noBrowser) OpenApp(local + "#dash");

        while (true)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { continue; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    // ---------- HTTP ----------
    static void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                var stream = client.GetStream();
                string method, path; Dictionary<string, string> headers; byte[] body;
                if (!ReadRequest(stream, out method, out path, out headers, out body)) return;
                Route(stream, method, path, body);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  ! " + ex.Message);
            }
        }
    }

    static bool ReadRequest(NetworkStream s, out string method, out string path, out Dictionary<string, string> headers, out byte[] body)
    {
        method = path = null; headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); body = new byte[0];
        var buf = new List<byte>();
        int b;
        while ((b = s.ReadByte()) != -1)
        {
            buf.Add((byte)b);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == 13 && buf[n - 3] == 10 && buf[n - 2] == 13 && buf[n - 1] == 10) break;
            if (n > 16384) return false;
        }
        if (b == -1) return false;
        var lines = Encoding.ASCII.GetString(buf.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length < 2) return false;
        method = first[0].ToUpperInvariant();
        path = first[1];
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
        }
        string cl;
        if (headers.TryGetValue("Content-Length", out cl))
        {
            int len;
            if (!int.TryParse(cl, out len) || len < 0 || len > MaxBody) { Send(s, 413, "text/plain", Encoding.UTF8.GetBytes("Too large")); return false; }
            body = new byte[len];
            int read = 0;
            while (read < len) { int r = s.Read(body, read, len - read); if (r <= 0) return false; read += r; }
        }
        return true;
    }

    static void Send(Stream s, int code, string type, byte[] data)
    {
        string reason = code == 200 ? "OK" : code == 204 ? "No Content" : code == 400 ? "Bad Request" : code == 404 ? "Not Found" : code == 413 ? "Payload Too Large" : "Error";
        var head = "HTTP/1.1 " + code + " " + reason + "\r\n" +
                   "Content-Type: " + type + "\r\n" +
                   "Content-Length: " + data.Length + "\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Connection: close\r\n\r\n";
        var hb = Encoding.ASCII.GetBytes(head);
        s.Write(hb, 0, hb.Length);
        s.Write(data, 0, data.Length);
        s.Flush();
    }

    static void SendJson(Stream s, object o) { Send(s, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(Json.Serialize(o))); }

    static void Route(Stream s, string method, string rawPath, byte[] body)
    {
        string path = rawPath.Split('?')[0];

        if (method == "GET" && (path == "/" || path == "/index.html")) { Send(s, 200, "text/html; charset=utf-8", page); return; }
        if (method == "GET" && path == "/favicon.ico") { Send(s, 204, "text/plain", new byte[0]); return; }

        if (method == "GET" && path == "/api/info")
        {
            SendJson(s, new Dictionary<string, object> { { "urls", LanUrls() }, { "dataDir", dataDir } });
            return;
        }

        if (path == "/api/settings")
        {
            if (method == "GET") { lock (Gate) SendJson(s, (object)settings ?? new Dictionary<string, object>()); return; }
            if (method == "PUT")
            {
                var incoming = Parse(body);
                if (incoming == null) { Send(s, 400, "text/plain", Encoding.UTF8.GetBytes("Bad JSON")); return; }
                var stores = (incoming.ContainsKey("stores") ? incoming["stores"] as System.Collections.ArrayList : null);
                var clean = new Dictionary<string, object>
                {
                    { "brand", Clip(Str(incoming, "brand"), 40) },
                    { "stores", stores == null ? new List<string>() : stores.Cast<object>().Select(x => Clip(Convert.ToString(x), 40)).Where(x => x.Length > 0).Distinct().Take(200).ToList() }
                };
                lock (Gate) { settings = clean; Save(settingsFile, settings); }
                SendJson(s, clean);
                return;
            }
        }

        if (path == "/api/responses")
        {
            if (method == "GET") { lock (Gate) SendJson(s, responses); return; }
            if (method == "POST")
            {
                var r = Parse(body);
                if (r == null) { Send(s, 400, "text/plain", Encoding.UTF8.GetBytes("Bad JSON")); return; }
                int rating = ToInt(r, "rating", 0);
                if (rating < 1 || rating > 5) { Send(s, 400, "text/plain", Encoding.UTF8.GetBytes("Rating must be 1-5")); return; }
                object nps = null;
                if (r.ContainsKey("nps") && r["nps"] != null) { int n = ToInt(r, "nps", -1); if (n >= 0 && n <= 10) nps = n; }
                var topics = r.ContainsKey("topics") ? r["topics"] as System.Collections.ArrayList : null;
                string status = Str(r, "status");
                var doc = new Dictionary<string, object>
                {
                    { "id", Guid.NewGuid().ToString("N").Substring(0, 16) },
                    { "store", Clip(Str(r, "store"), 40) },
                    { "rating", rating },
                    { "nps", nps },
                    { "topics", topics == null ? new List<string>() : topics.Cast<object>().Select(x => Clip(Convert.ToString(x), 30)).Take(20).ToList() },
                    { "comment", Clip(Str(r, "comment"), 1000) },
                    { "receipt", Clip(Str(r, "receipt"), 40) },
                    { "contact", Clip(Str(r, "contact"), 120) },
                    { "createdAt", (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds },
                    { "status", ValidStatus(status) ? status : (rating <= 2 ? "new" : "none") }
                };
                lock (Gate) { responses.Add(doc); Save(responsesFile, responses); }
                Console.WriteLine("  + {0:HH:mm}  {1}  rated {2}/5", DateTime.Now, doc["store"], rating);
                SendJson(s, doc);
                return;
            }
        }

        if (method == "PATCH" && path.StartsWith("/api/responses/"))
        {
            string id = Uri.UnescapeDataString(path.Substring("/api/responses/".Length));
            var r = Parse(body);
            string status = r == null ? null : Str(r, "status");
            if (!ValidStatus(status)) { Send(s, 400, "text/plain", Encoding.UTF8.GetBytes("Bad status")); return; }
            lock (Gate)
            {
                var doc = responses.FirstOrDefault(x => Convert.ToString(x["id"]) == id);
                if (doc == null) { Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found")); return; }
                doc["status"] = status;
                Save(responsesFile, responses);
                SendJson(s, doc);
            }
            return;
        }

        Send(s, 404, "text/plain", Encoding.UTF8.GetBytes("Not found"));
    }

    // ---------- helpers ----------
    static bool ValidStatus(string s) { return s == "none" || s == "new" || s == "follow" || s == "resolved"; }
    static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }
    static string Clip(string s, int n) { s = (s ?? "").Trim(); return s.Length > n ? s.Substring(0, n) : s; }
    static int ToInt(Dictionary<string, object> d, string k, int def)
    {
        object v; if (!d.TryGetValue(k, out v) || v == null) return def;
        try { return Convert.ToInt32(v); } catch { return def; }
    }

    static Dictionary<string, object> Parse(byte[] body)
    {
        try { return Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body)); }
        catch { return null; }
    }

    static T Load<T>(string file) where T : class
    {
        try { return File.Exists(file) ? Json.Deserialize<T>(File.ReadAllText(file, Encoding.UTF8)) : null; }
        catch (Exception ex)
        {
            // Keep a copy of an unreadable file instead of overwriting it.
            string bak = file + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { File.Copy(file, bak); } catch { }
            Console.WriteLine("  ! Couldn't read {0} ({1}). A copy was kept as {2}.", Path.GetFileName(file), ex.Message, Path.GetFileName(bak));
            return null;
        }
    }

    static void Save(string file, object data)
    {
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, Json.Serialize(data), new UTF8Encoding(false));
        if (File.Exists(file)) File.Replace(tmp, file, null);
        else File.Move(tmp, file);
    }

    static List<string> LanUrls()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = a.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;
                    list.Add("http://" + ip + ":" + port + "/");
                }
            }
        }
        catch { }
        return list.Distinct().ToList();
    }

    static bool IsRunning(int p)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create("http://localhost:" + p + "/api/info");
            req.Timeout = 1500;
            using (var res = (HttpWebResponse)req.GetResponse())
            using (var rd = new StreamReader(res.GetResponseStream()))
                return rd.ReadToEnd().Contains("dataDir");
        }
        catch { return false; }
    }

    static void OpenApp(string url)
    {
        // Prefer a clean Edge app window; fall back to the default browser.
        try { Process.Start(new ProcessStartInfo("msedge", "--app=" + url) { UseShellExecute = true }); return; }
        catch { }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { Console.WriteLine("  Open " + url + " in your browser."); }
    }
}
