using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class KstExchange
{
    internal DateTime Time;
    internal string Method;
    internal string ClientIp;
    internal string RequestHost;
    internal string DeviceCode;
    internal string RoutingSource;
    internal string RouteDeviceCode;
    internal bool AcceptedStaleAsset;
    internal string RequestPath;
    internal string RequestBody;
    internal string Transport;
    internal string RawRequestText;
    internal string RawRequestHex;
    internal int RawReceivedBytes;
    internal bool RawRequestTruncated;
    internal string DecodedRequestText;
    internal string DecodedRequestHex;
    internal int DecodedReceivedBytes;
    internal bool DecodedRequestTruncated;
    internal string Diagnostic;
    internal string ResponseBody;
    internal int Status;

    internal KstExchange ForForwardingAs(string method)
    {
        KstExchange forwarded = (KstExchange)MemberwiseClone();
        forwarded.Method = method;
        return forwarded;
    }
}

internal static class KstDiagnosticFormatter
{
    internal static string RequestDetails(KstExchange item)
    {
        StringBuilder text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.RequestBody))
        {
            text.AppendLine("解析后的请求正文:");
            text.AppendLine(item.RequestBody);
            text.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(item.Diagnostic))
        {
            text.AppendLine("诊断信息:");
            text.AppendLine(item.Diagnostic);
            text.AppendLine();
        }
        text.Append("传输方式: ").AppendLine(string.IsNullOrEmpty(item.Transport) ? "HTTP" : item.Transport);
        if (!string.IsNullOrEmpty(item.RequestHost)) text.Append("请求 Host: ").AppendLine(item.RequestHost);
        if (!string.IsNullOrEmpty(item.RoutingSource)) text.Append("设备归属: ")
            .Append(string.IsNullOrEmpty(item.DeviceCode) ? item.RouteDeviceCode : item.DeviceCode)
            .Append(" (").Append(item.RoutingSource).AppendLine(")");
        text.Append("传输层接收字节数: ").AppendLine(item.RawReceivedBytes.ToString(CultureInfo.InvariantCulture));
        if (item.RawRequestTruncated) text.AppendLine("传输层原始报文仅保留前 65536 字节。");
        if (!string.IsNullOrEmpty(item.DecodedRequestText))
        {
            text.AppendLine();
            text.Append("解密后 HTTP 字节数: ").AppendLine(item.DecodedReceivedBytes.ToString(CultureInfo.InvariantCulture));
            if (item.DecodedRequestTruncated) text.AppendLine("解密后的 HTTP 报文仅保留前 65536 字节。");
            text.AppendLine("解密后的 HTTP 报文（UTF-8 展示）:");
            text.AppendLine(item.DecodedRequestText);
        }
        if (!string.IsNullOrEmpty(item.RawRequestText))
        {
            text.AppendLine();
            text.AppendLine("传输层原始报文（UTF-8 展示）:");
            text.AppendLine(item.RawRequestText);
        }
        if (!string.IsNullOrEmpty(item.RawRequestHex))
        {
            text.AppendLine();
            text.AppendLine("传输层原始报文 HEX:");
            text.Append(item.RawRequestHex);
        }
        return text.ToString();
    }
}

internal static class KstResponseBuilder
{
    internal static string SelectTemplate(Dictionary<string, Dictionary<string, string>> deviceResponses, string deviceCode, string method, Dictionary<string, string> responses)
    {
        Dictionary<string, string> deviceTemplates;
        string response;
        if (!string.IsNullOrWhiteSpace(deviceCode) && deviceResponses != null
            && deviceResponses.TryGetValue(deviceCode, out deviceTemplates)
            && deviceTemplates.TryGetValue(method, out response)) return response;
        return responses.TryGetValue(method, out response) ? response : null;
    }

    internal static string Build(string template)
    {
        Dictionary<string, object> response = new JavaScriptSerializer().DeserializeObject(template) as Dictionary<string, object>;
        if (response == null) throw new ArgumentException("Response template must be a JSON object.");
        return template;
    }
}

