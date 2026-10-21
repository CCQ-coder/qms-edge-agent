using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class MesExchange
{
    internal DateTime Time;
    internal string Method;
    internal string ClientIp;
    internal string DeviceCode;
    internal string RequestPath;
    internal string RequestBody;
    internal string ResponseBody;
    internal int Status;
}

internal static class MesResponseBuilder
{
    private static long lastGeneratedId;

    internal static string SelectTemplate(Dictionary<string, Dictionary<string, string>> deviceResponses, string deviceCode, string method, Dictionary<string, string> responses)
    {
        Dictionary<string, string> deviceTemplates;
        string response;
        if (!string.IsNullOrWhiteSpace(deviceCode) && deviceResponses != null
            && deviceResponses.TryGetValue(deviceCode, out deviceTemplates)
            && deviceTemplates.TryGetValue(method, out response)) return response;
        return responses.TryGetValue(method, out response) ? response : null;
    }

    internal static string Build(string template, bool hasRequestId, object requestId)
    {
        Dictionary<string, object> response = new JavaScriptSerializer().DeserializeObject(template) as Dictionary<string, object>;
        if (response == null) throw new ArgumentException("Response template must be a JSON object.");
        response["timestamp"] = UnixMilliseconds(DateTime.UtcNow);
        response["id"] = hasRequestId ? requestId : (object)NextGeneratedId();
        return new JavaScriptSerializer().Serialize(response);
    }

    private static long NextGeneratedId()
    {
        while (true)
        {
            long previous = Interlocked.Read(ref lastGeneratedId);
            long candidate = Math.Max(UnixMilliseconds(DateTime.UtcNow), previous + 1);
            if (Interlocked.CompareExchange(ref lastGeneratedId, candidate, previous) == previous) return candidate;
        }
    }

    private static long UnixMilliseconds(DateTime value)
    {
        return (value.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;
    }
}

internal sealed class MesLogWriter
{
    private readonly string directory;
    private readonly object writeLock = new object();

    internal MesLogWriter(string directory) { this.directory = directory; }

    internal event Action<Exception> WriteFailed;

