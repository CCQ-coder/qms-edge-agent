using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class BtfS1Device
{
    internal string SourceMachine;
    internal string DeviceNumber;
    internal string ProductId;
    internal string UserId;
    internal string Username;
    internal string Password;
}

internal sealed class BtfSettings
{
    internal string ListenHost = "0.0.0.0";
    internal int ListenPort = 6100;
    internal int Columns = 2;
    internal int Rows = 2;
    internal int RetentionDays = 30;
    internal int IdleSeconds = 120;
    internal bool QmsEnabled;
    internal string QmsUrl = "http://127.0.0.1:8080/hessian/monitor";
    internal string QmsMethod = "Monitor";
    internal int QmsReportSeconds = 1;
    internal int QmsRetrySeconds = 10;
    internal bool S1Enabled;
    internal string S1Host = "171.35.40.79";
    internal int S1Port = 1883;
    internal string S1ProductId = "";
    internal string S1UserId = "";
    internal int S1ReportSeconds = 1;
    internal int S1RetrySeconds = 10;
    internal int S1TimeoutSeconds = 10;
    internal int S1KeepAliveSeconds = 30;
    internal List<BtfS1Device> S1Devices = new List<BtfS1Device>();
    internal string BaseDirectory;

    internal string QmsQueuePath { get { return Path.Combine(BaseDirectory, "data", "pending-qms"); } }
    internal string S1QueuePath { get { return Path.Combine(BaseDirectory, "data", "pending-s1"); } }

