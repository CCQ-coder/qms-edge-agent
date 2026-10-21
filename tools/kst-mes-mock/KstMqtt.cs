using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class KstMqttDevice
{
    internal string SourceDeviceCode;
    internal string DeviceNumber;
    internal string Username;
    internal string Password;

    internal string ClientId(KstMqttSettings settings)
    {
        return "S&" + DeviceNumber + "&" + settings.ProductId + "&" + settings.UserId;
    }
}

internal sealed class KstMqttSettings
{
    internal bool Enabled;
    internal string Host;
    internal int Port;
    internal string ProductId;
    internal string DeviceNumber;
    internal string SourceDeviceCode;
    internal string UserId;
    internal string Username;
    internal string Password;
    internal int KeepAliveSeconds;
    internal int RequestTimeoutSeconds;
    internal int RetryIntervalSeconds;
    internal int TelemetryIntervalSeconds = 60;
    internal string QueueFile;
    internal readonly List<KstMqttDevice> AdditionalDevices = new List<KstMqttDevice>();

    internal static KstMqttSettings Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("S1 MQTT config was not found.", path);
        Dictionary<string, object> values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
        if (values == null) throw new ArgumentException("S1 MQTT config must be a JSON object.");
        KstMqttSettings settings = new KstMqttSettings();
        settings.Enabled = Bool(values, "enabled", false);
        settings.Host = Text(values, "host", "192.168.3.128").Trim();
        settings.Port = Number(values, "port", 1883);
        settings.ProductId = Text(values, "product_id", "").Trim();
        settings.DeviceNumber = Text(values, "device_number", "KST001").Trim();
        settings.SourceDeviceCode = Text(values, "source_device_code", "").Trim();
        settings.UserId = Text(values, "user_id", "").Trim();
        settings.Username = Text(values, "username", "").Trim();
        settings.Password = Text(values, "password", "");
        object rawDevices;
        if (values.TryGetValue("additional_devices", out rawDevices) && rawDevices != null)
        {
            IList devices = rawDevices as IList;
            if (devices == null) throw new ArgumentException("S1 MQTT additional_devices 必须是数组。");
            foreach (object raw in devices)
            {
                Dictionary<string, object> entry = raw as Dictionary<string, object>;
                if (entry == null) throw new ArgumentException("S1 MQTT additional_devices 的每项必须是对象。");
                settings.AdditionalDevices.Add(new KstMqttDevice {
                    SourceDeviceCode = Text(entry, "source_device_code", "").Trim(),
                    DeviceNumber = Text(entry, "device_number", "").Trim(),
                    Username = Text(entry, "username", "").Trim(),
                    Password = Text(entry, "password", "")
                });
            }
        }
        settings.KeepAliveSeconds = Number(values, "keep_alive_seconds", 30);
        settings.RequestTimeoutSeconds = Number(values, "request_timeout_seconds", 10);
        settings.RetryIntervalSeconds = Number(values, "retry_interval_seconds", 10);
        settings.TelemetryIntervalSeconds = Number(values, "telemetry_interval_seconds", 60);
        string queue = Text(values, "queue_file", ".\\data\\pending-s1-mqtt.json");
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        settings.QueueFile = Path.IsPathRooted(queue) ? queue : Path.GetFullPath(Path.Combine(baseDirectory, queue));
        settings.Validate();
        return settings;
    }

    internal void Save(string path)
    {
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        QueueFile = Path.IsPathRooted(QueueFile) ? Path.GetFullPath(QueueFile) : Path.GetFullPath(Path.Combine(baseDirectory, QueueFile));
        Validate();
        Dictionary<string, object> values = new Dictionary<string, object>();
        values["enabled"] = Enabled;
        values["host"] = Host;
        values["port"] = Port;
        values["product_id"] = ProductId;
        values["device_number"] = DeviceNumber;
        if (!string.IsNullOrEmpty(SourceDeviceCode)) values["source_device_code"] = SourceDeviceCode;
        values["user_id"] = UserId;
        values["username"] = Username;
        values["password"] = Password;
        if (AdditionalDevices.Count > 0)
        {
            ArrayList devices = new ArrayList();
            foreach (KstMqttDevice device in AdditionalDevices)
            {
                Dictionary<string, object> entry = new Dictionary<string, object>();
                entry["source_device_code"] = device.SourceDeviceCode;
                entry["device_number"] = device.DeviceNumber;
                if (!string.IsNullOrEmpty(device.Username)) entry["username"] = device.Username;
                if (!string.IsNullOrEmpty(device.Password)) entry["password"] = device.Password;
                devices.Add(entry);
            }
            values["additional_devices"] = devices;
        }
        values["keep_alive_seconds"] = KeepAliveSeconds;
        values["request_timeout_seconds"] = RequestTimeoutSeconds;
        values["retry_interval_seconds"] = RetryIntervalSeconds;
        values["telemetry_interval_seconds"] = TelemetryIntervalSeconds;
        string fullQueue = Path.GetFullPath(QueueFile);
        values["queue_file"] = fullQueue.StartsWith(baseDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? ".\\" + fullQueue.Substring(baseDirectory.Length + 1) : fullQueue;
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(values), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    internal void Validate()
    {
        if (Port < 1 || Port > 65535) throw new ArgumentException("S1 MQTT 端口必须为 1 至 65535。");
        if (KeepAliveSeconds < 5 || KeepAliveSeconds > 65535) throw new ArgumentException("S1 MQTT 保活时间必须为 5 至 65535 秒。");
        if (RequestTimeoutSeconds < 1 || RequestTimeoutSeconds > 300) throw new ArgumentException("S1 MQTT 超时必须为 1 至 300 秒。");
        if (RetryIntervalSeconds < 1 || RetryIntervalSeconds > 3600) throw new ArgumentException("S1 MQTT 重试间隔必须为 1 至 3600 秒。");
        if (TelemetryIntervalSeconds < 1 || TelemetryIntervalSeconds > 3600) throw new ArgumentException("S1 MQTT OEE/心跳上报间隔必须为 1 至 3600 秒。");
        if (string.IsNullOrWhiteSpace(QueueFile)) throw new ArgumentException("S1 MQTT 队列文件不能为空。");
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("S1 MQTT 主机不能为空。");
        if (!PositiveNumber(ProductId)) throw new ArgumentException("S1 产品 ID 必须是正整数。");
        if (!PositiveNumber(UserId)) throw new ArgumentException("S1 用户 ID 必须是正整数。");
        if (string.IsNullOrWhiteSpace(DeviceNumber)) throw new ArgumentException("S1 设备编号只能包含英文字母和数字。");
        ValidateDeviceNumber(DeviceNumber);
        if (string.IsNullOrWhiteSpace(Username)) throw new ArgumentException("S1 MQTT 用户名不能为空。");
        if (string.IsNullOrEmpty(Password)) throw new ArgumentException("S1 MQTT 密码不能为空。");
        HashSet<string> sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sources.Add(NormalizeDeviceCode(PrimarySourceCode()));
        targets.Add(DeviceNumber);
        foreach (KstMqttDevice device in AdditionalDevices)
        {
            if (string.IsNullOrWhiteSpace(device.SourceDeviceCode)) throw new ArgumentException("科视原始设备编号不能为空。");
            if (string.IsNullOrWhiteSpace(device.DeviceNumber)) throw new ArgumentException("S1 设备编号不能为空。");
            ValidateDeviceNumber(device.DeviceNumber);
            if (!sources.Add(NormalizeDeviceCode(device.SourceDeviceCode))) throw new ArgumentException("科视原始设备编号重复。");
            if (!targets.Add(device.DeviceNumber)) throw new ArgumentException("S1 设备编号重复。");
        }
    }

    internal string ClientId() { return "S&" + DeviceNumber + "&" + ProductId + "&" + UserId; }

    internal KstMqttDevice ResolveDevice(string sourceCode, bool allowMissing)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
        {
            if (!allowMissing && AdditionalDevices.Count > 0)
                throw new InvalidOperationException("科视报文缺少设备编号，无法区分多台设备。");
            return PrimaryDevice();
        }
        string normalized = NormalizeDeviceCode(sourceCode);
        if (string.Equals(normalized, NormalizeDeviceCode(PrimarySourceCode()), StringComparison.OrdinalIgnoreCase)) return PrimaryDevice();
        foreach (KstMqttDevice device in AdditionalDevices)
            if (string.Equals(normalized, NormalizeDeviceCode(device.SourceDeviceCode), StringComparison.OrdinalIgnoreCase))
                return EffectiveDevice(device);
        throw new InvalidOperationException("科视设备编号 " + sourceCode + " 未配置 S1 设备映射。");
    }

    internal KstMqttDevice DeviceByNumber(string number)
    {
        if (string.Equals(number, DeviceNumber, StringComparison.OrdinalIgnoreCase)) return PrimaryDevice();
        foreach (KstMqttDevice device in AdditionalDevices)
            if (string.Equals(number, device.DeviceNumber, StringComparison.OrdinalIgnoreCase)) return EffectiveDevice(device);
        throw new InvalidOperationException("S1 设备编号 " + number + " 未配置，队列保留待重试。");
    }

    private KstMqttDevice PrimaryDevice()
    {
        return new KstMqttDevice { SourceDeviceCode = PrimarySourceCode(), DeviceNumber = DeviceNumber, Username = Username, Password = Password };
    }

    private string PrimarySourceCode() { return string.IsNullOrEmpty(SourceDeviceCode) ? DeviceNumber : SourceDeviceCode; }

    private KstMqttDevice EffectiveDevice(KstMqttDevice device)
    {
        return new KstMqttDevice { SourceDeviceCode = device.SourceDeviceCode, DeviceNumber = device.DeviceNumber,
            Username = string.IsNullOrEmpty(device.Username) ? Username : device.Username,
            Password = string.IsNullOrEmpty(device.Password) ? Password : device.Password };
    }

    private static string NormalizeDeviceCode(string code) { return (code ?? "").Trim().Replace("-", ""); }

    private static void ValidateDeviceNumber(string number)
    {
        foreach (char character in number)
            if (!((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9')))
                throw new ArgumentException("S1 设备编号只能包含英文字母和数字。");
    }

    private static bool PositiveNumber(string value)
    {
        long number;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    }

    private static string Text(Dictionary<string, object> values, string key, string fallback)
    {
        object value;
        return values.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : fallback;
    }

    private static int Number(Dictionary<string, object> values, string key, int fallback)
    {
        object value;
        int number;
        return values.TryGetValue(key, out value) && value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number) ? number : fallback;
    }

    private static bool Bool(Dictionary<string, object> values, string key, bool fallback)
    {
        object value;
        bool result;
        return values.TryGetValue(key, out value) && value != null && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result) ? result : fallback;
    }
}

internal sealed class KstMqttQueueItem
{
    internal string Method;
    internal string DeviceCode;
    internal string S1DeviceNumber;
    internal string SourceClientIp;
    internal string RequestHost;
    internal string RoutingSource;
    internal string QueueId;
    internal DateTime NotBeforeUtc;
    internal DateTime ReceivedAt;
    internal Dictionary<string, object> Body;

    internal static KstMqttQueueItem FromExchange(KstExchange exchange)
    {
        Dictionary<string, object> body = new JavaScriptSerializer().DeserializeObject(exchange.RequestBody) as Dictionary<string, object>;
        if (body == null) throw new ArgumentException("科视 MQTT 请求正文必须是 JSON 对象。");
        KstMqttQueueItem item = new KstMqttQueueItem();
        item.Method = exchange.Method;
        item.DeviceCode = exchange.DeviceCode;
        item.SourceClientIp = exchange.ClientIp;
        item.RequestHost = exchange.RequestHost;
        item.RoutingSource = exchange.RoutingSource;
        item.ReceivedAt = exchange.Time;
        item.Body = body;
        return item;
    }

    internal Dictionary<string, object> ToDictionary()
    {
        Dictionary<string, object> value = new Dictionary<string, object>();
        value["method"] = Method;
        value["device_code"] = DeviceCode;
        if (!string.IsNullOrEmpty(S1DeviceNumber)) value["s1_device_number"] = S1DeviceNumber;
        if (!string.IsNullOrEmpty(SourceClientIp)) value["source_client_ip"] = SourceClientIp;
        if (!string.IsNullOrEmpty(RequestHost)) value["request_host"] = RequestHost;
        if (!string.IsNullOrEmpty(RoutingSource)) value["routing_source"] = RoutingSource;
        if (!string.IsNullOrEmpty(QueueId)) value["queue_id"] = QueueId;
        if (NotBeforeUtc != DateTime.MinValue) value["not_before_utc"] = NotBeforeUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        value["received_at"] = ReceivedAt.ToString("o", CultureInfo.InvariantCulture);
        value["body"] = Body;
        return value;
    }

    internal static KstMqttQueueItem FromDictionary(Dictionary<string, object> value)
    {
        object raw;
        KstMqttQueueItem item = new KstMqttQueueItem();
        item.Method = value.TryGetValue("method", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
        item.DeviceCode = value.TryGetValue("device_code", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
        item.S1DeviceNumber = value.TryGetValue("s1_device_number", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
        item.SourceClientIp = value.TryGetValue("source_client_ip", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
        item.RequestHost = value.TryGetValue("request_host", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
        item.RoutingSource = value.TryGetValue("routing_source", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
        item.QueueId = value.TryGetValue("queue_id", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
        DateTime notBefore;
        if (value.TryGetValue("not_before_utc", out raw) && raw != null)
        {
            if (!DateTime.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out notBefore))
                throw new InvalidDataException("S1 MQTT queue due time is invalid.");
            item.NotBeforeUtc = notBefore.ToUniversalTime();
        }
        DateTime time;
        if (!value.TryGetValue("received_at", out raw) || !DateTime.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time))
            throw new InvalidDataException("S1 MQTT queue timestamp is invalid.");
        item.ReceivedAt = time;
        item.Body = value.TryGetValue("body", out raw) ? raw as Dictionary<string, object> : null;
        if (string.IsNullOrEmpty(item.Method) || item.Body == null) throw new InvalidDataException("S1 MQTT queue item is incomplete.");
        return item;
    }
}

internal sealed class KstMqttMessage
{
    internal readonly string Topic;
    internal readonly string Payload;
    internal KstMqttMessage(string topic, string payload) { Topic = topic; Payload = payload; }
}

internal static class KstMqttTemplate
{
    internal static KstMqttMessage Build(KstMqttQueueItem source, KstMqttSettings settings)
    {
        return Build(source, settings, settings.DeviceNumber);
    }

    internal static KstMqttMessage Build(KstMqttQueueItem source, KstMqttSettings settings, string deviceNumber)
    {
        string id, field, suffix;
        if (source.Method == "Status") { id = "kst_status"; field = "Status"; suffix = "/property/post"; }
        else if (source.Method == "OEE") { id = "kst_oee"; field = "OEE"; suffix = "/property/post"; }
        else if (source.Method == "Alarm") { id = "kst_alarm"; field = "EventMessage"; suffix = "/event/post"; }
        else if (source.Method == "ExposureResult") { id = "kst_exposure_result"; field = "CRESULT"; suffix = "/property/post"; }
        else if (source.Method == "HeartBeat") { id = "kst_heartbeat"; field = "HeartBeat"; suffix = "/property/post"; }
        else if (source.Method == "Mode") { id = "kst_mode"; field = "Mode"; suffix = "/property/post"; }
        else throw new ArgumentException("S1 MQTT 不支持科视方法 " + source.Method + "。");

        object value;
        if (!source.Body.TryGetValue(field, out value) || value == null || string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture)))
            throw new ArgumentException("S1 MQTT 模板缺少科视字段 " + field + "。");
        Dictionary<string, object> model = new Dictionary<string, object>();
        model["id"] = id;
        IFormattable formattable = value as IFormattable;
        string valueText = formattable == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : formattable.ToString(null, CultureInfo.InvariantCulture);
        if (source.Method == "OEE")
            valueText = Math.Round(decimal.Parse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture), 2,
                MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture);
        model["value"] = valueText;
        model["ts"] = Timestamp(source);
        model["remark"] = Remark(source);
        ArrayList array = new ArrayList();
        array.Add(model);
        return new KstMqttMessage("/" + settings.ProductId + "/" + deviceNumber + suffix,
            new JavaScriptSerializer().Serialize(array));
    }

    private static string Timestamp(KstMqttQueueItem source)
    {
        object value;
        DateTime parsed;
        if (source.Body.TryGetValue("MachineTime", out value) && value != null &&
            DateTime.TryParseExact(Convert.ToString(value, CultureInfo.InvariantCulture),
                new string[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out parsed))
            return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return source.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string Remark(KstMqttQueueItem source)
    {
        Dictionary<string, object> details = new Dictionary<string, object>();
        details["method"] = source.Method;
        details["source_device_code"] = source.DeviceCode;
        Add(details, source.Body, "ID");
        Add(details, source.Body, "assets");
        Add(details, source.Body, "MachineNO");
        Add(details, source.Body, "MachineTime");
        Add(details, source.Body, "StartTime");
        Add(details, source.Body, "EndTime");
        Add(details, source.Body, "UserName");
        Add(details, source.Body, "Level");
        Add(details, source.Body, "ItemNO");
        Add(details, source.Body, "LotNO");
        Add(details, source.Body, "CBARCODE");
        Add(details, source.Body, "CORDER_NO");
        Add(details, source.Body, "CITEM_NO");
        Add(details, source.Body, "CLOT_NO");
        Add(details, source.Body, "CLAYER");
        Add(details, source.Body, "CFACE");
        Add(details, source.Body, "CSTART_TIME");
        Add(details, source.Body, "CEND_TIME");
        return new JavaScriptSerializer().Serialize(details);
    }

    private static void Add(Dictionary<string, object> target, Dictionary<string, object> source, string key)
    {
        object value;
        if (source.TryGetValue(key, out value) && value != null) target[key] = value;
    }
}

internal sealed class KstMqttQueueClaim
{
    internal readonly string QueueId;
    internal readonly KstMqttQueueItem Item;

    internal KstMqttQueueClaim(string queueId, KstMqttQueueItem item)
    {
        QueueId = queueId;
        Item = item;
    }
}

internal sealed class KstMqttQueue
{
    private readonly string path;
    private readonly string statePath;
    private readonly KstMqttSettings settings;
    private readonly List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
    private readonly Dictionary<string, string> lastPublishedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> claimed = new HashSet<string>(StringComparer.Ordinal);
    private readonly object sync = new object();
    private string backupPath;

    internal KstMqttQueue(string path) : this(path, null) { }

    internal KstMqttQueue(string path, KstMqttSettings settings)
    {
        this.path = Path.GetFullPath(path);
        statePath = this.path + ".rate-state.json";
        this.settings = settings;
        if (settings != null) LoadRateState();
        if (!File.Exists(this.path)) return;
        IList saved = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(this.path, Encoding.UTF8)) as IList;
        if (saved == null) throw new InvalidDataException("S1 MQTT queue must be a JSON array: " + this.path);
        foreach (object raw in saved)
        {
            Dictionary<string, object> entry = raw as Dictionary<string, object>;
            if (entry == null) throw new InvalidDataException("S1 MQTT queue item is invalid.");
            KstMqttQueueItem.FromDictionary(entry);
            items.Add(entry);
        }
        if (settings != null) UpgradeOldQueue();
    }

    internal int Count { get { lock (sync) return items.Count; } }
    internal string BackupPath { get { lock (sync) return backupPath; } }

    internal List<KstMqttQueueItem> PendingSnapshot()
    {
        lock (sync)
        {
            List<KstMqttQueueItem> snapshot = new List<KstMqttQueueItem>(items.Count);
            foreach (Dictionary<string, object> entry in items) snapshot.Add(KstMqttQueueItem.FromDictionary(entry));
            return snapshot;
        }
    }

    // Legacy append is retained for existing callers and queue tests.
    internal void Enqueue(KstMqttQueueItem item)
    {
        lock (sync)
        {
            items.Add(item.ToDictionary());
            try { Save(); }
            catch { items.RemoveAt(items.Count - 1); throw; }
        }
    }

    internal bool EnqueueManaged(KstMqttQueueItem item)
    {
        if (settings == null) throw new InvalidOperationException("S1 MQTT managed queue requires settings.");
        string key = StreamKey(item);
        bool telemetry = IsTelemetry(item.Method);
        bool state = IsState(item.Method);
        lock (sync)
        {
            int pendingIndex = -1;
            bool inFlight = false;
            KstMqttQueueItem inFlightItem = null;
            if (key != null)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    KstMqttQueueItem existing = KstMqttQueueItem.FromDictionary(items[index]);
                    if (!string.Equals(key, StreamKey(existing), StringComparison.OrdinalIgnoreCase)) continue;
                    if (claimed.Contains(existing.QueueId)) { inFlight = true; inFlightItem = existing; }
                    else pendingIndex = index;
                }
            }

            string value = state ? StateValue(item) : null;
            string published;
            if (state && lastPublishedValues.TryGetValue(key, out published) &&
                string.Equals(value, published, StringComparison.Ordinal) && !inFlight)
            {
                // A state that returned to its last published value cancels an unsent transition.
                if (pendingIndex >= 0)
                {
                    Dictionary<string, object> removed = items[pendingIndex];
                    items.RemoveAt(pendingIndex);
                    try { Save(); }
                    catch { items.Insert(pendingIndex, removed); throw; }
                }
                return false;
            }

            if (state && inFlightItem != null && string.Equals(StateValue(inFlightItem), value, StringComparison.Ordinal))
            {
                // The in-flight state is already the newest one. Discard a stale successor, if any.
                if (pendingIndex >= 0)
                {
                    Dictionary<string, object> removed = items[pendingIndex];
                    items.RemoveAt(pendingIndex);
                    try { Save(); }
                    catch { items.Insert(pendingIndex, removed); throw; }
                }
                return false;
            }

            if (pendingIndex >= 0)
            {
                KstMqttQueueItem prior = KstMqttQueueItem.FromDictionary(items[pendingIndex]);
                if (state && string.Equals(StateValue(prior), value, StringComparison.Ordinal)) return false;
                item.QueueId = prior.QueueId;
                item.NotBeforeUtc = prior.NotBeforeUtc;
                Dictionary<string, object> old = items[pendingIndex];
                items[pendingIndex] = item.ToDictionary();
                try { Save(); }
                catch { items[pendingIndex] = old; throw; }
                return true;
            }

            item.QueueId = Guid.NewGuid().ToString("N");
            if (telemetry) item.NotBeforeUtc = DateTime.UtcNow.AddSeconds(settings.TelemetryIntervalSeconds);
            items.Add(item.ToDictionary());
            try { Save(); }
            catch { items.RemoveAt(items.Count - 1); throw; }
            return true;
        }
    }

    internal KstMqttQueueClaim ClaimDue(int index, DateTime utcNow)
    {
        lock (sync)
        {
            if (index < 0 || index >= items.Count) return null;
            KstMqttQueueItem item = KstMqttQueueItem.FromDictionary(items[index]);
            if (string.IsNullOrEmpty(item.QueueId) || claimed.Contains(item.QueueId) ||
                item.NotBeforeUtc > utcNow.ToUniversalTime()) return null;
            claimed.Add(item.QueueId);
            return new KstMqttQueueClaim(item.QueueId, item);
        }
    }

    internal void Complete(KstMqttQueueClaim claim, DateTime utcNow)
    {
        lock (sync)
        {
            if (claim == null || !claimed.Contains(claim.QueueId)) return;
            int index = IndexOf(claim.QueueId);
            if (index < 0) { claimed.Remove(claim.QueueId); return; }
            KstMqttQueueItem sent = KstMqttQueueItem.FromDictionary(items[index]);
            string key = StreamKey(sent);
            if (IsState(sent.Method))
            {
                string value = StateValue(sent);
                string previous;
                bool hadPrevious = lastPublishedValues.TryGetValue(key, out previous);
                lastPublishedValues[key] = value;
                try { SaveRateState(); }
                catch
                {
                    if (hadPrevious) lastPublishedValues[key] = previous;
                    else lastPublishedValues.Remove(key);
                    throw;
                }
            }

            List<Dictionary<string, object>> before = new List<Dictionary<string, object>>(items);
            items.RemoveAt(index);
            for (int otherIndex = items.Count - 1; otherIndex >= 0; otherIndex--)
            {
                KstMqttQueueItem other = KstMqttQueueItem.FromDictionary(items[otherIndex]);
                if (!string.Equals(key, StreamKey(other), StringComparison.OrdinalIgnoreCase)) continue;
                if (IsTelemetry(sent.Method))
                {
                    DateTime nextDue = utcNow.ToUniversalTime().AddSeconds(settings.TelemetryIntervalSeconds);
                    if (other.NotBeforeUtc < nextDue)
                    {
                        other.NotBeforeUtc = nextDue;
                        items[otherIndex] = other.ToDictionary();
                    }
                }
                else if (IsState(sent.Method) &&
                    string.Equals(StateValue(other), StateValue(sent), StringComparison.Ordinal) &&
                    !claimed.Contains(other.QueueId)) items.RemoveAt(otherIndex);
            }
            try { Save(); }
            catch
            {
                items.Clear();
                items.AddRange(before);
                throw;
            }
            claimed.Remove(claim.QueueId);
        }
    }

    internal void Release(KstMqttQueueClaim claim)
    {
        lock (sync)
        {
            if (claim == null || !claimed.Contains(claim.QueueId)) return;
            int index = IndexOf(claim.QueueId);
            if (index < 0) { claimed.Remove(claim.QueueId); return; }
            KstMqttQueueItem prior = KstMqttQueueItem.FromDictionary(items[index]);
            string key = StreamKey(prior);
            int successorIndex = -1;
            if (key != null)
                for (int i = 0; i < items.Count; i++)
                    if (i != index && !claimed.Contains(KstMqttQueueItem.FromDictionary(items[i]).QueueId) &&
                        string.Equals(key, StreamKey(KstMqttQueueItem.FromDictionary(items[i])), StringComparison.OrdinalIgnoreCase))
                        successorIndex = i;
            if (successorIndex >= 0)
            {
                List<Dictionary<string, object>> before = new List<Dictionary<string, object>>(items);
                KstMqttQueueItem latest = KstMqttQueueItem.FromDictionary(items[successorIndex]);
                // A missing PUBACK is ambiguous: even a successor equal to the last confirmed
                // state must be sent, since the broker may have accepted the in-flight change.
                latest.QueueId = prior.QueueId;
                latest.NotBeforeUtc = prior.NotBeforeUtc;
                items[index] = latest.ToDictionary();
                items.RemoveAt(successorIndex);
                try { Save(); }
                catch { items.Clear(); items.AddRange(before); throw; }
            }
            claimed.Remove(claim.QueueId);
        }
    }

    internal int NextWaitMilliseconds(int maxMilliseconds)
    {
        lock (sync)
        {
            int wait = Math.Max(0, maxMilliseconds);
            DateTime now = DateTime.UtcNow;
            foreach (Dictionary<string, object> raw in items)
            {
                KstMqttQueueItem item = KstMqttQueueItem.FromDictionary(raw);
                if (claimed.Contains(item.QueueId)) continue;
                if (item.NotBeforeUtc <= now) return 0;
                double remaining = (item.NotBeforeUtc - now).TotalMilliseconds;
                if (remaining < wait) wait = Math.Max(1, (int)Math.Ceiling(remaining));
            }
            return wait;
        }
    }

    internal KstMqttQueueItem Peek() { return Peek(0); }

    internal KstMqttQueueItem Peek(int index)
    {
        lock (sync) return KstMqttQueueItem.FromDictionary(items[index]);
    }

    internal void RemoveFirst() { RemoveAt(0); }

    internal void RemoveAt(int index)
    {
        lock (sync)
        {
            if (items.Count == 0) return;
            Dictionary<string, object> removed = items[index];
            items.RemoveAt(index);
            try { Save(); }
            catch { items.Insert(index, removed); throw; }
        }
    }

    private void UpgradeOldQueue()
    {
        bool needsUpgrade = false;
        DateTime due = DateTime.UtcNow.AddSeconds(settings.TelemetryIntervalSeconds);
        for (int i = 0; i < items.Count; i++)
        {
            KstMqttQueueItem item = KstMqttQueueItem.FromDictionary(items[i]);
            bool changed = false;
            if (string.IsNullOrEmpty(item.QueueId)) { item.QueueId = Guid.NewGuid().ToString("N"); changed = true; }
            if (IsTelemetry(item.Method) && item.NotBeforeUtc == DateTime.MinValue)
            { item.NotBeforeUtc = due; changed = true; }
            if (string.IsNullOrEmpty(item.S1DeviceNumber))
            {
                try { item.S1DeviceNumber = settings.ResolveDevice(item.DeviceCode, true).DeviceNumber; changed = true; }
                catch (InvalidOperationException) { }
            }
            if (changed) { items[i] = item.ToDictionary(); needsUpgrade = true; }
        }
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = items.Count - 1; index >= 0; index--)
        {
            KstMqttQueueItem item = KstMqttQueueItem.FromDictionary(items[index]);
            string key = StreamKey(item);
            if (key != null && !seen.Add(key)) { items.RemoveAt(index); needsUpgrade = true; }
        }
        if (!needsUpgrade) return;
        backupPath = path + ".before-rate-limit-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
        File.Copy(path, backupPath, false);
        Save();
    }

    private int IndexOf(string queueId)
    {
        for (int index = 0; index < items.Count; index++)
            if (string.Equals(KstMqttQueueItem.FromDictionary(items[index]).QueueId, queueId, StringComparison.Ordinal)) return index;
        return -1;
    }

    private string StreamKey(KstMqttQueueItem item)
    {
        if (!IsTelemetry(item.Method) && !IsState(item.Method)) return null;
        string device = string.IsNullOrEmpty(item.S1DeviceNumber) ? item.DeviceCode : item.S1DeviceNumber;
        string key = (device ?? "") + "\u001f" + item.Method;
        if (item.Method == "OEE") key += "\u001f" + Field(item.Body, "StartTime") + "\u001f" + Field(item.Body, "EndTime");
        return key;
    }

    private static bool IsTelemetry(string method) { return method == "OEE" || method == "HeartBeat"; }
    private static bool IsState(string method) { return method == "Status" || method == "Mode"; }
    private static string StateValue(KstMqttQueueItem item) { return Field(item.Body, item.Method); }

    private static string Field(Dictionary<string, object> body, string name)
    {
        object value;
        return body != null && body.TryGetValue(name, out value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
    }

    private void LoadRateState()
    {
        if (!File.Exists(statePath)) return;
        Dictionary<string, object> values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(statePath, Encoding.UTF8)) as Dictionary<string, object>;
        if (values == null) throw new InvalidDataException("S1 MQTT rate state must be a JSON object: " + statePath);
        foreach (KeyValuePair<string, object> pair in values)
            lastPublishedValues[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "";
    }

    private void SaveRateState()
    {
        string directory = Path.GetDirectoryName(statePath);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        string temporary = statePath + ".tmp";
        File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(lastPublishedValues), new UTF8Encoding(false));
        if (File.Exists(statePath)) File.Replace(temporary, statePath, null);
        else File.Move(temporary, statePath);
    }

    private void Save()
    {
        string directory = Path.GetDirectoryName(path);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(items), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}

internal sealed class KstMqttClient
{
    private readonly KstMqttSettings settings;
    private readonly KstMqttDevice device;
    private TcpClient socket;
    private NetworkStream stream;
    private ushort packetId = 1;
    private DateTime lastActivityUtc = DateTime.MinValue;
    private volatile bool aborted;

    internal KstMqttClient(KstMqttSettings settings) : this(settings, settings.DeviceByNumber(settings.DeviceNumber)) { }
    internal KstMqttClient(KstMqttSettings settings, KstMqttDevice device) { this.settings = settings; this.device = device; }

    internal void Publish(KstMqttMessage message)
    {
        try
        {
            // A healthy idle session must stay online. Probe it before publishing if a
            // keepalive is due; a failed probe closes the stale socket and reconnects.
            if (stream != null && MillisecondsUntilPing() == 0)
            {
                try { PingIfDue(); }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
            EnsureConnected();
            ushort id = packetId++;
            if (packetId == 0) packetId = 1;
            Write(0x32, Combine(Combine(Encode(message.Topic), new byte[] { (byte)(id >> 8), (byte)id }), Encoding.UTF8.GetBytes(message.Payload)));
            byte type;
            byte[] ack = Read(out type);
            if (type != 0x40 || ack.Length != 2 || ack[0] != (byte)(id >> 8) || ack[1] != (byte)id)
                throw new IOException("S1 MQTT 未返回匹配的 PUBACK。");
            lastActivityUtc = DateTime.UtcNow;
        }
        catch { Close(); throw; }
    }

    internal int MillisecondsUntilPing()
    {
        if (stream == null || lastActivityUtc == DateTime.MinValue) return int.MaxValue;
        long safeIdleTicks = (long)settings.KeepAliveSeconds * TimeSpan.TicksPerSecond * 3 / 4;
        double remaining = (lastActivityUtc + TimeSpan.FromTicks(safeIdleTicks) - DateTime.UtcNow).TotalMilliseconds;
        return remaining <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining));
    }

    internal void PingIfDue()
    {
        if (MillisecondsUntilPing() > 0) return;
        try
        {
            Write(0xC0, new byte[0]);
            byte type;
            byte[] response = Read(out type);
            if (type != 0xD0 || response.Length != 0) throw new IOException("S1 MQTT PINGRESP 无效。");
            lastActivityUtc = DateTime.UtcNow;
        }
        catch { Close(); throw; }
    }

    internal bool MaintainConnection()
    {
        if (stream != null) { PingIfDue(); return false; }
        try { EnsureConnected(); return true; }
        catch { Close(); throw; }
    }

    internal bool HasActiveSession { get { return stream != null && lastActivityUtc != DateTime.MinValue; } }

    // Used when stopping the worker: closing the socket interrupts a blocked PUBACK read.
    internal void Abort()
    {
        aborted = true;
        TcpClient current = socket;
        try { if (current != null) current.Close(); } catch { }
    }

    internal void Close()
    {
        try { if (stream != null) Write(0xE0, new byte[0]); } catch { }
        try { if (stream != null) stream.Close(); } catch { }
        try { if (socket != null) socket.Close(); } catch { }
        stream = null;
        socket = null;
        lastActivityUtc = DateTime.MinValue;
    }

    private void EnsureConnected()
    {
        if (aborted) throw new IOException("S1 MQTT 客户端已停止。");
        if (socket != null && socket.Connected && stream != null) return;
        Close();
        socket = new TcpClient();
        IAsyncResult result = socket.BeginConnect(settings.Host, settings.Port, null, null);
        Stopwatch timer = Stopwatch.StartNew();
        int timeout = settings.RequestTimeoutSeconds * 1000;
        while (true)
        {
            if (aborted) { Close(); throw new IOException("S1 MQTT 客户端已停止。"); }
            int remaining = Math.Max(0, timeout - (int)timer.ElapsedMilliseconds);
            if (remaining == 0) { Close(); throw new TimeoutException("连接 S1 MQTT 超时。"); }
            if (result.AsyncWaitHandle.WaitOne(Math.Min(100, remaining))) break;
        }
        if (aborted) { Close(); throw new IOException("S1 MQTT 客户端已停止。"); }
        socket.EndConnect(result);
        socket.NoDelay = true;
        stream = socket.GetStream();
        stream.ReadTimeout = settings.RequestTimeoutSeconds * 1000;
        stream.WriteTimeout = settings.RequestTimeoutSeconds * 1000;
        byte flags = 0xC2;
        byte[] variable = Combine(Encode("MQTT"), new byte[] { 0x04, flags, (byte)(settings.KeepAliveSeconds >> 8), (byte)settings.KeepAliveSeconds });
        byte[] payload = Combine(Combine(Encode(device.ClientId(settings)), Encode(device.Username)), Encode(device.Password));
        Write(0x10, Combine(variable, payload));
        byte type;
        byte[] ack = Read(out type);
        if (type != 0x20 || ack.Length != 2) throw new IOException("S1 MQTT CONNACK 无效。");
        if (ack[1] != 0) throw new IOException("S1 MQTT 拒绝连接，返回码 " + ack[1] + "。");
        lastActivityUtc = DateTime.UtcNow;
    }

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
        if (body.Length != 0) stream.Write(body, 0, body.Length);
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
}

internal sealed class KstMqttForwarder
{
    private readonly KstMqttSettings settings;
    private readonly KstMqttQueue queue;
    private readonly Dictionary<string, KstMqttClient> clients = new Dictionary<string, KstMqttClient>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Thread> maintenanceThreads = new Dictionary<string, Thread>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AutoResetEvent> maintenanceWakes = new Dictionary<string, AutoResetEvent>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> retryAfter = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> report;
    private readonly object reportSync = new object();
    private readonly object lifecycleSync = new object();
    private readonly AutoResetEvent wake = new AutoResetEvent(false);
    private FileStream queueLease;
    private volatile bool stopping;
    private bool stoppedPermanently;
    private Thread thread;

    internal KstMqttForwarder(KstMqttSettings settings, Action<string> report)
        : this(settings, report, null) { }

    internal KstMqttForwarder(KstMqttSettings settings, Action<string> report, KstSourceRoutes sourceRoutes)
    {
        this.settings = settings;
        this.report = report;
        if (settings.Enabled)
        {
            string queuePath = Path.GetFullPath(settings.QueueFile);
            Directory.CreateDirectory(Path.GetDirectoryName(queuePath));
            try { queueLease = new FileStream(queuePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("S1 MQTT 队列已由另一进程占用：" + queuePath, error); }
            try
            {
                if (sourceRoutes != null) sourceRoutes.ValidatePendingQueue(settings);
                queue = new KstMqttQueue(settings.QueueFile, settings);
            }
            catch { queueLease.Dispose(); queueLease = null; throw; }
            if (!string.IsNullOrEmpty(queue.BackupPath))
                Report("S1 MQTT 旧队列已压缩，原始备份：" + queue.BackupPath + "；待发送 " + queue.Count + " 条");
        }
    }

    internal void Start()
    {
        lock (lifecycleSync)
        {
            if (stoppedPermanently) throw new InvalidOperationException("S1 MQTT 转发器已停止，请创建新实例。");
            if (!settings.Enabled) return;
            lock (clients)
            {
                if (thread != null) return;
                stopping = false;
                thread = new Thread(Run);
                thread.IsBackground = true;
                thread.Name = "kst-s1-mqtt";
                foreach (KeyValuePair<string, KstMqttClient> pair in clients)
                    StartMaintenanceLocked(pair.Key, pair.Value);
                thread.Start();
            }
        }
    }

    internal void Enqueue(KstExchange exchange)
    {
        if (stopping || stoppedPermanently) throw new InvalidOperationException("S1 MQTT 转发器已停止，请创建新实例。");
        if (!settings.Enabled) return;
        KstMqttQueueItem item = KstMqttQueueItem.FromExchange(exchange);
        if (settings.AdditionalDevices.Count > 0)
        {
            string machine = DeviceCodeField(item.Body, "MachineNO");
            string assets = DeviceCodeField(item.Body, "assets");
            if (machine.Length > 0 && assets.Length > 0 &&
                !string.Equals(machine.Replace("-", ""), assets.Replace("-", ""), StringComparison.OrdinalIgnoreCase)
                && !(exchange.AcceptedStaleAsset && exchange.RoutingSource == "trusted_proxy_host"
                    && string.Equals(machine.Replace("-", ""), item.DeviceCode.Replace("-", ""), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("科视报文的 MachineNO 与 assets 设备编号冲突。");
        }
        KstMqttDevice device = settings.ResolveDevice(item.DeviceCode, false);
        item.S1DeviceNumber = device.DeviceNumber;
        // Validate the S1 mapping before acknowledging the device so a malformed item cannot block the durable queue.
        KstMqttTemplate.Build(item, settings, device.DeviceNumber);
        int previousCount;
        int pendingCount;
        bool changed;
        lock (lifecycleSync)
        {
            if (stopping || stoppedPermanently) throw new InvalidOperationException("S1 MQTT 转发器正在停止。");
            previousCount = queue.Count;
            changed = queue.EnqueueManaged(item);
            // An accepted source is active even when its telemetry is buffered for 60 seconds
            // or a repeated state produces no new property report.
            RegisterClient(device);
            pendingCount = queue.Count;
            wake.Set();
        }
        if (!changed) return;
        Report((pendingCount > previousCount ? "S1 MQTT 已入队" : "S1 MQTT 已更新待发送数据")
            + "，待发送 " + pendingCount + " 条");
    }

    internal void Stop()
    {
        lock (lifecycleSync)
        {
            if (stoppedPermanently) return;
            stopping = true;
            wake.Set();
            lock (clients)
                foreach (AutoResetEvent signal in maintenanceWakes.Values) signal.Set();
            AbortClients();
        }
        Thread running = thread;
        if (running != null && running != Thread.CurrentThread) running.Join();
        List<Thread> keepers;
        lock (clients) keepers = new List<Thread>(maintenanceThreads.Values);
        foreach (Thread keeper in keepers)
            if (keeper != Thread.CurrentThread) keeper.Join();
        CloseClients();
        thread = null;
        if (queueLease != null) { queueLease.Dispose(); queueLease = null; }
        stoppedPermanently = true;
    }

    private void Run()
    {
        while (!stopping)
        {
            Flush();
            wake.WaitOne(NextWaitMilliseconds());
        }
    }

    private KstMqttClient RegisterClient(KstMqttDevice device)
    {
        lock (clients)
        {
            if (stopping) throw new InvalidOperationException("S1 MQTT 转发器正在停止。");
            KstMqttClient client;
            if (!clients.TryGetValue(device.DeviceNumber, out client))
            {
                client = new KstMqttClient(settings, device);
                clients.Add(device.DeviceNumber, client);
                maintenanceWakes.Add(device.DeviceNumber, new AutoResetEvent(false));
                if (thread != null) StartMaintenanceLocked(device.DeviceNumber, client);
            }
            maintenanceWakes[device.DeviceNumber].Set();
            return client;
        }
    }

    private void StartMaintenanceLocked(string deviceNumber, KstMqttClient client)
    {
        if (maintenanceThreads.ContainsKey(deviceNumber)) return;
        AutoResetEvent signal = maintenanceWakes[deviceNumber];
        Thread keeper = new Thread(delegate() { MaintainDevice(deviceNumber, client, signal); });
        keeper.IsBackground = true;
        keeper.Name = "kst-s1-mqtt-" + deviceNumber;
        maintenanceThreads.Add(deviceNumber, keeper);
        keeper.Start();
    }

    private void WakeMaintenance(string deviceNumber)
    {
        lock (clients)
        {
            AutoResetEvent signal;
            if (maintenanceWakes.TryGetValue(deviceNumber, out signal)) signal.Set();
        }
    }

    private void MaintainDevice(string deviceNumber, KstMqttClient client, AutoResetEvent signal)
    {
        DateTime retryTime = DateTime.MinValue;
        while (!stopping)
        {
            // A publish may have established the session while this maintainer was
            // waiting after a failed attempt. Do not let that old retry delay its ping.
            if (retryTime != DateTime.MinValue)
                lock (client) if (client.HasActiveSession) retryTime = DateTime.MinValue;
            if (DateTime.UtcNow >= retryTime)
            {
                try
                {
                    bool reconnected;
                    lock (client)
                    {
                        if (stopping) return;
                        reconnected = client.MaintainConnection();
                    }
                    retryTime = DateTime.MinValue;
                    if (reconnected && !stopping) Report("S1 MQTT 已连接或恢复：设备 " + deviceNumber);
                }
                catch (Exception error)
                {
                    retryTime = DateTime.UtcNow.AddSeconds(settings.RetryIntervalSeconds);
                    if (!stopping) Report("S1 MQTT 保活或重连失败：设备 " + deviceNumber + "；" + error.Message);
                }
            }
            if (stopping) return;
            int wait = settings.RetryIntervalSeconds * 1000;
            if (retryTime != DateTime.MinValue)
                wait = Math.Max(1, (int)Math.Min(int.MaxValue, Math.Ceiling((retryTime - DateTime.UtcNow).TotalMilliseconds)));
            else lock (client) wait = Math.Min(wait, client.MillisecondsUntilPing());
            signal.WaitOne(wait);
        }
    }

    private void Flush()
    {
        int index = 0;
        int remaining = queue.Count;
        while (!stopping && remaining > 0 && index < queue.Count)
        {
            remaining--;
            KstMqttQueueClaim claim = queue.ClaimDue(index, DateTime.UtcNow);
            if (claim == null) { index++; continue; }
            KstMqttQueueItem item = claim.Item;
            string deviceKey = DeviceKey(item);
            try
            {
                // Old queue records have no S1 target; retain their original routing rule when replaying.
                KstMqttDevice device = string.IsNullOrEmpty(item.S1DeviceNumber)
                    ? settings.ResolveDevice(item.DeviceCode, true) : settings.DeviceByNumber(item.S1DeviceNumber);
                deviceKey = device.DeviceNumber;
                DateTime retryTime;
                if (retryAfter.TryGetValue(deviceKey, out retryTime) && DateTime.UtcNow < retryTime)
                { queue.Release(claim); index++; continue; }
                KstMqttMessage message = KstMqttTemplate.Build(item, settings, device.DeviceNumber);
                KstMqttClient client = RegisterClient(device);
                if (stopping) { queue.Release(claim); return; }
                lock (client) client.Publish(message);
                WakeMaintenance(device.DeviceNumber);
                queue.Complete(claim, DateTime.UtcNow);
                retryAfter.Remove(deviceKey);
                Report("S1 MQTT 发送成功：" + message.Topic + "；待发送 " + queue.Count + " 条");
            }
            catch (Exception error)
            {
                queue.Release(claim);
                retryAfter[deviceKey] = DateTime.UtcNow.AddSeconds(settings.RetryIntervalSeconds);
                Report("S1 MQTT 待重试：设备 " + deviceKey + "；" + error.Message + "；保留 " + queue.Count + " 条");
                index++;
            }
        }
    }

    private int NextWaitMilliseconds()
    {
        int wait = settings.RetryIntervalSeconds * 1000;
        DateTime now = DateTime.UtcNow;
        foreach (KstMqttQueueItem item in queue.PendingSnapshot())
        {
            DateTime due = item.NotBeforeUtc;
            DateTime retryTime;
            if (retryAfter.TryGetValue(DeviceKey(item), out retryTime) && retryTime > due) due = retryTime;
            if (due <= now) return 0;
            double remaining = (due - now).TotalMilliseconds;
            if (remaining < wait) wait = Math.Max(1, (int)Math.Ceiling(remaining));
        }
        return wait;
    }

    private string DeviceKey(KstMqttQueueItem item)
    {
        return !string.IsNullOrEmpty(item.S1DeviceNumber) ? item.S1DeviceNumber
            : (string.IsNullOrEmpty(item.DeviceCode) ? settings.DeviceNumber : item.DeviceCode);
    }

    private static string DeviceCodeField(Dictionary<string, object> body, string key)
    {
        object value;
        return body.TryGetValue(key, out value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim() : "";
    }

    private void CloseClients()
    {
        lock (clients)
        {
            foreach (KstMqttClient client in clients.Values) client.Close();
            foreach (AutoResetEvent signal in maintenanceWakes.Values) signal.Close();
            clients.Clear();
            maintenanceWakes.Clear();
            maintenanceThreads.Clear();
        }
    }

    private void AbortClients()
    {
        lock (clients)
            foreach (KstMqttClient client in clients.Values) client.Abort();
    }

    private void Report(string text)
    {
        lock (reportSync)
        {
            try
            {
                string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, "mqtt-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine;
                using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(line);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch { }
        }
        if (report != null) report(text);
    }
}

internal sealed class KstMqttSettingsForm : Form
{
    private readonly string path;
    private readonly string originalQueueFile;
    private readonly List<KstMqttDevice> additionalDevices;
    private readonly string sourceDeviceCode;
    private readonly CheckBox enabled = new CheckBox();
    private readonly TextBox host = Box();
    private readonly TextBox port = Box();
    private readonly TextBox product = Box();
    private readonly TextBox device = Box();
    private readonly TextBox user = Box();
    private readonly TextBox username = Box();
    private readonly TextBox password = Box();
    private readonly TextBox keepAlive = Box();
    private readonly TextBox timeout = Box();
    private readonly TextBox retry = Box();
    private readonly TextBox telemetryInterval = Box();
    private readonly TextBox queue = Box();

    internal KstMqttSettingsForm(string path, KstMqttSettings settings)
    {
        this.path = path;
        originalQueueFile = settings.QueueFile;
        additionalDevices = new List<KstMqttDevice>(settings.AdditionalDevices);
        sourceDeviceCode = settings.SourceDeviceCode;
        Text = "S1 MQTT 配置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(650, 545);
        Font = new Font("Microsoft YaHei UI", 9F);
        enabled.Text = "启用科视数据转发到 S1";
        enabled.Checked = settings.Enabled;
        host.Text = settings.Host;
        port.Text = settings.Port.ToString(CultureInfo.InvariantCulture);
        product.Text = settings.ProductId;
        device.Text = settings.DeviceNumber;
        user.Text = settings.UserId;
        username.Text = settings.Username;
        password.Text = settings.Password;
        password.UseSystemPasswordChar = true;
        keepAlive.Text = settings.KeepAliveSeconds.ToString(CultureInfo.InvariantCulture);
        timeout.Text = settings.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        retry.Text = settings.RetryIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        telemetryInterval.Text = settings.TelemetryIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        queue.Text = settings.QueueFile;
        Build();
    }

    private void Build()
    {
        TableLayoutPanel table = new TableLayoutPanel();
        table.Dock = DockStyle.Top;
        table.Padding = new Padding(18);
        table.AutoSize = true;
        table.ColumnCount = 2;
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(table, "转发开关", enabled);
        Add(table, "Broker 主机", host);
        Add(table, "Broker 端口", port);
        Add(table, "产品 ID", product);
        Add(table, "设备编号", device);
        if (additionalDevices.Count > 0)
        {
            Label mappings = new Label();
            mappings.Text = "另有 " + additionalDevices.Count + " 台设备；在 kst_mqtt_config.json 的 additional_devices 中维护。";
            mappings.AutoSize = true;
            Add(table, "其他设备映射", mappings);
        }
        Add(table, "用户 ID", user);
        Add(table, "MQTT 用户名", username);
        Add(table, "MQTT 密码", password);
        Add(table, "保活秒数", keepAlive);
        Add(table, "请求超时秒数", timeout);
        Add(table, "失败重试秒数", retry);
        Add(table, "OEE/心跳上报间隔秒", telemetryInterval);
        Add(table, "本地队列文件", queue);
        Panel buttons = new Panel();
        buttons.Dock = DockStyle.Bottom;
        buttons.Height = 58;
        Button cancel = new Button();
        cancel.Text = "取消";
        cancel.DialogResult = DialogResult.Cancel;
        cancel.SetBounds(440, 12, 88, 32);
        Button save = new Button();
        save.Text = "保存";
        save.SetBounds(535, 12, 88, 32);
        save.Click += delegate { SaveSettings(); };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        Controls.Add(table);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void SaveSettings()
    {
        try
        {
            SaveSettingsCore();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void SaveSettingsCore()
    {
        KstMqttSettings settings = new KstMqttSettings();
        settings.Enabled = enabled.Checked;
        settings.Host = host.Text.Trim();
        settings.Port = Number(port, "Broker 端口");
        settings.ProductId = product.Text.Trim();
        settings.DeviceNumber = device.Text.Trim();
        settings.SourceDeviceCode = sourceDeviceCode;
        settings.UserId = user.Text.Trim();
        settings.Username = username.Text.Trim();
        settings.Password = password.Text;
        settings.AdditionalDevices.AddRange(additionalDevices);
        settings.KeepAliveSeconds = Number(keepAlive, "保活秒数");
        settings.RequestTimeoutSeconds = Number(timeout, "请求超时");
        settings.RetryIntervalSeconds = Number(retry, "失败重试");
        settings.TelemetryIntervalSeconds = Number(telemetryInterval, "OEE/心跳上报间隔");
        settings.QueueFile = queue.Text.Trim();
        settings.Validate();

        string configDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        string oldQueue = FullQueuePath(originalQueueFile, configDirectory);
        string newQueue = FullQueuePath(settings.QueueFile, configDirectory);
        settings.QueueFile = newQueue;
        List<string> copied = new List<string>();
        try
        {
            if (!string.Equals(oldQueue, newQueue, StringComparison.OrdinalIgnoreCase))
                CopyQueueFiles(oldQueue, newQueue, copied);
            settings.Save(path);
        }
        catch (Exception saveError)
        {
            Exception cleanupError = null;
            for (int index = copied.Count - 1; index >= 0; index--)
            {
                try { File.Delete(copied[index]); }
                catch (Exception error) { if (cleanupError == null) cleanupError = error; }
            }
            if (cleanupError != null)
                throw new IOException("保存失败，且新队列文件清理失败：" + cleanupError.Message + "；原错误：" + saveError.Message, saveError);
            throw;
        }
    }

    private static string FullQueuePath(string queuePath, string configDirectory)
    {
        return Path.GetFullPath(Path.IsPathRooted(queuePath) ? queuePath : Path.Combine(configDirectory, queuePath));
    }

    private static void CopyQueueFiles(string sourceQueue, string targetQueue, List<string> copied)
    {
        string sourceState = sourceQueue + ".rate-state.json";
        string targetState = targetQueue + ".rate-state.json";
        if (File.Exists(targetQueue) || File.Exists(targetState))
            throw new IOException("新队列文件或状态文件已存在，未覆盖：" + targetQueue);
        if (!File.Exists(sourceQueue) && !File.Exists(sourceState)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(targetQueue));
        if (File.Exists(sourceQueue)) CopyNewFile(sourceQueue, targetQueue, copied);
        if (File.Exists(sourceState)) CopyNewFile(sourceState, targetState, copied);
    }

    private static void CopyNewFile(string source, string target, List<string> copied)
    {
        using (FileStream sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream targetStream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            copied.Add(target);
            sourceStream.CopyTo(targetStream);
            targetStream.Flush();
        }
    }

    private static int Number(TextBox box, string name)
    {
        int value;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)) throw new ArgumentException(name + "必须是整数。");
        return value;
    }

    private static TextBox Box() { TextBox box = new TextBox(); box.Dock = DockStyle.Top; return box; }

    private static void Add(TableLayoutPanel table, string title, Control control)
    {
        Label label = new Label();
        label.Text = title;
        label.TextAlign = ContentAlignment.MiddleRight;
        label.Dock = DockStyle.Fill;
        control.Margin = new Padding(5);
        int row = table.RowCount++;
        table.Controls.Add(label, 0, row);
        table.Controls.Add(control, 1, row);
    }
}