internal static class KstRoutes
{
    internal static string FromPath(string path)
    {
        string route = (path ?? "").TrimEnd('/');
        foreach (string name in new[] { "Status", "OEE", "Alarm", "ExposureResult", "Mode", "Log" })
            if (route == "/" + name) return name;
        if (route == "/Heart") return "HeartBeat";
        return null;
    }
}

internal sealed class KstLogWriter
{
    private readonly string directory;
    private readonly object writeLock = new object();

    internal KstLogWriter(string directory) { this.directory = directory; }

    internal event Action<Exception> WriteFailed;

    internal bool Append(KstExchange item)
    {
        try
        {
            Dictionary<string, object> record = new Dictionary<string, object>();
            record["time"] = item.Time.ToString("o", CultureInfo.InvariantCulture);
            record["method"] = item.Method ?? "";
            record["client_ip"] = item.ClientIp ?? "";
            record["request_host"] = item.RequestHost ?? "";
            record["machine_no"] = item.DeviceCode ?? "";
            record["routing_source"] = item.RoutingSource ?? "";
            record["route_device_code"] = item.RouteDeviceCode ?? "";
            record["accepted_stale_asset"] = item.AcceptedStaleAsset;
            record["request_path"] = item.RequestPath ?? "";
            record["request"] = item.RequestBody ?? "";
            record["transport"] = item.Transport ?? "";
            record["raw_received_bytes"] = item.RawReceivedBytes;
            record["raw_request_truncated"] = item.RawRequestTruncated;
            record["raw_request_text"] = item.RawRequestText ?? "";
            record["raw_request_hex"] = item.RawRequestHex ?? "";
            record["decoded_received_bytes"] = item.DecodedReceivedBytes;
            record["decoded_request_truncated"] = item.DecodedRequestTruncated;
            record["decoded_request_text"] = item.DecodedRequestText ?? "";
            record["decoded_request_hex"] = item.DecodedRequestHex ?? "";
            record["diagnostic"] = item.Diagnostic ?? "";
            record["status"] = item.Status;
            record["response"] = item.ResponseBody ?? "";
            string filePath = Path.Combine(directory, item.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            lock (writeLock)
            {
                Directory.CreateDirectory(directory);
                using (FileStream stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.WriteLine(new JavaScriptSerializer().Serialize(record));
            }
            return true;
        }
        catch (Exception error)
        {
            Action<Exception> handler = WriteFailed;
            if (handler != null) handler(error);
            return false;
        }
    }
}

internal static class KstTlsCertificate
{
    internal const string Password = "kst-mes-mock";

    internal static X509Certificate2 LoadDefault()
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_server.pfx");
        if (!File.Exists(path))
            throw new FileNotFoundException("TLS certificate was not found: " + path, path);
        X509Certificate2 certificate = new X509Certificate2(path, Password);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("TLS certificate does not contain a private key: " + path);
        }
        return certificate;
    }
}

internal sealed class KstMesServer
{
    internal const string DefaultLogResponse = "{\"success\":true,\"message\":\"accepted\",\"method\":\"Log\"}";
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxCapturedRawBytes = 64 * 1024;
    private const int MaxConcurrentHandlers = 32;
    private volatile TcpListener listener;
    private Thread acceptThread;
    private volatile Dictionary<string, string> responses;
    private volatile Dictionary<string, Dictionary<string, string>> deviceResponses;
    private volatile X509Certificate2 tlsCertificate;
    private volatile SourceRouteSnapshot sourceRoutes;
    private readonly KstLogWriter logWriter;
    private readonly object clientsLock = new object();
    private readonly HashSet<TcpClient> activeClients = new HashSet<TcpClient>();
    private readonly Semaphore handlerSlots = new Semaphore(MaxConcurrentHandlers, MaxConcurrentHandlers);

    private sealed class HttpRequestInfo
    {
        internal string Method;
        internal string Path;
        internal string Body;
        internal string Host;
    }

    private sealed class SourceRouteSnapshot
    {
        internal readonly string TrustedProxyIp;
        internal readonly Dictionary<string, string> HostToCode;
        internal readonly Dictionary<string, string> AllowedStaleAssets;

