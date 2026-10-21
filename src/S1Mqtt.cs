using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace JingdeEapForwarder
{
    internal sealed class S1MqttMessage
    {
        public readonly string Topic;
        public readonly string Payload;

        public S1MqttMessage(string topic, string payload)
        {
            Topic = topic;
            Payload = payload;
        }
    }

    internal static class S1MqttTemplate
    {
        public static S1MqttMessage Build(Dictionary<string, object> normalizedEvent, Settings settings)
        {
            if (normalizedEvent == null) throw new ArgumentNullException("normalizedEvent");
            Dictionary<string, object> attributes = GetDictionary(normalizedEvent, "attributes");
            Dictionary<string, object> raw = GetDictionary(attributes, "rawKstPayload");
            string method = GetText(attributes, "kstMethod");
            if (string.IsNullOrEmpty(method)) throw new ProtocolException("S1 MQTT 模板缺少科视方法。");

            string identifier;
            object sourceValue;
            string topicSuffix;
            if (string.Equals(method, "Status", StringComparison.OrdinalIgnoreCase))
            {
                identifier = "kst_status";
                sourceValue = GetRequired(raw, "Status");
                topicSuffix = "/property/post";
            }
            else if (string.Equals(method, "OEE", StringComparison.OrdinalIgnoreCase))
            {
                identifier = "kst_oee";
                sourceValue = GetRequired(raw, "OEE");
                topicSuffix = "/property/post";
            }
            else if (string.Equals(method, "Alarm", StringComparison.OrdinalIgnoreCase))
            {
                identifier = "kst_alarm";
                sourceValue = GetRequired(raw, "EventMessage");
                topicSuffix = "/event/post";
            }
            else if (string.Equals(method, "ExposureResult", StringComparison.OrdinalIgnoreCase))
            {
                identifier = "kst_exposure_result";
                sourceValue = GetRequired(raw, "CRESULT");
                topicSuffix = "/property/post";
            }
            else if (string.Equals(method, "HeartBeat", StringComparison.OrdinalIgnoreCase))
            {
                identifier = "kst_heartbeat";
                sourceValue = GetRequired(raw, "HeartBeat");
                topicSuffix = "/property/post";
            }
            else if (string.Equals(method, "Mode", StringComparison.OrdinalIgnoreCase))
            {
                identifier = "kst_mode";
                sourceValue = GetRequired(raw, "Mode");
                topicSuffix = "/property/post";
            }
            else
            {
                throw new ProtocolException("S1 MQTT 不支持科视方法 " + method + "。");
            }

            Dictionary<string, object> item = new Dictionary<string, object>();
            item["id"] = identifier;
            item["value"] = ToInvariantText(sourceValue);
            item["ts"] = ToS1Timestamp(GetText(normalizedEvent, "deviceTime"));
            item["remark"] = BuildRemark(method, raw);
            ArrayList items = new ArrayList();
            items.Add(item);
            string topic = "/" + settings.S1MqttProductId + "/" + settings.S1MqttDeviceNumber + topicSuffix;
            return new S1MqttMessage(topic, new JavaScriptSerializer().Serialize(items));
        }

        private static string BuildRemark(string method, Dictionary<string, object> raw)
        {
            Dictionary<string, object> details = new Dictionary<string, object>();
            details["method"] = method;
            AddIfPresent(details, raw, "ID");
            AddIfPresent(details, raw, "assets");
            AddIfPresent(details, raw, "MachineNO");
            AddIfPresent(details, raw, "MachineTime");
            AddIfPresent(details, raw, "UserName");
            AddIfPresent(details, raw, "StartTime");
            AddIfPresent(details, raw, "EndTime");
            AddIfPresent(details, raw, "Level");
            AddIfPresent(details, raw, "ItemNO");
            AddIfPresent(details, raw, "LotNO");
            AddIfPresent(details, raw, "CBARCODE");
            AddIfPresent(details, raw, "CORDER_NO");
            AddIfPresent(details, raw, "CITEM_NO");
            AddIfPresent(details, raw, "CLOT_NO");
            AddIfPresent(details, raw, "CLAYER");
            AddIfPresent(details, raw, "CFACE");
            return new JavaScriptSerializer().Serialize(details);
        }

        private static void AddIfPresent(Dictionary<string, object> target, Dictionary<string, object> source, string key)
        {
            object value;
            if (source != null && source.TryGetValue(key, out value) && value != null) target[key] = value;
        }

        private static string ToS1Timestamp(string value)
        {
            DateTime timestamp;
            if (!string.IsNullOrEmpty(value) && DateTime.TryParseExact(value,
                new string[] { "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out timestamp))
            {
                return timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string ToInvariantText(object value)
        {
            IFormattable formattable = value as IFormattable;
            return formattable == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : formattable.ToString(null, CultureInfo.InvariantCulture);
        }

        private static object GetRequired(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null || string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture)))
                throw new ProtocolException("S1 MQTT 模板缺少科视字段 " + key + "。");
            return value;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            Dictionary<string, object> dictionary;
            if (source == null || !source.TryGetValue(key, out value) || (dictionary = value as Dictionary<string, object>) == null)
                throw new ProtocolException("S1 MQTT 模板缺少对象字段 " + key + "。");
            return dictionary;
        }

        private static string GetText(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }
    }

    internal sealed class S1MqttClient
    {
        private readonly Settings settings;
        private readonly object sync = new object();
        private TcpClient socket;
        private NetworkStream stream;
        private ushort nextPacketId = 1;
        private DateTime lastActivity = DateTime.MinValue;

        public S1MqttClient(Settings settings) { this.settings = settings; }

        public void Publish(S1MqttMessage message)
        {
            if (message == null) throw new ArgumentNullException("message");
            lock (sync)
            {
                try
                {
                    EnsureConnected();
                    ushort packetId = NextPacketId();
                    byte[] variableHeader = Combine(EncodeText(message.Topic), new byte[] { (byte)(packetId >> 8), (byte)packetId });
                    byte[] payload = Encoding.UTF8.GetBytes(message.Payload);
                    WritePacket(0x32, Combine(variableHeader, payload));
                    byte packetType;
                    byte[] response = ReadPacket(out packetType);
                    if (packetType != 0x40 || response.Length != 2 || response[0] != (byte)(packetId >> 8) || response[1] != (byte)packetId)
                        throw new IOException("S1 MQTT 未返回匹配的 PUBACK。");
                    lastActivity = DateTime.UtcNow;
                }
                catch
                {
                    CloseUnsafe();
                    throw;
                }
            }
        }

        public void Maintain()
        {
            lock (sync)
            {
                if (stream == null || (DateTime.UtcNow - lastActivity).TotalSeconds < Math.Max(1, settings.S1MqttKeepAliveSeconds / 2)) return;
                try
                {
                    WritePacket(0xC0, new byte[0]);
                    byte packetType;
                    byte[] response = ReadPacket(out packetType);
                    if (packetType != 0xD0 || response.Length != 0) throw new IOException("S1 MQTT PINGRESP 无效。");
                    lastActivity = DateTime.UtcNow;
                }
                catch { CloseUnsafe(); }
            }
        }

        public void Close()
        {
            lock (sync)
            {
                if (stream != null)
                {
                    try { WritePacket(0xE0, new byte[0]); } catch { }
                }
                CloseUnsafe();
            }
        }

        private void EnsureConnected()
        {
            if (socket != null && socket.Connected && stream != null) return;
            CloseUnsafe();
            socket = new TcpClient();
            IAsyncResult connect = socket.BeginConnect(settings.S1MqttHost, settings.S1MqttPort, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(settings.RequestTimeoutSeconds * 1000))
            {
                CloseUnsafe();
                throw new TimeoutException("连接 S1 MQTT 超时。");
            }
            socket.EndConnect(connect);
            socket.NoDelay = true;
            stream = socket.GetStream();
            stream.ReadTimeout = settings.RequestTimeoutSeconds * 1000;
            stream.WriteTimeout = settings.RequestTimeoutSeconds * 1000;

            byte flags = 0x02;
            if (!string.IsNullOrEmpty(settings.S1MqttUsername)) flags |= 0x80;
            if (!string.IsNullOrEmpty(settings.S1MqttPassword)) flags |= 0x40;
            byte[] variableHeader = Combine(EncodeText("MQTT"), new byte[] { 0x04, flags,
                (byte)(settings.S1MqttKeepAliveSeconds >> 8), (byte)settings.S1MqttKeepAliveSeconds });
            byte[] payload = EncodeText(settings.S1MqttClientId());
            if ((flags & 0x80) != 0) payload = Combine(payload, EncodeText(settings.S1MqttUsername));
            if ((flags & 0x40) != 0) payload = Combine(payload, EncodeText(settings.S1MqttPassword));
            WritePacket(0x10, Combine(variableHeader, payload));
            byte packetType;
            byte[] response = ReadPacket(out packetType);
            if (packetType != 0x20 || response.Length != 2) throw new IOException("S1 MQTT CONNACK 无效。");
            if (response[1] != 0) throw new IOException("S1 MQTT 拒绝连接，返回码 " + response[1] + "。");
            lastActivity = DateTime.UtcNow;
        }

        private ushort NextPacketId()
        {
            ushort value = nextPacketId++;
            if (nextPacketId == 0) nextPacketId = 1;
            return value;
        }

        private void WritePacket(byte header, byte[] body)
        {
            if (stream == null) throw new IOException("S1 MQTT 尚未连接。");
            stream.WriteByte(header);
            int remaining = body.Length;
            do
            {
                int digit = remaining % 128;
                remaining /= 128;
                if (remaining > 0) digit |= 128;
                stream.WriteByte((byte)digit);
            } while (remaining > 0);
            if (body.Length > 0) stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private byte[] ReadPacket(out byte packetType)
        {
            int first = stream.ReadByte();
            if (first < 0) throw new EndOfStreamException("S1 MQTT 连接已关闭。");
            packetType = (byte)(first & 0xF0);
            int multiplier = 1;
            int length = 0;
            int digit;
            do
            {
                digit = stream.ReadByte();
                if (digit < 0) throw new EndOfStreamException("S1 MQTT 报文长度不完整。");
                length += (digit & 127) * multiplier;
                if (multiplier > 128 * 128 * 128) throw new IOException("S1 MQTT 报文长度无效。");
                multiplier *= 128;
            } while ((digit & 128) != 0);
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(data, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException("S1 MQTT 报文不完整。");
                offset += read;
            }
            return data;
        }

        private static byte[] EncodeText(string value)
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

        private void CloseUnsafe()
        {
            try { if (stream != null) stream.Close(); } catch { }
            try { if (socket != null) socket.Close(); } catch { }
            stream = null;
            socket = null;
            lastActivity = DateTime.MinValue;
        }
    }

    internal sealed class S1MqttUploader
    {
        private readonly PersistentQueue queue;
        private readonly S1MqttClient client;
        private readonly Settings settings;
        private readonly GatewayLog log;
        private readonly Action<GatewayFlowEvent> publish;
        private readonly AutoResetEvent wakeSignal = new AutoResetEvent(false);
        private readonly object flushSync = new object();

        public S1MqttUploader(PersistentQueue queue, S1MqttClient client, Settings settings, GatewayLog log, Action<GatewayFlowEvent> publish)
        {
            this.queue = queue;
            this.client = client;
            this.settings = settings;
            this.log = log;
            this.publish = publish;
            queue.ItemEnqueued += Signal;
        }

        public void Run(Func<bool> shouldStop)
        {
            try
            {
                while (!shouldStop())
                {
                    FlushOnce();
                    client.Maintain();
                    wakeSignal.WaitOne(settings.RetryIntervalSeconds * 1000);
                }
            }
            finally { client.Close(); }
        }

        public void FlushOnce()
        {
            lock (flushSync)
            {
                while (queue.Count > 0)
                {
                    Dictionary<string, object> item = queue.Peek(1)[0];
                    S1MqttMessage message;
                    try
                    {
                        message = S1MqttTemplate.Build(item, settings);
                        client.Publish(message);
                        queue.RemoveFirst(1);
                        log.Info("Forwarded KST event to S1 MQTT. Topic=" + message.Topic + ", Pending=" + queue.Count);
                        Publish(new GatewayFlowEvent {
                            Stage = "S1 转发成功", Source = settings.S1MqttHost + ":" + settings.S1MqttPort,
                            Method = message.Topic, Equipment = settings.S1MqttDeviceNumber,
                            Summary = "已通过 MQTT QoS 1 提交到 S1；待转发 " + queue.Count + " 条。",
                            NormalizedPayload = message.Payload
                        });
                    }
                    catch (Exception exception)
                    {
                        log.Warn("S1 MQTT is unavailable; retained " + queue.Count + " pending event(s). " + exception.Message);
                        Publish(new GatewayFlowEvent {
                            Stage = "S1 转发待重试", Source = settings.S1MqttHost + ":" + settings.S1MqttPort,
                            Equipment = settings.S1MqttDeviceNumber,
                            Summary = "S1 MQTT 暂不可用，已保留 " + queue.Count + " 条：" + exception.Message
                        });
                        return;
                    }
                }
            }
        }

        public void Stop() { wakeSignal.Set(); }

        private void Signal() { wakeSignal.Set(); }
        private void Publish(GatewayFlowEvent flow) { if (publish != null) publish(flow); }
    }
}
