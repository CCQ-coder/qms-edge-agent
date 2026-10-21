using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class BtfHessianClient
{
    internal static byte[] EncodeCall(string method, string argument)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            stream.WriteByte((byte)'c'); stream.WriteByte(1); stream.WriteByte(0);
            stream.WriteByte((byte)'m'); WriteStringBytes(stream, method);
            int offset = 0;
            while (argument.Length - offset > 32767)
            {
                stream.WriteByte((byte)'s');
                WriteStringBytes(stream, argument.Substring(offset, 32767));
                offset += 32767;
            }
            stream.WriteByte((byte)'S');
            WriteStringBytes(stream, argument.Substring(offset));
            stream.WriteByte((byte)'z');
            return stream.ToArray();
        }
    }

    private static void WriteStringBytes(Stream stream, string value)
    {
        if (value.Length > 65535) throw new ArgumentException("Hessian 字符串过长。");
        stream.WriteByte((byte)(value.Length >> 8));
        stream.WriteByte((byte)value.Length);
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    internal static void Send(BtfSettings settings, BtfSnapshot snapshot)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(settings.QmsUrl);
        request.Method = "POST";
        request.ContentType = "application/x-hessian";
        request.Timeout = 10000;
        request.ReadWriteTimeout = 10000;
        request.AllowAutoRedirect = false;
        byte[] body = EncodeCall(settings.QmsMethod, snapshot.ToLegacyJson());
        request.ContentLength = body.Length;
        using (Stream output = request.GetRequestStream()) output.Write(body, 0, body.Length);
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (MemoryStream result = new MemoryStream())
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new IOException("QMS Hessian 返回 HTTP " + (int)response.StatusCode);
            response.GetResponseStream().CopyTo(result);
            byte[] reply = result.ToArray();
            if (reply.Length < 7 || reply[0] != 'r' || reply[3] != 'S')
                throw new IOException("QMS Hessian 未返回成功字符串。");
            int length = reply[4] * 256 + reply[5];
            if (length <= 0 || reply.Length < 6 + length || Encoding.UTF8.GetString(reply, 6, length) != "OK")
                throw new IOException("QMS Hessian 返回内容不是 OK。");
        }
    }
}

internal sealed class BtfQueueWorker : IDisposable
{
    private readonly BtfQueue queue;
    private readonly Action<string> send;
    private readonly Action<string> log;
    private readonly string name;
    private readonly int retrySeconds;
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private Thread thread;
    private volatile bool stopping;

    internal BtfQueueWorker(string path, string name, int retrySeconds, Action<string> send, Action<string> log)
    {
        queue = new BtfQueue(path);
        this.name = name; this.retrySeconds = retrySeconds; this.send = send; this.log = log;
    }

    internal void Start()
    {
        thread = new Thread(Run);
        thread.IsBackground = true;
        thread.Name = name;
        thread.Start();
    }

    internal void Enqueue(string payload) { queue.Enqueue(payload); wake.Set(); }

    private void Run()
    {
        while (!stopping)
        {
            try
            {
                string item = queue.Peek();
                if (item == null) { wake.WaitOne(1000); continue; }
                send(item);
                queue.Acknowledge();
                log(name + " 发送成功");
            }
            catch (Exception error)
            {
                log(name + " 待重试：" + error.Message);
                wake.WaitOne(retrySeconds * 1000);
            }
        }
    }

    public void Dispose()
    {
        stopping = true;
        wake.Set();
        if (thread != null && thread != Thread.CurrentThread) thread.Join(15000);
        queue.Dispose();
        wake.Dispose();
    }
}

internal sealed class BtfDelivery : IDisposable
{
    private readonly BtfSettings settings;
    private readonly Action<string> log;
    private readonly BtfQueueWorker qms;
    private readonly object qmsIntervalLock = new object();
    private readonly Dictionary<string, Tuple<DateTime, string>> lastQms = new Dictionary<string, Tuple<DateTime, string>>(StringComparer.OrdinalIgnoreCase);
    private readonly object s1IntervalLock = new object();
    private readonly Dictionary<string, Tuple<DateTime, string>> lastS1 = new Dictionary<string, Tuple<DateTime, string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BtfQueueWorker> s1 = new Dictionary<string, BtfQueueWorker>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BtfMqttClient> clients = new Dictionary<string, BtfMqttClient>(StringComparer.OrdinalIgnoreCase);
    private readonly BtfQueueWorker unmapped;