        internal SourceRouteSnapshot(string trustedProxyIp, Dictionary<string, string> hostToCode,
            Dictionary<string, string> allowedStaleAssets)
        {
            TrustedProxyIp = trustedProxyIp;
            HostToCode = hostToCode;
            AllowedStaleAssets = allowedStaleAssets;
        }
    }

    private sealed class HttpRequestException : Exception
    {
        internal readonly int Status;
        internal HttpRequestException(int status, string message) : base(message) { Status = status; }
    }

    private sealed class RawRequestCapture
    {
        private readonly MemoryStream bytes = new MemoryStream();
        private int totalBytes;

        internal void Append(byte[] buffer, int offset, int count)
        {
            totalBytes += count;
            int remaining = MaxCapturedRawBytes - (int)bytes.Length;
            if (remaining > 0) bytes.Write(buffer, offset, Math.Min(remaining, count));
        }

        internal void Apply(KstExchange item)
        {
            byte[] captured = bytes.ToArray();
            item.RawReceivedBytes = totalBytes;
            item.RawRequestTruncated = totalBytes > captured.Length;
            item.RawRequestText = Encoding.UTF8.GetString(captured);
            item.RawRequestHex = BitConverter.ToString(captured).Replace("-", "");
        }

        internal void ApplyDecoded(KstExchange item)
        {
            byte[] captured = bytes.ToArray();
            item.DecodedReceivedBytes = totalBytes;
            item.DecodedRequestTruncated = totalBytes > captured.Length;
            item.DecodedRequestText = Encoding.UTF8.GetString(captured);
            item.DecodedRequestHex = BitConverter.ToString(captured).Replace("-", "");
        }
    }

    private sealed class CaptureReadStream : Stream
    {
        private readonly Stream inner;
        private readonly RawRequestCapture capture;

        internal CaptureReadStream(Stream inner, RawRequestCapture capture)
        {
            this.inner = inner;
            this.capture = capture;
        }

        public override bool CanRead { get { return inner.CanRead; } }
        public override bool CanSeek { get { return inner.CanSeek; } }
        public override bool CanWrite { get { return inner.CanWrite; } }
        public override long Length { get { return inner.Length; } }
        public override long Position { get { return inner.Position; } set { inner.Position = value; } }
        public override int ReadTimeout { get { return inner.ReadTimeout; } set { inner.ReadTimeout = value; } }
        public override int WriteTimeout { get { return inner.WriteTimeout; } set { inner.WriteTimeout = value; } }
        public override void Flush() { inner.Flush(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int received = inner.Read(buffer, offset, count);
            if (received > 0) capture.Append(buffer, offset, received);
            return received;
        }
        public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
        public override void SetLength(long value) { inner.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); }
    }

    internal event Action<KstExchange> ExchangeReceived;
    internal event Action<KstExchange> RequestAccepted;
    internal event Action<Exception> LogWriteFailed;
    internal bool IsRunning { get { return listener != null; } }

    internal KstMesServer() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")) { }

    internal KstMesServer(string logDirectory)
    {
        logWriter = new KstLogWriter(logDirectory);
        logWriter.WriteFailed += delegate(Exception error)
        {
            Action<Exception> handler = LogWriteFailed;
            if (handler != null) handler(error);
        };
        deviceResponses = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    }

