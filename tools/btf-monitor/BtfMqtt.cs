using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class BtfMqttMessage
{
    internal string Topic;
    internal string Payload;
    internal BtfMqttMessage(string topic, string payload) { Topic = topic; Payload = payload; }
}

internal static class BtfS1
{
    internal static BtfMqttMessage BuildSnapshot(BtfSnapshot snapshot, BtfS1Device device, string productId)
    {
        var items = new ArrayList();
        Add(items, "btf_status", snapshot.State, snapshot);
        Add(items, "btf_program", snapshot.ProgramName ?? "", snapshot);
        Add(items, "btf_operator", snapshot.OperatorName ?? "", snapshot);
        Add(items, "btf_work_count", snapshot.WorkCount, snapshot);
        Add(items, "btf_boot_seconds", snapshot.BootSeconds, snapshot);
        Add(items, "btf_work_seconds", snapshot.WorkSeconds, snapshot);
        Add(items, "btf_finished_count", snapshot.FinishedCount, snapshot);
        Add(items, "btf_progress", snapshot.ProgressPercent, snapshot);
        Add(items, "btf_route_length", snapshot.RouteLength, snapshot);
        Add(items, "btf_spindle_status", snapshot.SpindleStatus ?? "", snapshot);
        return new BtfMqttMessage("/" + productId + "/" + device.DeviceNumber + "/property/post",
            new JavaScriptSerializer().Serialize(items));
    }

    internal static BtfMqttMessage BuildAlarm(BtfSnapshot snapshot, BtfS1Device device, string productId)
    {
        var items = new ArrayList();
        Add(items, "btf_alarm", snapshot.ScreenText ?? "", snapshot);
        return new BtfMqttMessage("/" + productId + "/" + device.DeviceNumber + "/event/post",
            new JavaScriptSerializer().Serialize(items));
    }

    private static void Add(ArrayList items, string id, object value, BtfSnapshot snapshot)
    {
        var item = new Dictionary<string, object>();
        item["id"] = id;
        IFormattable formattable = value as IFormattable;
        item["value"] = formattable == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : formattable.ToString(null, CultureInfo.InvariantCulture);
        item["ts"] = snapshot.DeviceTime;
        item["remark"] = new JavaScriptSerializer().Serialize(new Dictionary<string, object> { {"machinename", snapshot.MachineName} });
        items.Add(item);
    }
}

internal sealed class BtfMqttClient : IDisposable
{
    private readonly BtfSettings settings;
    private readonly BtfS1Device device;
    private readonly object sync = new object();
    private readonly Timer heartbeat;
    private TcpClient socket;
    private NetworkStream stream;
    private ushort packetId = 1;
    private long lastSourceTicks;
    private long lastPacketTicks;
    private bool disposed;

    internal BtfMqttClient(BtfSettings settings, BtfS1Device device)
    {
        this.settings = settings; this.device = device;
        int interval = Math.Max(1000, Math.Min(10000, settings.S1KeepAliveSeconds * 1000 / 3));
        heartbeat = new Timer(KeepAlive, null, interval, interval);
    }

    internal void TouchSource() { Interlocked.Exchange(ref lastSourceTicks, DateTime.UtcNow.Ticks); }