    internal BtfDelivery(BtfSettings settings, Action<string> log)
    {
        this.settings = settings; this.log = log;
        try
        {
            if (settings.QmsEnabled)
                qms = new BtfQueueWorker(settings.QmsQueuePath, "QMS", settings.QmsRetrySeconds,
                    delegate(string text) { BtfHessianClient.Send(settings, BtfSnapshot.Deserialize(text)); }, log);
            if (settings.S1Enabled)
            {
                foreach (BtfS1Device device in settings.S1Devices)
                {
                    BtfS1Device target = device;
                    BtfMqttClient client = new BtfMqttClient(settings, target);
                    clients.Add(target.DeviceNumber, client);
                    BtfQueueWorker worker = new BtfQueueWorker(Path.Combine(settings.S1QueuePath, target.DeviceNumber),
                        "S1 " + target.DeviceNumber, settings.S1RetrySeconds,
                        delegate(string text) { BtfMqttMessage message = ParseMessage(text); client.Publish(message); }, log);
                    s1.Add(target.DeviceNumber, worker);
                }
                unmapped = new BtfQueueWorker(Path.Combine(settings.S1QueuePath, "unmapped"), "S1 未映射",
                    settings.S1RetrySeconds, RouteUnmapped, log);
            }
        }
        catch { Dispose(); throw; }
    }

    internal void Start()
    {
        if (qms != null) qms.Start();
        foreach (BtfQueueWorker worker in s1.Values) worker.Start();
        if (unmapped != null) unmapped.Start();
    }

    internal void Enqueue(BtfSnapshot snapshot)
    {
        if (qms != null)
        {
            lock (qmsIntervalLock)
            {
                Tuple<DateTime, string> previous;
                DateTime now = DateTime.UtcNow;
                if (!lastQms.TryGetValue(snapshot.MachineName, out previous)
                    || (now - previous.Item1).TotalSeconds >= settings.QmsReportSeconds
                    || previous.Item2 != snapshot.State)
                {
                    qms.Enqueue(snapshot.Serialize());
                    lastQms[snapshot.MachineName] = Tuple.Create(now, snapshot.State);
                }
            }
        }
        if (!settings.S1Enabled) return;
        lock (s1IntervalLock)
        {
            Tuple<DateTime, string> previous;
            DateTime now = DateTime.UtcNow;
            BtfS1Device device;
            try { device = settings.FindS1Device(snapshot.MachineName); }
            catch (InvalidOperationException)
            {
                unmapped.Enqueue(snapshot.Serialize());
                log("S1 未映射设备 " + snapshot.MachineName + "，已保存待配置后补传。");
                lastS1[snapshot.MachineName] = Tuple.Create(now, snapshot.State);
                return;
            }
            clients[device.DeviceNumber].TouchSource();
            if (lastS1.TryGetValue(snapshot.MachineName, out previous)
                && (now - previous.Item1).TotalSeconds < settings.S1ReportSeconds
                && previous.Item2 == snapshot.State) return;
            EnqueueS1(snapshot, device);
            lastS1[snapshot.MachineName] = Tuple.Create(now, snapshot.State);
        }
    }

    private void EnqueueS1(BtfSnapshot snapshot, BtfS1Device device)
    {
        BtfQueueWorker worker = s1[device.DeviceNumber];
        string productId = settings.ProductIdFor(device);
        worker.Enqueue(SerializeMessage(BtfS1.BuildSnapshot(snapshot, device, productId)));
        if (snapshot.State == "ALAM" && !string.IsNullOrWhiteSpace(snapshot.ScreenText))
            worker.Enqueue(SerializeMessage(BtfS1.BuildAlarm(snapshot, device, productId)));
    }

    private void RouteUnmapped(string text)
    {
        BtfSnapshot snapshot = BtfSnapshot.Deserialize(text);
        BtfS1Device device = settings.FindS1Device(snapshot.MachineName);
        EnqueueS1(snapshot, device);
    }

    private static string SerializeMessage(BtfMqttMessage message)
    {
        return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { {"topic", message.Topic}, {"payload", message.Payload} });
    }

    private static BtfMqttMessage ParseMessage(string text)
    {
        var value = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
        if (value == null || !value.ContainsKey("topic") || !value.ContainsKey("payload")) throw new InvalidDataException("S1 队列记录损坏。");
        return new BtfMqttMessage(Convert.ToString(value["topic"], CultureInfo.InvariantCulture),
            Convert.ToString(value["payload"], CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        if (qms != null) qms.Dispose();
        if (unmapped != null) unmapped.Dispose();
        foreach (BtfQueueWorker worker in s1.Values) worker.Dispose();
        foreach (BtfMqttClient client in clients.Values) client.Dispose();
    }
}
