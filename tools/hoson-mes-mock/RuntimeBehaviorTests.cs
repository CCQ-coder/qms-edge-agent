using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

internal static class RuntimeBehaviorTests
{
    private static int Main()
    {
        string logDirectory = Path.Combine(Path.GetTempPath(), "hoson-runtime-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Type responseBuilder = RequireType("MesResponseBuilder", "response field builder is available");
            Type logWriter = RequireType("MesLogWriter", "persistent log writer is available");

            Dictionary<string, string> generic = new Dictionary<string, string> { { "login", "{\"data\":{\"token\":\"generic\"}}" } };
            Dictionary<string, Dictionary<string, string>> devices = new Dictionary<string, Dictionary<string, string>> {
                { "DEVICE-001", new Dictionary<string, string> { { "login", "{\"data\":{\"token\":\"device\"}}" } } }
            };
            string selected = (string)Invoke(responseBuilder, "SelectTemplate", devices, "DEVICE-001", "login", generic);
            Assert(selected.Contains("device"), "configured device overrides the generic response");
            string fallback = (string)Invoke(responseBuilder, "SelectTemplate", devices, "UNKNOWN", "login", generic);
            Assert(fallback.Contains("generic"), "unknown device uses the generic response");
            Dictionary<string, object> requestWithConflictingDeviceCodes = new Dictionary<string, object> {
                { "device_code", "TOP-LEVEL" },
                { "data", new Dictionary<string, object> { { "device_code", "DEVICE-001" } } }
            };
            Assert((string)Invoke(typeof(HosonMesServer), "DeviceCode", requestWithConflictingDeviceCodes) == "DEVICE-001", "nested device code takes precedence over the top-level compatibility field");
            Dictionary<string, string> genericWithFallback = new Dictionary<string, string> { { "version_info", "{\"data\":{\"source\":\"generic\"}}" } };
            Assert(((string)Invoke(responseBuilder, "SelectTemplate", devices, "DEVICE-001", "version_info", genericWithFallback)).Contains("generic"), "device method gaps use the generic response");

            DateTime before = DateTime.UtcNow;
            string echoed = (string)Build(responseBuilder, "{\"id\":\"old\",\"timestamp\":0}", true, "REQ-42");
            DateTime after = DateTime.UtcNow;
            Dictionary<string, object> echoedBody = Parse(echoed);
            Assert((string)echoedBody["id"] == "REQ-42", "request id is echoed");
            long timestamp = Convert.ToInt64(echoedBody["timestamp"]);
            Assert(timestamp >= UnixMilliseconds(before) && timestamp <= UnixMilliseconds(after), "timestamp uses the current Unix milliseconds");

            Dictionary<string, object> generatedA = Parse((string)Build(responseBuilder, "{\"id\":\"old\",\"timestamp\":0}", false, null));
            Dictionary<string, object> generatedB = Parse((string)Build(responseBuilder, "{\"id\":\"old\",\"timestamp\":0}", false, null));
            Assert(generatedA["id"] is long && generatedB["id"] is long && (long)generatedA["id"] != (long)generatedB["id"], "missing request ids are generated uniquely");
            Dictionary<string, object> nullId = Parse((string)Build(responseBuilder, "{\"id\":\"old\",\"timestamp\":0}", true, null));
            Assert(nullId.ContainsKey("id") && nullId["id"] == null, "explicit null request id is echoed instead of generated");

            Directory.CreateDirectory(logDirectory);
            string deviceConfigPath = Path.Combine(logDirectory, "mes_server_device_config.json");
            File.WriteAllText(deviceConfigPath, "{\"DEVICE-002\":{\"login\":{\"data\":{\"token\":\"from-file\"}}}}");
            Dictionary<string, Dictionary<string, string>> loadedDevices = MesConfig.LoadDeviceResponses(deviceConfigPath);
            Assert(loadedDevices["DEVICE-002"]["login"].Contains("from-file"), "device response config loads nested JSON responses");

            object writer = Activator.CreateInstance(logWriter, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new object[] { logDirectory }, null);
            MesExchange exchange = new MesExchange();
            exchange.Time = DateTime.Now;
            exchange.Method = "login";
            exchange.ClientIp = "10.0.0.8";
            SetField(exchange, "DeviceCode", "DEVICE-001");
            exchange.RequestPath = "/api/mes/invoke";
            exchange.RequestBody = "{\"id\":\"REQ-42\"}";
            exchange.ResponseBody = echoed;
            exchange.Status = 200;
            Assert((bool)InvokeInstance(writer, "Append", exchange), "persistent log append succeeds");
            string[] files = Directory.GetFiles(logDirectory, "*.jsonl");
            Assert(files.Length == 1, "one daily JSONL log file is created");
            Dictionary<string, object> record = Parse(File.ReadAllText(files[0]).Trim());
            Assert((string)record["device_code"] == "DEVICE-001" && (string)record["client_ip"] == "10.0.0.8", "log stores device code and client IP");
            Assert((int)record["status"] == 200, "log stores response status");

            string blockedLogDirectory = Path.Combine(logDirectory, "not-a-directory");
            File.WriteAllText(blockedLogDirectory, "occupied");
            HosonMesServer failingServer = new HosonMesServer(blockedLogDirectory);
            Exception surfacedLogError = null;
            failingServer.LogWriteFailed += delegate(Exception error) { surfacedLogError = error; };
            object failingWriter = GetField(failingServer, "logWriter");
            Assert(!(bool)InvokeInstance(failingWriter, "Append", exchange), "failed log writes are reported to the caller");
            Assert(surfacedLogError != null, "failed log writes surface a server diagnostic");

            Console.WriteLine("Runtime response and persistent log checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
        finally
        {
            if (Directory.Exists(logDirectory)) Directory.Delete(logDirectory, true);
        }
    }

    private static Type RequireType(string name, string message)
    {
        Type type = typeof(HosonMesServer).Assembly.GetType(name, false);
        Assert(type != null, message);
        return type;
    }

    private static object Invoke(Type type, string name, params object[] arguments)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(method != null, name + " is available");
        return method.Invoke(null, arguments);
    }

    private static object Build(Type type, string template, bool hasRequestId, object requestId)
    {
        MethodInfo method = type.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(method != null && method.GetParameters().Length == 3, "response builder distinguishes missing and explicit request ids");
        return method.Invoke(null, new object[] { template, hasRequestId, requestId });
    }

    private static object InvokeInstance(object instance, string name, params object[] arguments)
    {
        MethodInfo method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(method != null, name + " is available");
        return method.Invoke(instance, arguments);
    }

    private static void SetField(object instance, string name, object value)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(field != null, name + " is available");
        field.SetValue(instance, value);
    }

    private static object GetField(object instance, string name)
    {
        FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(field != null, name + " is available");
        return field.GetValue(instance);
    }

    private static Dictionary<string, object> Parse(string json)
    {
        return new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
    }

    private static long UnixMilliseconds(DateTime value)
    {
        return (value.Ticks - 621355968000000000L) / TimeSpan.TicksPerMillisecond;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Failed: " + message);
    }
}
