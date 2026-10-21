using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class ServerTests
{
    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        Assert(args.Length == 1 && File.Exists(args[0]), "TLS test certificate is available");
        TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string logDirectory = Path.Combine(Path.GetTempPath(), "kst-mock-" + Guid.NewGuid().ToString("N"));
        KstMesServer server = new KstMesServer(logDirectory);
        KstExchange last = null;
        int exchangeCount = 0;
        using (ManualResetEvent received = new ManualResetEvent(false))
        using (ManualResetEvent diagnosticReceived = new ManualResetEvent(false))
        {
            server.ExchangeReceived += delegate(KstExchange item)
            {
                last = item;
                Interlocked.Increment(ref exchangeCount);
                if (item.Method == "Mode") received.Set();
                diagnosticReceived.Set();
            };
            try
            {
                server.Start("127.0.0.1", port, Templates());
                foreach (string method in new[] { "Status", "OEE", "Alarm", "ExposureResult", "HeartBeat", "Mode" })
                {
                    string body = method == "ExposureResult" ? "{\"CMACHINE_CODE\":\"M-1\",\"CRESULT\":\"OK\"}" : "{\"MachineNO\":\"M-1\"}";
                    Dictionary<string, object> response = Json(Post(port, method == "HeartBeat" ? "/Heart" : "/" + method, body));
                    Assert((bool)response["success"] && (string)response["method"] == method, "route " + method);
                }
                Assert(received.WaitOne(3000), "request event emitted");
                Assert(last.Method == "Mode" && last.RequestBody.Contains("M-1") && last.Status == 200, "exchange details");
                Assert(Status(port, "GET", "/Status", null) == 405, "non POST rejected");
                Assert(Status(port, "POST", "/unknown", "{}") == 404, "unknown path rejected");
                Assert(Status(port, "POST", "/HeartBeat", "{}") == 404, "old heartbeat path rejected");
                int acceptedLogCount = 0;
                Action<KstExchange> countLogAccepted = delegate(KstExchange item)
                {
                    if (item.Method == "Log") Interlocked.Increment(ref acceptedLogCount);
                };
                server.RequestAccepted += countLogAccepted;
                string jsonLog = "{\"MachineNO\":\"KST-001\",\"message\":\"设备已启动\"}";
                diagnosticReceived.Reset();
                Assert((bool)Json(Post(port, "/Log", jsonLog))["success"], "JSON /Log uses default success response without a Log template");
                Assert(diagnosticReceived.WaitOne(3000), "JSON /Log exchange emitted");
                Dictionary<string, object> jsonLogRecord = LastLog(logDirectory);
                Assert((string)jsonLogRecord["method"] == "Log" && (int)jsonLogRecord["status"] == 200,
                    "JSON /Log is recorded as a successful log request");
                Assert((string)jsonLogRecord["request"] == jsonLog && ((string)jsonLogRecord["raw_request_text"]).Contains("设备已启动"),
                    "JSON /Log preserves decoded and raw request body");
                Assert((string)jsonLogRecord["machine_no"] == "KST-001", "JSON /Log retains the originating device code");

                string plainLog = "科视代理启动完成";
                diagnosticReceived.Reset();
                Assert((bool)Json(Post(port, "/Log", plainLog, "text/plain; charset=utf-8"))["success"],
                    "plain-text /Log succeeds without JSON parsing");
                Assert(diagnosticReceived.WaitOne(3000), "plain-text /Log exchange emitted");
                Dictionary<string, object> plainLogRecord = LastLog(logDirectory);
                Assert((string)plainLogRecord["request"] == plainLog && (int)plainLogRecord["status"] == 200,
                    "plain-text /Log body is retained in daily log");
                Assert(Thread.VolatileRead(ref acceptedLogCount) == 0, "/Log does not enter S1 MQTT forwarding");

                KstExchange forwardedResult = null;
                int forwardedResultCount = 0;
                Action<KstExchange> captureResult = delegate(KstExchange item)
                {
                    if (item.Method == "ExposureResult")
                    {
                        forwardedResult = item;
                        Interlocked.Increment(ref forwardedResultCount);
                    }
                };
                server.RequestAccepted += captureResult;
                string resultLog = "{\"function\":\"result\",\"assets\":\"KST-001\",\"CMACHINE_CODE\":\"KST-002\",\"CRESULT\":\"OK\",\"CITEM_NO\":\"board-42\",\"MachineTime\":\"2026-10-20 14:31:00\"}";
                diagnosticReceived.Reset();
                Assert((string)Json(Post(port, "/Log", resultLog))["method"] == "Log",
                    "result /Log retains the proxy response");
                Assert(diagnosticReceived.WaitOne(3000), "result /Log exchange emitted");
                Assert(Thread.VolatileRead(ref forwardedResultCount) == 1 && forwardedResult != null
                    && forwardedResult.Method == "ExposureResult" && forwardedResult.DeviceCode == "KST-002"
                    && forwardedResult.RequestBody == resultLog,
                    "result /Log enters exposure forwarding with its original body and device");
                Dictionary<string, object> resultLogRecord = LastLog(logDirectory);
                Assert((string)resultLogRecord["method"] == "Log" && (string)resultLogRecord["request"] == resultLog,
                    "result /Log remains an original Log entry in the daily log");
                Assert(Thread.VolatileRead(ref acceptedLogCount) == 0,
                    "result forwarding does not relabel the original Log exchange");
                server.RequestAccepted -= captureResult;
                Assert(Status(port, "POST", "/Log",
                    "{\"function\":\"result\",\"MachineNO\":\"KST-001\",\"CMACHINE_CODE\":\"KST-002\",\"CRESULT\":\"OK\"}") == 400,
                    "conflicting explicit machine codes in a result Log are rejected without source routes");
                Assert(Status(port, "POST", "/Status", "bad") == 400, "invalid JSON rejected");
                Assert(Status(port, "POST", "/Status", "[]") == 400, "non-object JSON rejected");

                KstExchange acceptedExchange = null;
                Action<KstExchange> captureAccepted = delegate(KstExchange item) { acceptedExchange = item; };
                server.RequestAccepted += captureAccepted;
                Assert(Status(port, "POST", "/Status", "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}") == 200, "actual assets field accepted");
                server.RequestAccepted -= captureAccepted;
                Assert(acceptedExchange != null && acceptedExchange.DeviceCode == "KST-001", "assets is the actual device identifier");

                server.RequestAccepted += captureAccepted;
                Assert(Status(port, "POST", "/Status", "{\"MachineNO\":\"\",\"assets\":\"KST-002\",\"Status\":\"WORK\"}") == 200,
                    "empty MachineNO falls back to assets");
                server.RequestAccepted -= captureAccepted;
                Assert(acceptedExchange != null && acceptedExchange.DeviceCode == "KST-002", "second device number read from assets");

                Action<KstExchange> rejectBeforeResponse = delegate { throw new IOException("queue unavailable"); };
                server.RequestAccepted += rejectBeforeResponse;
                Assert(Status(port, "POST", "/Status", "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}") == 500,
                    "durable forwarding failure is returned before device receives success");
                server.RequestAccepted -= rejectBeforeResponse;

                Dictionary<string, string> updated = Templates();
                updated["Status"] = "{\"ok\":7}";
                updated["Log"] = "{\"success\":true,\"message\":\"log saved\",\"method\":\"Log\"}";
                server.UpdateResponses(updated);
                Assert((int)Json(Post(port, "/Status", "{}"))["ok"] == 7, "edited response applies without restart");
                Assert((string)Json(Post(port, "/Log", "updated response"))["message"] == "log saved",
                    "/Log uses a configured response when available");
                Assert(Thread.VolatileRead(ref acceptedLogCount) == 0,
                    "configured /Log response still does not enter S1 MQTT forwarding");
                server.RequestAccepted -= countLogAccepted;
                WaitForExchangeCount(ref exchangeCount, 18);

                string chineseBody = "{\"MachineNO\":\"M-1\",\"Message\":\"中文\"}";
                diagnosticReceived.Reset();
                RawRequest(port, "/Status", Encoding.UTF8.GetBytes(chineseBody), Encoding.UTF8.GetByteCount(chineseBody));
                Assert(diagnosticReceived.WaitOne(3000), "UTF-8 request event emitted");
                Dictionary<string, object> utf8Record = LastLog(logDirectory);
                Assert((int)utf8Record["raw_received_bytes"] > Encoding.UTF8.GetByteCount(chineseBody), "raw byte count includes HTTP headers");
                Assert(((string)utf8Record["raw_request_text"]).Contains("中文"), "raw UTF-8 request text retained");
                Assert(!string.IsNullOrEmpty((string)utf8Record["raw_request_hex"]), "raw request hex retained");
                Assert((bool)utf8Record["raw_request_truncated"] == false, "small raw request is not truncated");

                byte[] incompleteBody = Encoding.UTF8.GetBytes("{\"MachineNO\":\"M-1\"}");
                diagnosticReceived.Reset();
                RawRequest(port, "/Heart", incompleteBody, incompleteBody.Length + 5);
                Assert(diagnosticReceived.WaitOne(3000), "incomplete request event emitted");
                Dictionary<string, object> incompleteRecord = LastLog(logDirectory);
                Assert((int)incompleteRecord["status"] == 400, "incomplete body rejected");
                Assert(((string)incompleteRecord["raw_request_text"]).Contains("POST /Heart HTTP/1.1"), "failed request line retained");
                Assert(((string)incompleteRecord["raw_request_text"]).Contains("M-1"), "failed request body retained");
                Assert(((string)incompleteRecord["diagnostic"]).Contains("Incomplete HTTP request body"), "failure reason retained");

                diagnosticReceived.Reset();
                RawRequest(port, "/Status", new byte[] { 0xC3, 0x28 }, 2);
                Assert(diagnosticReceived.WaitOne(3000), "invalid UTF-8 request event emitted");
                Dictionary<string, object> invalidUtf8Record = LastLog(logDirectory);
                Assert((int)invalidUtf8Record["status"] == 500, "invalid UTF-8 remains a server error");
                Assert(((string)invalidUtf8Record["raw_request_hex"]).EndsWith("C328"), "invalid UTF-8 bytes retained exactly");
                Assert(((string)invalidUtf8Record["diagnostic"]).Contains("DecoderFallbackException"), "UTF-8 decoder failure retained");
                string displayedDiagnostic = KstDiagnosticFormatter.RequestDetails(last);
                Assert(displayedDiagnostic.Contains("DecoderFallbackException"), "request details show the decoder failure");
                Assert(displayedDiagnostic.Contains("C328"), "request details show exact invalid bytes");
                Assert(displayedDiagnostic.Contains(last.RawReceivedBytes.ToString()), "request details show received byte count");
            }
            finally { server.Stop(); }
        }
        Assert(Directory.GetFiles(logDirectory, "*.jsonl").Length == 1, "daily log written");
        Directory.Delete(logDirectory, true);
        VerifyTls(args[0]);
        VerifyTrustedProxySourceRouting();
        Console.WriteLine("KST routes, TLS, response update, errors, and logging passed.");
        return 0;
    }

    private static void VerifyTls(string certificatePath)
    {
        TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string logDirectory = Path.Combine(Path.GetTempPath(), "kst-tls-mock-" + Guid.NewGuid().ToString("N"));
        KstMesServer server = new KstMesServer(logDirectory);
        KstExchange last = null;
        using (ManualResetEvent received = new ManualResetEvent(false))
        using (X509Certificate2 certificate = new X509Certificate2(certificatePath, "kst-mes-mock"))
        {
            server.ExchangeReceived += delegate(KstExchange item) { last = item; received.Set(); };
            try
            {
                server.Start("127.0.0.1", port, Templates(), new Dictionary<string, Dictionary<string, string>>(), certificate);
                string body = "{\"MachineNO\":\"TLS-1\",\"Message\":\"中文\"}";
                Dictionary<string, object> response = Json(TlsPost(port, "/Status", body));
                Assert((bool)response["success"], "TLS request receives configured response");
                Assert(received.WaitOne(3000), "TLS exchange event emitted");
                Assert(last.Status == 200 && last.Method == "Status" && last.RequestBody.Contains("中文"), "TLS request is decrypted before HTTP parsing");
                Assert(last.Transport == "TLS", "TLS transport is identified");
                Assert(last.DecodedRequestText.Contains("POST /Status HTTP/1.1") && last.DecodedRequestText.Contains("中文"), "decrypted HTTP request is retained");
                Assert(last.RawReceivedBytes > last.DecodedReceivedBytes, "encrypted transport bytes are retained separately");
                received.Reset();
                Dictionary<string, object> logResponse = Json(TlsPost(port, "/Log", "代理运行日志"));
                Assert((bool)logResponse["success"] && (string)logResponse["method"] == "Log", "TLS /Log receives success response");
                Assert(received.WaitOne(3000) && last.Method == "Log" && last.Status == 200 && last.Transport == "TLS",
                    "TLS /Log is captured as a successful request");
            }
            finally { server.Stop(); }
        }
        Directory.Delete(logDirectory, true);
    }

    private static Dictionary<string, string> Templates()
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string method in new[] { "Status", "OEE", "Alarm", "ExposureResult", "HeartBeat", "Mode" })
            result[method] = "{\"success\":true,\"method\":\"" + method + "\"}";
        return result;
    }

    private static void VerifyTrustedProxySourceRouting()
    {
        TcpListener reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string logDirectory = Path.Combine(Path.GetTempPath(), "kst-source-route-" + Guid.NewGuid().ToString("N"));
        KstMesServer server = new KstMesServer(logDirectory);
        int acceptedCount = 0;
        KstExchange accepted = null;
        KstExchange completed = null;
        using (ManualResetEvent completedEvent = new ManualResetEvent(false))
        {
            server.RequestAccepted += delegate(KstExchange item) { accepted = item; Interlocked.Increment(ref acceptedCount); };
            server.ExchangeReceived += delegate(KstExchange item) { completed = item; completedEvent.Set(); };
            try
            {
                Dictionary<string, string> hostToCode = new Dictionary<string, string> {
                    { "192.168.10.1:9000", "KST-001" }, { "192.168.10.1:9001", "KST-002" }
                };
                Dictionary<string, string> staleAssets = new Dictionary<string, string> {
                    { "192.168.10.1:9001", "KST-001" }
                };
                MethodInfo configure = typeof(KstMesServer).GetMethod("ConfigureSourceRoutes",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new[] { typeof(string), typeof(Dictionary<string, string>), typeof(Dictionary<string, string>) }, null);
                Assert(configure != null, "server provides explicit trusted proxy source routes");
                configure.Invoke(server, new object[] { "127.0.0.1", hostToCode, staleAssets });
                server.Start("127.0.0.1", port, Templates());

                completedEvent.Reset();
                Assert(ProxyPost(port, "127.0.0.1", new[] { "192.168.10.1:9001" }, "/OEE",
                    "{\"assets\":\"KST-001\",\"OEE\":0.25}", null) == 200,
                    "trusted 9001 OEE with known stale assets is acknowledged");
                Assert(completedEvent.WaitOne(3000) && acceptedCount == 1 && accepted.DeviceCode == "KST-002",
                    "trusted 9001 OEE routes to KST-002 before forwarding");

                completedEvent.Reset();
                Assert(ProxyPost(port, "127.0.0.1", new[] { "192.168.10.1:9001" }, "/Status",
                    "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}", null) == 200,
                    "trusted 9001 Status with known stale assets is acknowledged");
                Assert(completedEvent.WaitOne(3000) && acceptedCount == 2 && accepted.DeviceCode == "KST-002",
                    "trusted 9001 Status routes to KST-002 before forwarding");

                completedEvent.Reset();
                Assert(ProxyPost(port, "127.0.0.1", new[] { "192.168.10.1:9001" }, "/Heart",
                    "{\"MachineNO\":\"KST-002\",\"assets\":\"KST-001\",\"HeartBeat\":13382}", null) == 200,
                    "trusted 9001 Heart accepts the corroborating MachineNO despite stale assets");
                Assert(completedEvent.WaitOne(3000) && acceptedCount == 3 && accepted.DeviceCode == "KST-002",
                    "trusted 9001 Heart remains assigned to KST-002");
                Assert(completed.Diagnostic.Contains("assets") && completed.Diagnostic.Contains("KST-001")
                    && completed.Diagnostic.Contains("KST-002"), "accepted stale assets conflict is recorded in diagnostics");
                Dictionary<string, object> recordedHeart = LastLog(logDirectory);
                Assert((string)recordedHeart["machine_no"] == "KST-002"
                    && ((string)recordedHeart["diagnostic"]).Contains("assets"),
                    "daily log records canonical device and stale assets warning");

                completedEvent.Reset();
                Assert(ProxyPost(port, "127.0.0.1", new[] { "192.168.10.1:9001" }, "/Alarm",
                    "{\"assets\":\"KST-001\",\"EventMessage\":\"Alarm from second machine\",\"Level\":\"1\"}", null) == 200,
                    "trusted 9001 Alarm with known stale assets is acknowledged");
                Assert(completedEvent.WaitOne(3000) && acceptedCount == 4 && accepted.DeviceCode == "KST-002",
                    "trusted 9001 Alarm routes to KST-002 before forwarding");

                completedEvent.Reset();
                Assert(ProxyPost(port, "127.0.0.1", new[] { "192.168.10.1:9000" }, "/Status",
                    "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}", null) == 200,
                    "trusted 9000 remains the first device");
                Assert(completedEvent.WaitOne(3000) && acceptedCount == 5 && accepted.DeviceCode == "KST-001",
                    "trusted 9000 forwards as KST-001");

                AssertRejectedProxyPost(port, ref acceptedCount, completedEvent,
                    "127.0.0.1", new[] { "192.168.10.1:9001" }, "/Heart",
                    "{\"MachineNO\":\"KST-001\",\"assets\":\"KST-001\"}", null,
                    "explicit MachineNO conflicting with 9001 is rejected");
                AssertRejectedProxyPost(port, ref acceptedCount, completedEvent,
                    "127.0.0.1", new[] { "192.168.10.1:9001" }, "/Status",
                    "{\"assets\":\"KST-099\",\"Status\":\"WORK\"}", null,
                    "unknown assets cannot use the stale-assets exception");
                AssertRejectedProxyPost(port, ref acceptedCount, completedEvent,
                    "127.0.0.1", new[] { "192.168.10.1:9000" }, "/Status",
                    "{\"assets\":\"KST-002\",\"Status\":\"WORK\"}", null,
                    "9000 cannot claim second device assets");
                AssertRejectedProxyPost(port, ref acceptedCount, completedEvent,
                    "127.0.0.1", new[] { "192.168.10.1:9002" }, "/Status",
                    "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}", null,
                    "unknown proxy listener Host is rejected");
                AssertRejectedProxyPost(port, ref acceptedCount, completedEvent,
                    "127.0.0.1", new[] { "192.168.10.1:9000", "192.168.10.1:9001" }, "/Status",
                    "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}", null,
                    "duplicate Host cannot choose a source route");
                AssertRejectedProxyPost(port, ref acceptedCount, completedEvent,
                    "127.0.0.2", new[] { "192.168.10.1:9001" }, "/Status",
                    "{\"assets\":\"KST-001\",\"Status\":\"WORK\"}", "X-Forwarded-For: 127.0.0.1\r\n",
                    "an untrusted TCP peer cannot spoof the trusted proxy through headers");
            }
            finally { server.Stop(); }
        }
        Directory.Delete(logDirectory, true);
    }

    private static void AssertRejectedProxyPost(int port, ref int acceptedCount,
        ManualResetEvent completedEvent, string sourceIp, string[] hosts, string path, string body,
        string extraHeaders, string label)
    {
        int before = Thread.VolatileRead(ref acceptedCount);
        completedEvent.Reset();
        Assert(ProxyPost(port, sourceIp, hosts, path, body, extraHeaders) == 400, label);
        Assert(completedEvent.WaitOne(3000), label + " is logged");
        Assert(Thread.VolatileRead(ref acceptedCount) == before, label + " before MQTT forwarding");
    }

    private static int ProxyPost(int port, string sourceIp, string[] hosts, string path, string body, string extraHeaders)
    {
        using (TcpClient client = new TcpClient(new IPEndPoint(IPAddress.Parse(sourceIp), 0)))
        {
            client.Connect(IPAddress.Loopback, port);
            using (NetworkStream stream = client.GetStream())
            {
                stream.ReadTimeout = 5000;
                byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                StringBuilder header = new StringBuilder("POST " + path + " HTTP/1.1\r\n");
                foreach (string host in hosts) header.Append("Host: ").Append(host).Append("\r\n");
                if (extraHeaders != null) header.Append(extraHeaders);
                header.Append("Content-Type: application/json; charset=utf-8\r\nContent-Length: ")
                    .Append(bodyBytes.Length).Append("\r\nConnection: close\r\n\r\n");
                byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Write(bodyBytes, 0, bodyBytes.Length);
                using (MemoryStream response = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int length;
                    try
                    {
                        while ((length = stream.Read(buffer, 0, buffer.Length)) > 0) response.Write(buffer, 0, length);
                    }
                    catch (IOException)
                    {
                        // A rejected request can leave unread body bytes; closing that socket may reset
                        // it after the HTTP response has already arrived.
                        if (response.Length == 0) throw;
                    }
                    string text = Encoding.UTF8.GetString(response.ToArray());
                    int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
                    Assert(lineEnd > 0, "proxy response has an HTTP status line");
                    string[] parts = text.Substring(0, lineEnd).Split(' ');
                    int status = 0;
                    Assert(parts.Length >= 2 && int.TryParse(parts[1], out status), "proxy response has numeric status");
                    return status;
                }
            }
        }
    }

    private static string Post(int port, string path, string body)
    {
        return Post(port, path, body, "application/json");
    }

    private static string Post(int port, string path, string body, string contentType)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
        request.Proxy = null;
        request.Method = "POST";
        request.ContentType = contentType;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        request.ContentLength = bytes.Length;
        using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        using (WebResponse response = request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
    }

    private static void RawRequest(int port, string path, byte[] body, int declaredLength)
    {
        using (TcpClient client = new TcpClient("127.0.0.1", port))
        using (NetworkStream stream = client.GetStream())
        {
            byte[] headers = Encoding.ASCII.GetBytes("POST " + path + " HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + declaredLength + "\r\nConnection: close\r\n\r\n");
            stream.Write(headers, 0, headers.Length);
            stream.Write(body, 0, body.Length);
            client.Client.Shutdown(SocketShutdown.Send);
            byte[] response = new byte[4096];
            while (stream.Read(response, 0, response.Length) > 0) { }
        }
    }

    private static string TlsPost(int port, string path, string body)
    {
        using (TcpClient client = new TcpClient("127.0.0.1", port))
        using (SslStream stream = new SslStream(client.GetStream(), false, delegate { return true; }))
        {
            stream.AuthenticateAsClient("192.168.1.71", null, SslProtocols.Tls12, false);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            byte[] headers = Encoding.ASCII.GetBytes("POST " + path + " HTTP/1.1\r\nHost: 192.168.1.71\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + bodyBytes.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(headers, 0, headers.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
            using (MemoryStream response = new MemoryStream())
            {
                byte[] buffer = new byte[4096];
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) response.Write(buffer, 0, count);
                string message = Encoding.UTF8.GetString(response.ToArray());
                int bodyStart = message.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                Assert(bodyStart >= 0, "TLS response contains HTTP headers");
                return message.Substring(bodyStart + 4);
            }
        }
    }

    private static Dictionary<string, object> LastLog(string directory)
    {
        string[] files = Directory.GetFiles(directory, "*.jsonl");
        Assert(files.Length == 1, "one daily diagnostic log exists");
        string last = null;
        using (FileStream stream = new FileStream(files[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            string line;
            while ((line = reader.ReadLine()) != null) last = line;
        }
        Assert(last != null, "diagnostic log contains a record");
        return Json(last);
    }

    private static void WaitForExchangeCount(ref int count, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (Thread.VolatileRead(ref count) < expected && DateTime.UtcNow < deadline) Thread.Sleep(10);
        Assert(Thread.VolatileRead(ref count) >= expected, "expected request exchange count " + expected);
    }

    private static int Status(int port, string method, string path, string body)
    {
        try
        {
            if (method == "POST") Post(port, path, body);
            else
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
                request.Proxy = null;
                request.Method = method;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse()) return (int)response.StatusCode;
            }
            return 200;
        }
        catch (WebException error)
        {
            using (HttpWebResponse response = (HttpWebResponse)error.Response) return (int)response.StatusCode;
        }
    }

    private static Dictionary<string, object> Json(string text)
    {
        return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Failed: " + message);
    }
}