    internal void Publish(BtfMqttMessage message)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException("BtfMqttClient");
            try
            {
                Connect();
                ushort id = packetId++;
                if (packetId == 0) packetId = 1;
                byte[] header = Combine(Encode(message.Topic), new byte[] { (byte)(id >> 8), (byte)id });
                Write(0x32, Combine(header, Encoding.UTF8.GetBytes(message.Payload)));
                byte type;
                byte[] ack = Read(out type);
                if (type != 0x40 || ack.Length != 2 || ack[0] != (byte)(id >> 8) || ack[1] != (byte)id)
                    throw new IOException("S1 未返回匹配的 MQTT PUBACK。");
                lastPacketTicks = DateTime.UtcNow.Ticks;
            }
            catch { Close(); throw; }
        }
    }

    private void KeepAlive(object unused)
    {
        if (!Monitor.TryEnter(sync)) return;
        try
        {
            if (disposed) return;
            long seen = Interlocked.Read(ref lastSourceTicks);
            long now = DateTime.UtcNow.Ticks;
            long sourceGrace = Math.Max(settings.IdleSeconds, settings.S1KeepAliveSeconds * 2) * TimeSpan.TicksPerSecond;
            if (seen == 0 || now - seen >= sourceGrace)
            {
                if (stream != null) Close();
                return;
            }
            try
            {
                Connect();
                if (DateTime.UtcNow.Ticks - lastPacketTicks < settings.S1KeepAliveSeconds * TimeSpan.TicksPerSecond / 2) return;
                Write(0xC0, new byte[0]);
                byte type;
                byte[] reply = Read(out type);
                if (type != 0xD0 || reply.Length != 0) throw new IOException("S1 未返回 MQTT PINGRESP。");
                lastPacketTicks = DateTime.UtcNow.Ticks;
            }
            catch { Close(); }
        }
        finally { Monitor.Exit(sync); }
    }

    private void Connect()
    {
        if (socket != null && socket.Connected && stream != null) return;
        Close();
        socket = new TcpClient();
        IAsyncResult pending = socket.BeginConnect(settings.S1Host, settings.S1Port, null, null);
        try
        {
            if (!pending.AsyncWaitHandle.WaitOne(settings.S1TimeoutSeconds * 1000)) throw new TimeoutException("连接 S1 Broker 超时。");
            socket.EndConnect(pending);
        }
        finally { pending.AsyncWaitHandle.Close(); }
        socket.NoDelay = true;
        stream = socket.GetStream();
        stream.ReadTimeout = settings.S1TimeoutSeconds * 1000;
        stream.WriteTimeout = settings.S1TimeoutSeconds * 1000;
        byte[] variable = Combine(Encode("MQTT"), new byte[] { 0x04, 0xC2,
            (byte)(settings.S1KeepAliveSeconds >> 8), (byte)settings.S1KeepAliveSeconds });
        string clientId = ClientId(settings, device);
        byte[] payload = Combine(Combine(Encode(clientId), Encode(device.Username)), Encode(device.Password));
        Write(0x10, Combine(variable, payload));
        byte type;
        byte[] ack = Read(out type);
        if (type != 0x20 || ack.Length != 2 || ack[1] != 0) throw new IOException("S1 MQTT CONNECT 被拒绝。");
        lastPacketTicks = DateTime.UtcNow.Ticks;
    }

    internal static string ClientId(BtfSettings settings, BtfS1Device device)
    { return "S&" + device.DeviceNumber + "&" + settings.ProductIdFor(device) + "&" + settings.UserIdFor(device); }

    private void Write(byte header, byte[] body)
    {
        stream.WriteByte(header);
        int remaining = body.Length;
        do
        {
            int digit = remaining % 128;
            remaining /= 128;
            if (remaining > 0) digit |= 128;
            stream.WriteByte((byte)digit);
        } while (remaining > 0);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private byte[] Read(out byte type)
    {
        int first = stream.ReadByte();
        if (first < 0) throw new EndOfStreamException();
        type = (byte)(first & 0xF0);
        int length = 0, multiplier = 1, digit;
        do
        {
            digit = stream.ReadByte();
            if (digit < 0) throw new EndOfStreamException();
            length += (digit & 127) * multiplier;
            if (multiplier > 128 * 128 * 128) throw new IOException("MQTT 报文长度无效。");
            multiplier *= 128;
        } while ((digit & 128) != 0);
        byte[] body = new byte[length];
        int offset = 0;
        while (offset < body.Length)
        {
            int count = stream.Read(body, offset, body.Length - offset);
            if (count <= 0) throw new EndOfStreamException();
            offset += count;
        }
        return body;
    }

    private static byte[] Encode(string value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value ?? "");
        if (text.Length > 65535) throw new ArgumentException("MQTT 文本字段过长。");
        byte[] result = new byte[text.Length + 2];
        result[0] = (byte)(text.Length >> 8);
        result[1] = (byte)text.Length;
        Buffer.BlockCopy(text, 0, result, 2, text.Length);
        return result;
    }

    private static byte[] Combine(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private void Close()
    {
        try { if (stream != null) Write(0xE0, new byte[0]); } catch { }
        try { if (stream != null) stream.Close(); } catch { }
        try { if (socket != null) socket.Close(); } catch { }
        stream = null; socket = null;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            heartbeat.Dispose();
            Close();
        }
    }
}
