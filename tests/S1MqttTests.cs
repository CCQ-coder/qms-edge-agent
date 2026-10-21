using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace JingdeEapForwarder
{
    internal static class S1MqttTests
    {
        private static int Main()
        {
            try
            {
                TestS1DeviceNumberValidation();
                TestActualKstOeeTemplate();
                TestChineseAlarmTemplate();
                TestActualStatusModeAndHeartTemplates();
                TestPersistentS1QueueReload();
                TestMqttConnectAndQos1Publish();
                Console.WriteLine("S1 MQTT TESTS PASSED");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("S1 MQTT TESTS FAILED: " + exception);
                return 1;
            }
        }

        private static void TestActualKstOeeTemplate()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> payload = serializer.DeserializeObject("{\"function\":\"duty\",\"assets\":\"KST-001\",\"OEE\":0.0715467513303224,\"StartTime\":\"2026-10-17 08:00:00\",\"EndTime\":\"2026-10-17 20:00:00\",\"MachineTime\":\"2026-10-17 09:41:39\",\"UserName\":\"\",\"LotNO\":\"\",\"CBARCODE\":\"unknown\"}") as Dictionary<string, object>;
            Dictionary<string, object> normalized = KstTranslator.Translate("OEE", payload, "192.168.10.1", DateTime.Now);
            S1MqttMessage message = S1MqttTemplate.Build(normalized, SettingsForTest(1883));
            AssertEqual("/88/KST001/property/post", message.Topic, "OEE topic");
            IList items = serializer.DeserializeObject(message.Payload) as IList;
            Assert(items != null && items.Count >= 1, "OEE payload must be a JSON array");
            Dictionary<string, object> first = items[0] as Dictionary<string, object>;
            AssertEqual("kst_oee", Convert.ToString(first["id"], CultureInfo.InvariantCulture), "OEE identifier");
            AssertEqual("0.0715467513303224", Convert.ToString(first["value"], CultureInfo.InvariantCulture), "OEE value");
            AssertEqual("2026-10-17 09:41:39", Convert.ToString(first["ts"], CultureInfo.InvariantCulture), "OEE timestamp");
            Assert(Convert.ToString(first["remark"], CultureInfo.InvariantCulture).Contains("\"assets\":\"KST-001\""), "raw KST asset remains in remark");
        }

        private static void TestChineseAlarmTemplate()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> payload = serializer.DeserializeObject("{\"function\":\"alarm\",\"assets\":\"KST-001\",\"UserName\":\"\",\"EventMessage\":\"正在中止线程。\",\"Level\":\"1\",\"StartTime\":\"2026-10-17 09:39:28\",\"EndTime\":\"2026-10-17 09:39:38\",\"ItemNO\":\"\",\"LotNO\":\"\",\"CBARCODE\":\"unknown\",\"ID\":\"202610170939285864\",\"MachineTime\":\"2026-10-17 09:39:28\"}") as Dictionary<string, object>;
            Dictionary<string, object> normalized = KstTranslator.Translate("Alarm", payload, "192.168.10.1", DateTime.Now);
            S1MqttMessage message = S1MqttTemplate.Build(normalized, SettingsForTest(1883));
            AssertEqual("/88/KST001/event/post", message.Topic, "Alarm topic");
            Assert(message.Payload.IndexOf("正在中止线程。", StringComparison.Ordinal) >= 0, "Chinese alarm text must remain UTF-8 JSON text");
        }

        private static void TestMqttConnectAndQos1Publish()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            FakeBroker broker = new FakeBroker(listener);
            Thread thread = new Thread(new ThreadStart(broker.Run));
            thread.IsBackground = true;
            thread.Start();

            Settings settings = SettingsForTest(port);
            S1MqttClient client = new S1MqttClient(settings);
            try
            {
                client.Publish(new S1MqttMessage("/88/KST001/property/post", "[{\"id\":\"kst_heartbeat\",\"value\":\"1\",\"remark\":\"心跳\"}]"));
            }
            finally
            {
                client.Close();
            }
            thread.Join(3000);
            listener.Stop();
            if (broker.Error != null) throw broker.Error;
            AssertEqual("S&KST001&88&9", broker.ClientId, "MQTT client id");
            AssertEqual("device-user", broker.Username, "MQTT username");
            AssertEqual("device-password", broker.Password, "MQTT password");
            AssertEqual("/88/KST001/property/post", broker.Topic, "published topic");
            Assert(broker.Payload.IndexOf("心跳", StringComparison.Ordinal) >= 0, "published payload must be UTF-8");
        }

        private static void TestActualStatusModeAndHeartTemplates()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> status = serializer.DeserializeObject("{\"function\":\"status\",\"assets\":\"KST-001\",\"Status\":\"ALARM\",\"MachineTime\":\"2026-10-17 09:39:28\",\"UserName\":\"\",\"LotNO\":\"\",\"CBARCODE\":\"unknown\"}") as Dictionary<string, object>;
            Dictionary<string, object> mode = serializer.DeserializeObject("{\"function\":\"mode\",\"assets\":\"KST-001\",\"ID\":\"202610170940299512\",\"Mode\":\"EAP\",\"MachineTime\":\"2026-10-17 09:40:29\",\"UserName\":\"\",\"LotNO\":\"\",\"CBARCODE\":\"unknown\"}") as Dictionary<string, object>;
            Dictionary<string, object> heart = serializer.DeserializeObject("{\"ID\":\"202610170939528378\",\"MachineNO\":\"KST-001\",\"HeartBeat\":1,\"MachineTime\":\"2026-10-17 09:39:52\",\"function\":\"heartbeat\",\"assets\":\"KST-001\"}") as Dictionary<string, object>;
            Settings settings = SettingsForTest(1883);

            AssertEqual("HeartBeat", KstTranslator.MethodFromPath("/Heart"), "new heartbeat path");
            AssertEqual("HeartBeat", KstTranslator.MethodFromPath("/HeartBeat"), "legacy heartbeat path");
            Assert(S1MqttTemplate.Build(KstTranslator.Translate("Status", status, "192.168.10.1", DateTime.Now), settings).Payload.IndexOf("kst_status", StringComparison.Ordinal) >= 0, "status template");
            Assert(S1MqttTemplate.Build(KstTranslator.Translate("Mode", mode, "192.168.10.1", DateTime.Now), settings).Payload.IndexOf("kst_mode", StringComparison.Ordinal) >= 0, "mode template");
            Assert(S1MqttTemplate.Build(KstTranslator.Translate("HeartBeat", heart, "192.168.10.1", DateTime.Now), settings).Payload.IndexOf("kst_heartbeat", StringComparison.Ordinal) >= 0, "heart template");
        }

        private static Settings SettingsForTest(int port)
        {
            Settings settings = new Settings();
            settings.S1MqttHost = "127.0.0.1";
            settings.S1MqttPort = port;
            settings.S1MqttProductId = "88";
            settings.S1MqttDeviceNumber = "KST001";
            settings.S1MqttUserId = "9";
            settings.S1MqttUsername = "device-user";
            settings.S1MqttPassword = "device-password";
            settings.S1MqttKeepAliveSeconds = 30;
            settings.RequestTimeoutSeconds = 3;
            return settings;
        }

        private static void TestS1DeviceNumberValidation()
        {
            Settings settings = SettingsForTest(1883);
            settings.QmsBaseUrl = "http://127.0.0.1:8000";
            settings.GatewayToken = "test-token";
            settings.S1MqttEnabled = true;
            settings.Validate();
            settings.S1MqttDeviceNumber = "KST-001";
            bool rejected = false;
            try { settings.Validate(); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "S1 device number must reject a hyphen");
        }

        private static void TestPersistentS1QueueReload()
        {
            string directory = Path.Combine(Path.GetTempPath(), "s1-mqtt-queue-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string queuePath = Path.Combine(directory, "pending.json");
                GatewayLog log = new GatewayLog(Path.Combine(directory, "test.log"), 1);
                PersistentQueue queue = new PersistentQueue(queuePath, log);
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["recordNo"] = "KST-test-1";
                queue.Enqueue(item);
                PersistentQueue restored = new PersistentQueue(queuePath, log);
                Assert(restored.Count == 1, "persistent S1 queue must reload a serialized JSON array");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void AssertEqual(string expected, string actual, string label)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new Exception(label + ": expected " + expected + ", actual " + actual);
        }

        private sealed class FakeBroker
        {
            private readonly TcpListener listener;
            public string ClientId;
            public string Username;
            public string Password;
            public string Topic;
            public string Payload;
            public Exception Error;

            public FakeBroker(TcpListener listener) { this.listener = listener; }

            public void Run()
            {
                try
                {
                    using (TcpClient socket = listener.AcceptTcpClient())
                    using (NetworkStream stream = socket.GetStream())
                    {
                        byte header;
                        byte[] connect = ReadPacket(stream, out header);
                        if (header != 0x10) throw new Exception("Expected CONNECT packet");
                        int index = 0;
                        ReadText(connect, ref index); // protocol name
                        index++; // protocol level
                        byte flags = connect[index++];
                        index += 2; // keepalive
                        ClientId = ReadText(connect, ref index);
                        if ((flags & 0x80) != 0) Username = ReadText(connect, ref index);
                        if ((flags & 0x40) != 0) Password = ReadText(connect, ref index);
                        stream.Write(new byte[] { 0x20, 0x02, 0x00, 0x00 }, 0, 4);

                        byte[] publish = ReadPacket(stream, out header);
                        if (header != 0x32) throw new Exception("Expected QoS 1 PUBLISH packet");
                        index = 0;
                        Topic = ReadText(publish, ref index);
                        int packetId = (publish[index++] << 8) | publish[index++];
                        Payload = Encoding.UTF8.GetString(publish, index, publish.Length - index);
                        stream.Write(new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)packetId }, 0, 4);
                    }
                }
                catch (Exception exception) { Error = exception; }
            }

            private static byte[] ReadPacket(Stream stream, out byte header)
            {
                int first = stream.ReadByte();
                if (first < 0) throw new EndOfStreamException();
                header = (byte)first;
                int multiplier = 1;
                int length = 0;
                int digit;
                do
                {
                    digit = stream.ReadByte();
                    if (digit < 0) throw new EndOfStreamException();
                    length += (digit & 127) * multiplier;
                    multiplier *= 128;
                } while ((digit & 128) != 0);
                byte[] data = new byte[length];
                int offset = 0;
                while (offset < length)
                {
                    int count = stream.Read(data, offset, length - offset);
                    if (count <= 0) throw new EndOfStreamException();
                    offset += count;
                }
                return data;
            }

            private static string ReadText(byte[] data, ref int index)
            {
                int length = (data[index++] << 8) | data[index++];
                string value = Encoding.UTF8.GetString(data, index, length);
                index += length;
                return value;
            }
        }
    }
}