    internal bool Append(MesExchange item)
    {
        try
        {
            Dictionary<string, object> record = new Dictionary<string, object>();
            record["time"] = item.Time.ToString("o", CultureInfo.InvariantCulture);
            record["method"] = item.Method ?? "";
            record["client_ip"] = item.ClientIp ?? "";
            record["device_code"] = item.DeviceCode ?? "";
            record["request_path"] = item.RequestPath ?? "";
            record["request"] = item.RequestBody ?? "";
            record["status"] = item.Status;
            record["response"] = item.ResponseBody ?? "";
            string filePath = Path.Combine(directory, item.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            lock (writeLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(filePath, new JavaScriptSerializer().Serialize(record) + Environment.NewLine, new UTF8Encoding(false));
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

internal sealed class HosonMesServer
{
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxConcurrentHandlers = 32;
    private volatile TcpListener listener;
    private Thread acceptThread;
    private volatile Dictionary<string, string> responses;
    private volatile Dictionary<string, Dictionary<string, string>> deviceResponses;
    private readonly MesLogWriter logWriter;
    private readonly object clientsLock = new object();
    private readonly HashSet<TcpClient> activeClients = new HashSet<TcpClient>();
    private readonly Semaphore handlerSlots = new Semaphore(MaxConcurrentHandlers, MaxConcurrentHandlers);

    private sealed class HttpRequestInfo
    {
        internal string Method;
        internal string Path;
        internal string Body;
    }

    private sealed class HttpRequestException : Exception
    {
        internal readonly int Status;
        internal HttpRequestException(int status, string message) : base(message) { Status = status; }
    }

    internal event Action<MesExchange> ExchangeReceived;
    internal event Action<Exception> LogWriteFailed;
    internal bool IsRunning { get { return listener != null; } }

    internal HosonMesServer() : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")) { }

    internal HosonMesServer(string logDirectory)
    {
        logWriter = new MesLogWriter(logDirectory);
        logWriter.WriteFailed += delegate(Exception error)
        {
            Action<Exception> handler = LogWriteFailed;
            if (handler != null) handler(error);
        };
        deviceResponses = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    }

    internal void Start(string ipText, int port, Dictionary<string, string> templates)
    {
        Start(ipText, port, templates, new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal));
    }

    internal void Start(string ipText, int port, Dictionary<string, string> templates, Dictionary<string, Dictionary<string, string>> deviceTemplates)
    {
        if (IsRunning) throw new InvalidOperationException("Service is already running.");
        IPAddress ip;
        if (!IPAddress.TryParse(ipText, out ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Enter a valid IPv4 address (0.0.0.0 listens on all interfaces).");
        if (port < 1 || port > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        UpdateResponses(templates);
        UpdateDeviceResponses(deviceTemplates);

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
        MesExchange item = new MesExchange();
        item.Time = DateTime.Now;
        item.ClientIp = "unknown";
        item.DeviceCode = "";
        item.RequestPath = "";
        item.RequestBody = "";
        item.Method = "";
        item.Status = 200;
        NetworkStream stream = null;
        try
        {
            IPEndPoint endpoint = client.Client.RemoteEndPoint as IPEndPoint;
            if (endpoint != null) item.ClientIp = endpoint.Address.ToString();
            stream = client.GetStream();
            stream.ReadTimeout = 10000;
            stream.WriteTimeout = 10000;
            HttpRequestInfo request = ReadRequest(stream);
            item.RequestPath = request.Path;
            item.RequestBody = request.Body;
            if (item.RequestPath.TrimEnd('/') != "/api/mes/invoke")
            {
                item.Status = 404;
                item.ResponseBody = Error("Unknown path");
            }
            else if (request.Method != "POST")
            {
                item.Status = 405;
                item.ResponseBody = Error("Only POST is supported");
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
                    object value;
                    item.Method = requestBody.TryGetValue("method", out value) ? value as string : "";
                    if (string.IsNullOrWhiteSpace(item.Method))
                    {
                        item.Status = 400;
                        item.ResponseBody = Error("Missing method field");
                    }
                    else
                    {
                        item.DeviceCode = DeviceCode(requestBody);
                        string response = MesResponseBuilder.SelectTemplate(deviceResponses, item.DeviceCode, item.Method, responses);
                        if (response != null) item.ResponseBody = MesResponseBuilder.Build(response, requestBody.TryGetValue("id", out value), value);
                        else
                        {
                            item.Status = 404;
                            item.ResponseBody = Error("Unknown method");
                        }
                    }
                }
            }
        }
        catch (HttpRequestException error)
        {
            item.Status = error.Status;
            item.ResponseBody = Error(error.Message);
        }
        catch (Exception)
        {
            item.Status = 500;
            item.ResponseBody = Error("Server error");
        }
        finally
        {
            if (stream != null) WriteResponse(stream, item.Status, item.ResponseBody ?? "");
            CloseTrackedClient(client);
            logWriter.Append(item);
            Action<MesExchange> handler = ExchangeReceived;
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
        Dictionary<string, object> data;
        if (request.TryGetValue("data", out value) && (data = value as Dictionary<string, object>) != null && data.TryGetValue("device_code", out value))
        {
            string nested = Text(value);
            if (!string.IsNullOrWhiteSpace(nested)) return nested;
        }
        return request.TryGetValue("device_code", out value) ? Text(value) : "";
    }

    private static string Text(object value)
    {
        return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
    }

    private static HttpRequestInfo ReadRequest(Stream input)
    {
        byte[] buffer = new byte[4096];
        int headerEnd = -1;
        using (MemoryStream received = new MemoryStream())
        {
            while (headerEnd < 0)
            {
                int count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) throw new HttpRequestException(400, "Incomplete HTTP request.");
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
                    || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
                    throw new HttpRequestException(400, "Duplicate HTTP framing header.");
                values[name] = lines[i].Substring(separator + 1).Trim();
            }

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
                copied += count;
            }

            HttpRequestInfo request = new HttpRequestInfo();
            request.Method = firstLine[0].ToUpperInvariant();
            request.Path = RequestPath(firstLine[1]);
            request.Body = new UTF8Encoding(false, true).GetString(body);
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

    private static void WriteResponse(NetworkStream output, int status, string body)
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
        }
        catch (Exception) { }
    }

    private static void WriteContinue(Stream output)
    {
        byte[] bytes = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
        output.Write(bytes, 0, bytes.Length);
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