    internal void ConfigureSourceRoutes(string trustedProxyIp, Dictionary<string, string> hostToCode,
        Dictionary<string, string> allowedStaleAssets)
    {
        if (IsRunning) throw new InvalidOperationException("Stop the service before changing source routes.");
        int count = hostToCode == null ? 0 : hostToCode.Count;
        if (count == 0)
        {
            if (!string.IsNullOrWhiteSpace(trustedProxyIp) || (allowedStaleAssets != null && allowedStaleAssets.Count > 0))
                throw new ArgumentException("Source routes need both a trusted proxy IP and Host mappings.");
            sourceRoutes = null;
            return;
        }
        IPAddress proxyAddress;
        if (!IPAddress.TryParse(trustedProxyIp, out proxyAddress)
            || proxyAddress.AddressFamily != AddressFamily.InterNetwork
            || proxyAddress.ToString() != trustedProxyIp)
            throw new ArgumentException("Trusted proxy must be a canonical IPv4 address.");
        Dictionary<string, string> hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> entry in hostToCode)
        {
            if (!ValidSourceHost(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                throw new ArgumentException("Each source route needs a canonical IPv4:port Host and a device code.");
            if (hosts.ContainsKey(entry.Key)) throw new ArgumentException("Duplicate source Host.");
            hosts.Add(entry.Key, entry.Value.Trim());
            if (!devices.Add(NormalizeDeviceCode(entry.Value)))
                throw new ArgumentException("Source route device codes must be unique.");
        }
        Dictionary<string, string> stale = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (allowedStaleAssets != null)
            foreach (KeyValuePair<string, string> entry in allowedStaleAssets)
            {
                if (!hosts.ContainsKey(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                    throw new ArgumentException("A stale assets exception must name a configured source Host and code.");
                stale.Add(entry.Key, entry.Value.Trim());
            }
        sourceRoutes = new SourceRouteSnapshot(proxyAddress.ToString(), hosts, stale);
    }

    internal void Start(string ipText, int port, Dictionary<string, string> templates)
    {
        Start(ipText, port, templates, new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal));
    }

    internal void Start(string ipText, int port, Dictionary<string, string> templates, Dictionary<string, Dictionary<string, string>> deviceTemplates)
    {
        Start(ipText, port, templates, deviceTemplates, null);
    }

    internal void Start(string ipText, int port, Dictionary<string, string> templates, Dictionary<string, Dictionary<string, string>> deviceTemplates, X509Certificate2 certificate)
    {
        if (IsRunning) throw new InvalidOperationException("Service is already running.");
        IPAddress ip;
        if (!IPAddress.TryParse(ipText, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Enter a valid IPv4 address (0.0.0.0 listens on all interfaces).");
        if (port < 1 || port > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        UpdateResponses(templates);
        UpdateDeviceResponses(deviceTemplates);
        tlsCertificate = certificate;

        TcpListener next = new TcpListener(ip, port);
        try { next.Start(); }
        catch { next.Stop(); throw; }
        listener = next;
        acceptThread = new Thread(delegate() { Accept(next); });
        acceptThread.IsBackground = true;
        acceptThread.Start();
    }

    internal void UpdateResponses(Dictionary<string, string> templates)
    {
        if (templates == null || templates.Count == 0) throw new ArgumentException("Response config must be a nonempty JSON object.");
        Dictionary<string, string> copy = new Dictionary<string, string>(templates, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in copy)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                throw new ArgumentException("Each method needs a response template.");
        }
        responses = copy;
    }

    internal void UpdateDeviceResponses(Dictionary<string, Dictionary<string, string>> templates)
    {
        Dictionary<string, Dictionary<string, string>> copy = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (templates == null) { deviceResponses = copy; return; }
        foreach (KeyValuePair<string, Dictionary<string, string>> device in templates)
        {
            if (string.IsNullOrWhiteSpace(device.Key) || device.Value == null || device.Value.Count == 0)
                throw new ArgumentException("Each device needs one or more response templates.");
            Dictionary<string, string> responsesForDevice = new Dictionary<string, string>(device.Value, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> response in responsesForDevice)
                if (string.IsNullOrWhiteSpace(response.Key) || string.IsNullOrWhiteSpace(response.Value))
                    throw new ArgumentException("Each device method needs a response template.");
            copy.Add(device.Key, responsesForDevice);
        }
        deviceResponses = copy;
    }

    internal void Stop()
    {
        TcpListener active;
        TcpClient[] clients;
        lock (clientsLock)
        {
            active = listener;
            if (active == null) return;
            listener = null;
            clients = new TcpClient[activeClients.Count];
            activeClients.CopyTo(clients);
            activeClients.Clear();
        }
        try { active.Stop(); } catch (SocketException) { }
        foreach (TcpClient client in clients) CloseSocket(client);
        if (Thread.CurrentThread != acceptThread) acceptThread.Join(2000);
    }

    private void Accept(TcpListener active)
    {
        while (true)
        {
            TcpClient client;
            try { client = active.AcceptTcpClient(); }
            catch (SocketException)
            {
                if (listener == active) listener = null;
                break;
            }
            catch (ObjectDisposedException) { break; }
            lock (clientsLock)
            {
                if (listener != active)
                {
                    CloseSocket(client);
                    break;
                }
                activeClients.Add(client);
            }
            if (!handlerSlots.WaitOne(0))
            {
                CloseTrackedClient(client);
                continue;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { Handle(client); }
                finally { handlerSlots.Release(); }
            });
        }
    }

    private void Handle(TcpClient client)
    {
        KstExchange item = new KstExchange();
        item.Time = DateTime.Now;
        item.ClientIp = "unknown";
        item.DeviceCode = "";
        item.RequestPath = "";
        item.RequestBody = "";
        item.Transport = "HTTP";
        item.RawRequestText = "";
        item.RawRequestHex = "";
        item.DecodedRequestText = "";
        item.DecodedRequestHex = "";
        item.Diagnostic = "";
        item.Method = "";
        item.Status = 200;
        NetworkStream networkStream = null;
        Stream responseStream = null;
        SslStream tlsStream = null;
        RawRequestCapture transportCapture = new RawRequestCapture();
        RawRequestCapture decodedCapture = new RawRequestCapture();
        try
        {
            IPEndPoint endpoint = client.Client.RemoteEndPoint as IPEndPoint;
            if (endpoint != null) item.ClientIp = endpoint.Address.ToString();
            networkStream = client.GetStream();
            networkStream.ReadTimeout = 10000;
            networkStream.WriteTimeout = 10000;
            CaptureReadStream transportStream = new CaptureReadStream(networkStream, transportCapture);
            responseStream = transportStream;
            X509Certificate2 certificate = tlsCertificate;
            if (certificate != null && IsTlsClientHello(client))
            {
                item.Transport = "TLS";
                tlsStream = new SslStream(transportStream, true);
                tlsStream.ReadTimeout = 10000;
                tlsStream.WriteTimeout = 10000;
                tlsStream.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
                responseStream = tlsStream;
            }
            HttpRequestInfo request = ReadRequest(responseStream, decodedCapture);
            item.RequestPath = request.Path;
            item.RequestBody = request.Body;
            item.RequestHost = request.Host;
            item.Method = KstRoutes.FromPath(item.RequestPath);
            if (item.Method == null)
            {
                item.Status = 404;
                item.ResponseBody = Error("Unknown path");
            }
            else if (request.Method != "POST")
            {
                item.Status = 405;
                item.ResponseBody = Error("Only POST is supported");
            }
            else if (item.Method == "Log")
            {
                // Keep the original proxy log; result records also use the exposure MQTT mapping.
                Dictionary<string, object> logBody = null;
                try
                {
                    logBody = new JavaScriptSerializer().DeserializeObject(item.RequestBody) as Dictionary<string, object>;
                    item.DeviceCode = ResolveDeviceCode(item, logBody);
                }
                catch (ArgumentException)
                {
                    if (sourceRoutes != null) item.DeviceCode = ResolveDeviceCode(item, null);
                }
                catch (InvalidOperationException)
                {
                    if (sourceRoutes != null) item.DeviceCode = ResolveDeviceCode(item, null);
                }
                string template = KstResponseBuilder.SelectTemplate(deviceResponses, "", "Log", responses);
                item.ResponseBody = KstResponseBuilder.Build(template ?? DefaultLogResponse);
                if (IsResultLog(logBody))
                {
                    Action<KstExchange> accepted = RequestAccepted;
                    if (accepted != null) accepted(item.ForForwardingAs("ExposureResult"));
                }
            }
            else
            {
                Dictionary<string, object> requestBody;
                try { requestBody = new JavaScriptSerializer().DeserializeObject(item.RequestBody) as Dictionary<string, object>; }
                catch (ArgumentException) { requestBody = null; }
                catch (InvalidOperationException) { requestBody = null; }
                if (requestBody == null)
                {
                    item.Status = 400;
                    item.ResponseBody = Error("Invalid JSON request");
                }
                else
                {
                    item.DeviceCode = ResolveDeviceCode(item, requestBody);
                    string response = KstResponseBuilder.SelectTemplate(deviceResponses, item.DeviceCode, item.Method, responses);
                    if (response != null)
                    {
                        item.ResponseBody = KstResponseBuilder.Build(response);
                        Action<KstExchange> accepted = RequestAccepted;
                        if (accepted != null) accepted(item);
                    }
                    else
                    {
                        item.Status = 500;
                        item.ResponseBody = Error("Response template is missing");
                    }
                }
            }
        }
        catch (HttpRequestException error)
        {
            item.Status = error.Status;
            item.ResponseBody = Error(error.Message);
            item.Diagnostic = Diagnostic(error);
        }
        catch (Exception error)
        {
            item.Status = 500;
            item.ResponseBody = Error("Server error");
            item.Diagnostic = Diagnostic(error);
            if (item.Transport == "TLS") item.Diagnostic += TlsFailureHint(error);
        }
        finally
        {
            transportCapture.Apply(item);
            decodedCapture.ApplyDecoded(item);
            if (responseStream != null) WriteResponse(responseStream, item.Status, item.ResponseBody ?? "");
            if (tlsStream != null) try { tlsStream.Close(); } catch (Exception) { }
            CloseTrackedClient(client);
            logWriter.Append(item);
            Action<KstExchange> handler = ExchangeReceived;
            if (handler != null) handler(item);
        }
    }

    private void CloseTrackedClient(TcpClient client)
    {
        lock (clientsLock) activeClients.Remove(client);
        CloseSocket(client);
    }

    private static void CloseSocket(TcpClient client)
    {
        try { client.Close(); } catch (Exception) { }
    }

    private static string DeviceCode(Dictionary<string, object> request)
    {
        object value;
        if (request.TryGetValue("MachineNO", out value) && !string.IsNullOrWhiteSpace(Text(value))) return Text(value).Trim();
        if (request.TryGetValue("assets", out value) && !string.IsNullOrWhiteSpace(Text(value))) return Text(value).Trim();
        return request.TryGetValue("CMACHINE_CODE", out value) ? Text(value).Trim() : "";
    }

    private string ResolveDeviceCode(KstExchange item, Dictionary<string, object> body)
    {
        SourceRouteSnapshot routes = sourceRoutes;
        if (routes == null)
        {
            if (body == null) return "";
            if (item.Method == "Log" && IsResultLog(body))
            {
                string resultMachine = DeviceCodeField(body, "CMACHINE_CODE");
                string machine = DeviceCodeField(body, "MachineNO");
                if (resultMachine.Length > 0)
                {
                    if (machine.Length > 0 && !SameDeviceCode(machine, resultMachine))
                        throw new HttpRequestException(400, "MachineNO conflicts with CMACHINE_CODE in result Log.");
                    return resultMachine;
                }
            }
            return DeviceCode(body);
        }
        if (!string.Equals(item.ClientIp, routes.TrustedProxyIp, StringComparison.Ordinal))
            throw new HttpRequestException(400, "Request did not come from the configured KST proxy IP.");
        string code;
        if (string.IsNullOrEmpty(item.RequestHost) || !routes.HostToCode.TryGetValue(item.RequestHost, out code))
            throw new HttpRequestException(400, "Request Host does not match a configured KST source route.");
        item.RoutingSource = "trusted_proxy_host";
        item.RouteDeviceCode = code;
        if (body != null)
        {
            foreach (string field in new[] { "MachineNO", "CMACHINE_CODE" })
            {
                string explicitCode = DeviceCodeField(body, field);
                if (explicitCode.Length > 0 && !SameDeviceCode(explicitCode, code))
                    throw new HttpRequestException(400, field + " conflicts with the configured source Host device " + code + ".");
            }
            string assets = DeviceCodeField(body, "assets");
            if (assets.Length > 0 && !SameDeviceCode(assets, code))
            {
                string allowed;
                if (!routes.AllowedStaleAssets.TryGetValue(item.RequestHost, out allowed)
                    || !SameDeviceCode(assets, allowed))
                    throw new HttpRequestException(400, "assets conflicts with the configured source Host device " + code + ".");
                item.AcceptedStaleAsset = true;
                item.Diagnostic = "可信代理入口 " + item.RequestHost + " 映射为 " + code
                    + "；报文 assets=" + assets + " 是该入口配置允许的陈旧编号。";
            }
        }
        return code;
    }

    private static bool IsResultLog(Dictionary<string, object> body)
    {
        object function;
        return body != null && body.TryGetValue("function", out function)
            && string.Equals(Text(function), "result", StringComparison.OrdinalIgnoreCase);
    }

    private static string DeviceCodeField(Dictionary<string, object> body, string field)
    {
        object value;
        return body.TryGetValue(field, out value) ? Text(value) : "";
    }

    private static bool SameDeviceCode(string first, string second)
    {
        return string.Equals(NormalizeDeviceCode(first), NormalizeDeviceCode(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDeviceCode(string code)
    {
        return (code ?? "").Trim().Replace("-", "");
    }

    private static bool ValidSourceHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string[] parts = host.Split(':');
        IPAddress ip;
        int port;
        return parts.Length == 2 && IPAddress.TryParse(parts[0], out ip)
            && ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() == parts[0]
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port >= 1 && port <= 65535
            && port.ToString(CultureInfo.InvariantCulture) == parts[1];
    }

    private static bool IsTlsClientHello(TcpClient client)
    {
        byte[] firstByte = new byte[1];
        return client.Client.Receive(firstByte, 0, 1, SocketFlags.Peek) == 1 && firstByte[0] == 0x16;
    }

    private static string Text(object value)
    {
        return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
    }

    private static HttpRequestInfo ReadRequest(Stream input, RawRequestCapture capture)
    {
        byte[] buffer = new byte[4096];
        int headerEnd = -1;
        using (MemoryStream received = new MemoryStream())
        {
            while (headerEnd < 0)
            {
                int count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) throw new HttpRequestException(400, "Incomplete HTTP request.");
                capture.Append(buffer, 0, count);
                received.Write(buffer, 0, count);
                if (received.Length > MaxHeaderBytes) throw new HttpRequestException(413, "HTTP headers exceed 32 KB.");
                headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
            }

            byte[] all = received.ToArray();
            string headers = Encoding.ASCII.GetString(all, 0, headerEnd);
            string[] lines = headers.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] firstLine = lines[0].Split(' ');
            if (firstLine.Length < 3) throw new HttpRequestException(400, "Invalid HTTP request line.");

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf(':');
                if (separator <= 0) throw new HttpRequestException(400, "Invalid HTTP header.");
                string name = lines[i].Substring(0, separator).Trim();
                if (values.ContainsKey(name) && (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Host", StringComparison.OrdinalIgnoreCase)))
                    throw new HttpRequestException(400, "Duplicate HTTP framing header.");
                values[name] = lines[i].Substring(separator + 1).Trim();
            }

            string host;
            values.TryGetValue("Host", out host);
            Uri absoluteTarget;
            if (Uri.TryCreate(firstLine[1], UriKind.Absolute, out absoluteTarget)
                && !string.Equals(absoluteTarget.Authority, host, StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException(400, "Absolute request target and Host disagree.");

            string transferEncoding;
            if (values.TryGetValue("Transfer-Encoding", out transferEncoding)
                && !string.Equals(transferEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException(501, "Transfer-Encoding is not supported.");

            string lengthText;
            int contentLength = 0;
            if (values.TryGetValue("Content-Length", out lengthText)
                && (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0))
                throw new HttpRequestException(400, "Invalid Content-Length header.");
            if (contentLength > MaxRequestBytes) throw new HttpRequestException(413, "Request body exceeds 1 MB.");

            string expect;
            if (values.TryGetValue("Expect", out expect))
            {
                if (!string.Equals(expect, "100-continue", StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException(417, "Unsupported Expect header.");
                if (contentLength > 0) WriteContinue(input);
            }

            int bodyStart = headerEnd + 4;
            byte[] body = new byte[contentLength];
            int available = all.Length - bodyStart;
            int copied = Math.Min(available, contentLength);
            if (copied > 0) Buffer.BlockCopy(all, bodyStart, body, 0, copied);
            while (copied < contentLength)
            {
                int count = input.Read(body, copied, contentLength - copied);
                if (count == 0) throw new HttpRequestException(400, "Incomplete HTTP request body.");
                capture.Append(body, copied, count);
                copied += count;
            }

            HttpRequestInfo request = new HttpRequestInfo();
            request.Method = firstLine[0].ToUpperInvariant();
            request.Path = RequestPath(firstLine[1]);
            request.Body = new UTF8Encoding(false, true).GetString(body);
            request.Host = host;
            return request;
        }
    }

    private static int FindHeaderEnd(byte[] bytes, int length)
    {
        for (int i = 3; i < length; i++)
            if (bytes[i - 3] == '\r' && bytes[i - 2] == '\n' && bytes[i - 1] == '\r' && bytes[i] == '\n') return i - 3;
        return -1;
    }

    private static string RequestPath(string requestTarget)
    {
        Uri absolute;
        if (Uri.TryCreate(requestTarget, UriKind.Absolute, out absolute)) return absolute.AbsolutePath;
        int query = requestTarget.IndexOf('?');
        return query < 0 ? requestTarget : requestTarget.Substring(0, query);
    }

    private static void WriteResponse(Stream output, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string headers = "HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " " + StatusText(status) + "\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + "Access-Control-Allow-Origin: *\r\n"
            + "Content-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "Connection: close\r\n\r\n";
        try
        {
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            output.Write(headerBytes, 0, headerBytes.Length);
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
        }
        catch (Exception) { }
    }

    private static void WriteContinue(Stream output)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
        output.Write(bytes, 0, bytes.Length);
    }

    private static string Diagnostic(Exception error)
    {
        StringBuilder text = new StringBuilder();
        for (Exception current = error; current != null; current = current.InnerException)
        {
            if (text.Length > 0) text.Append(" --> ");
            text.Append(current.GetType().FullName);
            if (!string.IsNullOrWhiteSpace(current.Message)) text.Append(": ").Append(current.Message);
            text.Append(" [HRESULT=0x").Append(current.HResult.ToString("X8", CultureInfo.InvariantCulture)).Append("]");
            Win32Exception native = current as Win32Exception;
            if (native != null)
                text.Append(" [NativeError=0x").Append(native.NativeErrorCode.ToString("X8", CultureInfo.InvariantCulture)).Append("]");
        }
        return text.ToString();
    }

    private static string TlsFailureHint(Exception error)
    {
        for (Exception current = error; current != null; current = current.InnerException)
        {
            string message = current.Message ?? "";
            if (message.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("证书", StringComparison.OrdinalIgnoreCase) >= 0)
                return Environment.NewLine + "TLS 握手未完成：客户端在收到服务端证书后拒绝连接。请在科视端信任 kst_server.cer，或将 kst_server.pfx 替换为科视认可的服务端证书。";
        }
        return Environment.NewLine + "TLS 握手未完成，尚未收到可解析的 HTTP 报文。";
    }

    private static string StatusText(int status)
    {
        if (status == 200) return "OK";
        if (status == 400) return "Bad Request";
        if (status == 404) return "Not Found";
        if (status == 405) return "Method Not Allowed";
        if (status == 413) return "Payload Too Large";
        if (status == 417) return "Expectation Failed";
        if (status == 501) return "Not Implemented";
        return "Internal Server Error";
    }

    private static string Error(string message)
    {
        return new JavaScriptSerializer().Serialize(new { success = false, message = message, error_code = -1, method = "" });
    }
}