    internal static BtfSettings Load(string path)
    {
        BtfSettings result = new BtfSettings();
        result.BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!File.Exists(path)) { result.Save(path); return result; }
        var map = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
        if (map == null) throw new InvalidDataException("标特福配置必须为 JSON 对象。");
        result.ListenHost = Text(map, "listen_host", result.ListenHost);
        result.ListenPort = Number(map, "listen_port", result.ListenPort);
        result.Columns = Number(map, "columns", result.Columns);
        result.Rows = Number(map, "rows", result.Rows);
        result.RetentionDays = Number(map, "retention_days", result.RetentionDays);
        result.IdleSeconds = Number(map, "idle_seconds", result.IdleSeconds);
        result.QmsEnabled = Bool(map, "qms_enabled", result.QmsEnabled);
        result.QmsUrl = Text(map, "qms_url", result.QmsUrl);
        result.QmsMethod = Text(map, "qms_method", result.QmsMethod);
        result.QmsReportSeconds = Number(map, "qms_report_seconds", result.QmsReportSeconds);
        result.QmsRetrySeconds = Number(map, "qms_retry_seconds", result.QmsRetrySeconds);
        result.S1Enabled = Bool(map, "s1_enabled", result.S1Enabled);
        result.S1Host = Text(map, "s1_host", result.S1Host);
        result.S1Port = Number(map, "s1_port", result.S1Port);
        result.S1ProductId = Text(map, "s1_product_id", result.S1ProductId);
        result.S1UserId = Text(map, "s1_user_id", result.S1UserId);
        result.S1ReportSeconds = Number(map, "s1_report_seconds", result.S1ReportSeconds);
        result.S1RetrySeconds = Number(map, "s1_retry_seconds", result.S1RetrySeconds);
        result.S1TimeoutSeconds = Number(map, "s1_timeout_seconds", result.S1TimeoutSeconds);
        result.S1KeepAliveSeconds = Number(map, "s1_keep_alive_seconds", result.S1KeepAliveSeconds);
        object raw;
        if (map.TryGetValue("s1_devices", out raw) && raw != null)
        {
            IList entries = raw as IList;
            if (entries == null) throw new InvalidDataException("s1_devices 必须为数组。");
            foreach (object item in entries)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null) throw new InvalidDataException("s1_devices 每项必须为对象。");
                result.S1Devices.Add(new BtfS1Device {
                    SourceMachine = Text(entry, "source_machine", ""), DeviceNumber = Text(entry, "device_number", ""),
                    ProductId = Text(entry, "product_id", ""), UserId = Text(entry, "user_id", ""),
                    Username = Text(entry, "username", ""), Password = Text(entry, "password", "")
                });
            }
        }
        result.Validate();
        return result;
    }

    internal void Validate()
    {
        IPAddress ignored;
        if (!IPAddress.TryParse(ListenHost, out ignored)) throw new ArgumentException("监听 IP 必须是具体 IPv4 或 IPv6 地址。");
        Port(ListenPort, "监听端口");
        if (Columns < 1 || Columns > 6 || Rows < 1 || Rows > 12) throw new ArgumentException("显示列数或行数超出范围。");
        if (RetentionDays < 1 || RetentionDays > 3650 || IdleSeconds < 1 || IdleSeconds > 3600)
            throw new ArgumentException("记录保留天数或空闲断线秒数超出范围。");
        if (QmsReportSeconds < 1 || QmsReportSeconds > 3600)
            throw new ArgumentException("QMS 上报间隔必须为 1 至 3600 秒。");
        if (S1ReportSeconds < 1 || S1ReportSeconds > 3600)
            throw new ArgumentException("S1 上报间隔必须为 1 至 3600 秒。");
        if (QmsRetrySeconds < 1 || QmsRetrySeconds > 3600 || S1RetrySeconds < 1 || S1RetrySeconds > 3600)
            throw new ArgumentException("重试间隔必须为 1 至 3600 秒。");
        if (QmsEnabled)
        {
            Uri uri;
            if (!Uri.TryCreate(QmsUrl, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new ArgumentException("QMS/ERP 地址必须是完整的 HTTP(S) URL。");
            if (QmsMethod != "Monitor") throw new ArgumentException("QMS Hessian 方法必须为 Monitor。");
        }
        Port(S1Port, "S1 MQTT 端口");
        if (S1TimeoutSeconds < 1 || S1TimeoutSeconds > 300 || S1KeepAliveSeconds < 5 || S1KeepAliveSeconds > 65535)
            throw new ArgumentException("S1 超时或保活时间超出范围。");
        if (!S1Enabled) return;
        if (string.IsNullOrWhiteSpace(S1Host)) throw new ArgumentException("启用 S1 时必须填写 Broker 主机。");
        if (S1Devices.Count == 0) throw new ArgumentException("启用 S1 时至少配置一台设备映射。");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BtfS1Device device in S1Devices)
        {
            if (string.IsNullOrWhiteSpace(device.SourceMachine) || string.IsNullOrWhiteSpace(device.DeviceNumber)
                || string.IsNullOrWhiteSpace(device.Username) || string.IsNullOrEmpty(device.Password))
                throw new ArgumentException("S1 每台设备必须填写现场名称、设备编号、用户名和密码。");
            PositiveId(ProductIdFor(device), "S1 产品 ID");
            PositiveId(UserIdFor(device), "S1 用户 ID");
            foreach (char ch in device.DeviceNumber)
                if (!char.IsLetterOrDigit(ch) || ch > 127) throw new ArgumentException("S1 设备编号只能使用英文字母和数字。");
            if (!sources.Add(NormalizeSourceMachine(device.SourceMachine)) || !targets.Add(device.DeviceNumber.Trim()))
                throw new ArgumentException("S1 现场设备名称或设备编号重复。");
        }
    }

    internal string ProductIdFor(BtfS1Device device)
    { return string.IsNullOrWhiteSpace(device.ProductId) ? S1ProductId.Trim() : device.ProductId.Trim(); }

    internal string UserIdFor(BtfS1Device device)
    { return string.IsNullOrWhiteSpace(device.UserId) ? S1UserId.Trim() : device.UserId.Trim(); }

    private static void PositiveId(string value, string name)
    {
        long number;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number <= 0)
            throw new ArgumentException(name + "必须为正整数；可在默认值或设备行中填写。");
    }

    internal BtfS1Device FindS1Device(string machine)
    {
        foreach (BtfS1Device device in S1Devices)
            if (string.Equals(device.SourceMachine.Trim(), (machine ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return device;
        string normalized = NormalizeSourceMachine(machine);
        foreach (BtfS1Device device in S1Devices)
            if (string.Equals(NormalizeSourceMachine(device.SourceMachine), normalized, StringComparison.OrdinalIgnoreCase)) return device;
        throw new InvalidOperationException("设备 " + machine + " 未配置 S1 设备映射。");
    }

    private static string NormalizeSourceMachine(string value)
    {
        string name = (value ?? "").Trim();
        if (!name.StartsWith("CNC", StringComparison.OrdinalIgnoreCase)) return name;
        string number = name.Substring(3).Trim();
        if (number.StartsWith("#") || number.StartsWith("-")) number = number.Substring(1).Trim();
        int separator = number.IndexOf('_');
        if (separator >= 0)
        {
            int window;
            if (!int.TryParse(number.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out window) || window < 1)
                return name;
            number = number.Substring(0, separator);
        }
        int index;
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 1 || index > 99)
            return name;
        return "CNC" + index.ToString("00", CultureInfo.InvariantCulture);
    }

    internal void Save(string path)
    {
        BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        Validate();
        var values = new Dictionary<string, object> {
            {"listen_host", ListenHost}, {"listen_port", ListenPort}, {"columns", Columns}, {"rows", Rows},
            {"retention_days", RetentionDays}, {"idle_seconds", IdleSeconds},
            {"qms_enabled", QmsEnabled}, {"qms_url", QmsUrl}, {"qms_method", QmsMethod},
            {"qms_report_seconds", QmsReportSeconds}, {"qms_retry_seconds", QmsRetrySeconds},
            {"s1_enabled", S1Enabled}, {"s1_host", S1Host}, {"s1_port", S1Port},
            {"s1_product_id", S1ProductId}, {"s1_user_id", S1UserId},
            {"s1_report_seconds", S1ReportSeconds}, {"s1_retry_seconds", S1RetrySeconds}, {"s1_timeout_seconds", S1TimeoutSeconds},
            {"s1_keep_alive_seconds", S1KeepAliveSeconds}
        };
        ArrayList devices = new ArrayList();
        foreach (BtfS1Device device in S1Devices)
            devices.Add(new Dictionary<string, object> { {"source_machine", device.SourceMachine}, {"device_number", device.DeviceNumber},
                {"product_id", device.ProductId ?? ""}, {"user_id", device.UserId ?? ""},
                {"username", device.Username}, {"password", device.Password} });
        values["s1_devices"] = devices;
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(values), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
    }

    private static void Port(int value, string name) { if (value < 1 || value > 65535) throw new ArgumentException(name + "必须为 1 至 65535。"); }
    private static string Text(Dictionary<string, object> map, string key, string fallback)
    { object value; return map.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback; }
    private static int Number(Dictionary<string, object> map, string key, int fallback)
    { object value; int result; return map.TryGetValue(key, out value) && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) ? result : fallback; }
    private static bool Bool(Dictionary<string, object> map, string key, bool fallback)
    { object value; bool result; return map.TryGetValue(key, out value) && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) ? result : fallback; }
}
