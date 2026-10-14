using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace JingdeEapForwarder
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (HasArgument(args, "--self-test")) return SelfTest.Run();
            string configPath = FindConfigPath(args);
            if (HasArgument(args, "--headless")) return RunHeadless(configPath);
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs eventArgs)
                {
                    ReportStartupFailure(eventArgs.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs)
                {
                    WriteStartupError(eventArgs.ExceptionObject);
                };
                Application.Run(new GatewayForm(configPath));
                return 0;
            }
            catch (Exception exception)
            {
                ReportStartupFailure(exception);
                return 1;
            }
        }

        private static void ReportStartupFailure(Exception exception)
        {
            WriteStartupError(exception);
            MessageBox.Show("LYR 设备数据监听启动失败：\r\n" + exception.Message +
                "\r\n\r\n详细错误已写入 JingdeEapForwarder-startup-errors.log。",
                "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private static void WriteStartupError(object exception)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JingdeEapForwarder-startup-errors.log");
                File.AppendAllText(path,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n" +
                    Convert.ToString(exception, CultureInfo.InvariantCulture) + "\r\n\r\n", Encoding.UTF8);
            }
            catch { }
        }

        private static int RunHeadless(string configPath)
        {
            GatewayRuntime runtime = null;
            try
            {
                runtime = new GatewayRuntime(configPath);
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    runtime.Stop();
                };
                runtime.Start();
                Console.WriteLine("LYR device listener is running. HTTP: " + runtime.CurrentSettings.ListenerSummary());
                runtime.WaitUntilStopped();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("LYR device listener failed: " + exception.Message);
                return 1;
            }
            finally
            {
                if (runtime != null) runtime.Stop();
            }
        }

        private static bool HasArgument(string[] args, string value)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string FindConfigPath(string[] args)
        {
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (string.Equals(args[index], "--config", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(args[index + 1]);
                }
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "JingdeEapForwarder.ini");
        }
    }

    internal sealed class Settings
    {
        public bool JingdeEnabled;
        public string JingdeListenPrefix;
        public HashSet<string> JingdeAllowedDeviceIps;
        public bool HosonEnabled;
        public string HosonListenPrefix;
        public HashSet<string> HosonAllowedDeviceIps;
        public bool KstEnabled;
        public string KstListenPrefix;
        public HashSet<string> KstAllowedDeviceIps;
        public int HosonScanResultIsNg;
        public string QmsBaseUrl;
        public string GatewayToken;
        public string TelemetryPath;
        public int BatchSize;
        public int RetryIntervalSeconds;
        public int RequestTimeoutSeconds;
        public int MaxRequestBytes;
        public bool UseTls12;
        public string QueueFile;
        public string LogFile;
        public int DisplayColumns;
        public int DisplayRows;
        public int RecordRetentionDays;
        public int IdleDisconnectSeconds;

        public static Settings Load(string configPath)
        {
            if (!File.Exists(configPath)) throw new FileNotFoundException("Configuration file was not found", configPath);
            Dictionary<string, string> values = IniFile.Read(configPath);
            string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            Settings settings = new Settings();
            // Preserve the original keys as fallback so existing Jingde deployments upgrade without manual edits.
            settings.JingdeEnabled = ReadBool(values, "JingdeEnabled", true);
            settings.JingdeListenPrefix = Read(values, "JingdeListenPrefix", Read(values, "ListenPrefix", "http://+:8002/")).Trim();
            settings.JingdeAllowedDeviceIps = ParseIpList(Read(values, "JingdeAllowedDeviceIps", Read(values, "AllowedDeviceIps", "")));
            settings.HosonEnabled = ReadBool(values, "HosonEnabled", true);
            settings.HosonListenPrefix = Read(values, "HosonListenPrefix", "http://+:6680/").Trim();
            settings.HosonAllowedDeviceIps = ParseIpList(Read(values, "HosonAllowedDeviceIps", ""));
            settings.KstEnabled = ReadBool(values, "KstEnabled", true);
            settings.KstListenPrefix = Read(values, "KstListenPrefix", "http://+:9000/").Trim();
            settings.KstAllowedDeviceIps = ParseIpList(Read(values, "KstAllowedDeviceIps", ""));
            settings.HosonScanResultIsNg = ReadInt(values, "HosonScanResultIsNg", -1, -1, 1);
            settings.QmsBaseUrl = Read(values, "QmsBaseUrl", "").Trim().TrimEnd('/');
            settings.GatewayToken = Read(values, "GatewayToken", "").Trim();
            settings.TelemetryPath = Read(values, "TelemetryPath", "/api/v1/equipment/telemetry").Trim();
            settings.BatchSize = ReadInt(values, "BatchSize", 50, 1, 200);
            settings.RetryIntervalSeconds = ReadInt(values, "RetryIntervalSeconds", 10, 1, 3600);
            settings.RequestTimeoutSeconds = ReadInt(values, "RequestTimeoutSeconds", 20, 1, 300);
            settings.MaxRequestBytes = ReadInt(values, "MaxRequestBytes", 1048576, 1024, 10485760);
            settings.UseTls12 = ReadBool(values, "UseTls12", true);
            settings.QueueFile = ResolvePath(baseDirectory, Read(values, "QueueFile", ".\\data\\pending-events.json"));
            settings.LogFile = ResolvePath(baseDirectory, Read(values, "LogFile", ".\\logs\\jingde-eap-forwarder.log"));
            settings.DisplayColumns = ReadInt(values, "DisplayColumns", 2, 1, 6);
            settings.DisplayRows = ReadInt(values, "DisplayRows", 2, 1, 12);
            settings.RecordRetentionDays = ReadInt(values, "RecordRetentionDays", 30, 1, 3650);
            settings.IdleDisconnectSeconds = ReadInt(values, "IdleDisconnectSeconds", 10, 1, 3600);
            settings.Validate();
            return settings;
        }

        public Settings Clone()
        {
            Settings copy = (Settings)MemberwiseClone();
            copy.JingdeAllowedDeviceIps = new HashSet<string>(JingdeAllowedDeviceIps, StringComparer.OrdinalIgnoreCase);
            copy.HosonAllowedDeviceIps = new HashSet<string>(HosonAllowedDeviceIps, StringComparer.OrdinalIgnoreCase);
            copy.KstAllowedDeviceIps = new HashSet<string>(KstAllowedDeviceIps, StringComparer.OrdinalIgnoreCase);
            return copy;
        }

        public void Validate()
        {
            if (JingdeEnabled) JingdeListenPrefix = NormalizeListenPrefix(JingdeListenPrefix, "精德 EAP 监听地址");
            if (HosonEnabled) HosonListenPrefix = NormalizeListenPrefix(HosonListenPrefix, "森泉喷印机监听地址");
            if (KstEnabled) KstListenPrefix = NormalizeListenPrefix(KstListenPrefix, "科视 LDI 监听地址");
            HashSet<string> prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ListenerChannel channel in GetListeners())
            {
                if (!prefixes.Add(channel.ListenPrefix)) throw new ArgumentException("三个设备协议不能使用相同监听地址：" + channel.ListenPrefix);
            }
            Uri qmsUri;
            if (!Uri.TryCreate(QmsBaseUrl, UriKind.Absolute, out qmsUri) ||
                (qmsUri.Scheme != Uri.UriSchemeHttp && qmsUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("QmsBaseUrl must be an absolute HTTP or HTTPS URL.");
            }
            if (string.IsNullOrEmpty(GatewayToken)) throw new ArgumentException("GatewayToken must be configured.");
        }

        public List<ListenerChannel> GetListeners()
        {
            List<ListenerChannel> listeners = new List<ListenerChannel>();
            if (JingdeEnabled) listeners.Add(new ListenerChannel("精德 EAP", "JINGDE_EAP", JingdeListenPrefix, JingdeAllowedDeviceIps));
            if (HosonEnabled) listeners.Add(new ListenerChannel("森泉喷印机", "HOSON_MES", HosonListenPrefix, HosonAllowedDeviceIps));
            if (KstEnabled) listeners.Add(new ListenerChannel("科视 LDI", "KST_LDI", KstListenPrefix, KstAllowedDeviceIps));
            return listeners;
        }

        public string ListenerSummary()
        {
            return "精德 " + (JingdeEnabled ? JingdeListenPrefix : "未启用") +
                "；森泉 " + (HosonEnabled ? HosonListenPrefix : "未启用") +
                "；科视 " + (KstEnabled ? KstListenPrefix : "未启用");
        }

        public void Save(string configPath)
        {
            Validate();
            StringBuilder text = new StringBuilder();
            text.AppendLine("; LYR 设备数据监听配置。由图形管理界面保存。");
            text.AppendLine("; 三类设备协议分别监听端口，修改后程序会自动重新加载并重启监听。");
            text.AppendLine();
            text.AppendLine("[LYR Listener]");
            text.AppendLine("; 精德 EAP：/RestAPI/ZJDataCollectionReport、/RestAPI/EQPAlarmReport、/RestAPI/EQPStatusChangeReport。");
            text.AppendLine("JingdeEnabled=" + JingdeEnabled.ToString().ToLowerInvariant());
            text.AppendLine("JingdeListenPrefix=" + JingdeListenPrefix);
            text.AppendLine("JingdeAllowedDeviceIps=" + JoinIpList(JingdeAllowedDeviceIps));
            text.AppendLine("; 森泉 PCB 文字喷印机：HTTP JSON method 协议；文档示例端口为 6680。");
            text.AppendLine("HosonEnabled=" + HosonEnabled.ToString().ToLowerInvariant());
            text.AppendLine("HosonListenPrefix=" + HosonListenPrefix);
            text.AppendLine("HosonAllowedDeviceIps=" + JoinIpList(HosonAllowedDeviceIps));
            text.AppendLine("; -1 表示未配置扫码判定时返回失败，避免擅自决定打印；0=需要打印，1=不需要打印。");
            text.AppendLine("HosonScanResultIsNg=" + HosonScanResultIsNg.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("; 科视 LDI：/Status、/OEE、/Alarm、/ExposureResult、/HeartBeat、/Mode；文档示例端口为 9000。");
            text.AppendLine("KstEnabled=" + KstEnabled.ToString().ToLowerInvariant());
            text.AppendLine("KstListenPrefix=" + KstListenPrefix);
            text.AppendLine("KstAllowedDeviceIps=" + JoinIpList(KstAllowedDeviceIps));
            text.AppendLine("MaxRequestBytes=" + MaxRequestBytes.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
            text.AppendLine("[QMS]");
            text.AppendLine("QmsBaseUrl=" + QmsBaseUrl);
            text.AppendLine("GatewayToken=" + GatewayToken);
            text.AppendLine("TelemetryPath=" + TelemetryPath);
            text.AppendLine("BatchSize=" + BatchSize.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("RetryIntervalSeconds=" + RetryIntervalSeconds.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("RequestTimeoutSeconds=" + RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("UseTls12=" + UseTls12.ToString().ToLowerInvariant());
            text.AppendLine();
            text.AppendLine("[Display]");
            text.AppendLine("DisplayColumns=" + DisplayColumns.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("DisplayRows=" + DisplayRows.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("RecordRetentionDays=" + RecordRetentionDays.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("IdleDisconnectSeconds=" + IdleDisconnectSeconds.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
            text.AppendLine("[Storage]");
            text.AppendLine("QueueFile=" + QueueFile);
            text.AppendLine("LogFile=" + LogFile);
            File.WriteAllText(configPath, text.ToString(), Encoding.UTF8);
        }

        public static HashSet<string> ParseAllowedDeviceIps(string input)
        {
            return ParseIpList(input ?? "");
        }

        private static string JoinIpList(HashSet<string> values)
        {
            List<string> sorted = new List<string>(values);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", sorted.ToArray());
        }

        private static HashSet<string> ParseIpList(string input)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in input.Split(new char[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(raw.Trim());
            }
            return result;
        }

        private static string ResolvePath(string baseDirectory, string value)
        {
            return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(baseDirectory, value));
        }

        private static string NormalizeListenPrefix(string value, string label)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException(label + "不能为空。");
            string normalized = value.Trim();
            if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(label + "必须以 http:// 或 https:// 开头。");
            }
            return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
        }

        private static string Read(Dictionary<string, string> values, string key, string defaultValue)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : defaultValue;
        }

        private static int ReadInt(Dictionary<string, string> values, string key, int defaultValue, int min, int max)
        {
            string raw;
            int value;
            if (!values.TryGetValue(key, out raw)) return defaultValue;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < min || value > max)
            {
                throw new ArgumentException(key + " must be an integer from " + min + " to " + max + ".");
            }
            return value;
        }

        private static bool ReadBool(Dictionary<string, string> values, string key, bool defaultValue)
        {
            string raw;
            bool value;
            if (!values.TryGetValue(key, out raw)) return defaultValue;
            if (!bool.TryParse(raw, out value)) throw new ArgumentException(key + " must be true or false.");
            return value;
        }
    }

    internal sealed class ListenerChannel
    {
        public readonly string Name;
        public readonly string Protocol;
        public readonly string ListenPrefix;
        public readonly HashSet<string> AllowedDeviceIps;

        public ListenerChannel(string name, string protocol, string listenPrefix, HashSet<string> allowedDeviceIps)
        {
            Name = name;
            Protocol = protocol;
            ListenPrefix = listenPrefix;
            AllowedDeviceIps = allowedDeviceIps == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(allowedDeviceIps, StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static class IniFile
    {
        public static Dictionary<string, string> Read(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string originalLine in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = originalLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";") || (line.StartsWith("[") && line.EndsWith("]"))) continue;
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }
            return values;
        }
    }

    internal sealed class GatewayLog
    {
        private readonly string filePath;
        private readonly int retentionDays;
        private readonly object sync = new object();

        public GatewayLog(string filePath, int retentionDays)
        {
            this.filePath = filePath;
            this.retentionDays = retentionDays;
        }
        public void Info(string message) { Write("INFO", message); }
        public void Warn(string message) { Write("WARN", message); }
        public void Error(string message) { Write("ERROR", message); }

        private void Write(string level, string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " [" + level + "] " + message;
            lock (sync)
            {
                try
                {
                    string directory = Path.GetDirectoryName(filePath);
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                    RotateAndCleanUnsafe();
                    File.AppendAllText(filePath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            Console.WriteLine(line);
        }

        private void RotateAndCleanUnsafe()
        {
            string directory = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string extension = Path.GetExtension(filePath);
            string archiveDirectory = Path.Combine(directory, "archive");
            DateTime today = DateTime.Today;
            if (File.Exists(filePath) && File.GetLastWriteTime(filePath).Date < today)
            {
                if (!Directory.Exists(archiveDirectory)) Directory.CreateDirectory(archiveDirectory);
                DateTime lastWrite = File.GetLastWriteTime(filePath);
                string archiveName = fileName + "-" + lastWrite.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + extension;
                string archivePath = Path.Combine(archiveDirectory, archiveName);
                int suffix = 1;
                while (File.Exists(archivePath))
                {
                    archivePath = Path.Combine(archiveDirectory, fileName + "-" + lastWrite.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + suffix + extension);
                    suffix++;
                }
                File.Move(filePath, archivePath);
            }
            if (!Directory.Exists(archiveDirectory)) return;
            DateTime cutoff = today.AddDays(-retentionDays);
            foreach (string archivePath in Directory.GetFiles(archiveDirectory, fileName + "-*" + extension))
            {
                try
                {
                    if (File.GetLastWriteTime(archivePath) < cutoff) File.Delete(archivePath);
                }
                catch { }
            }
        }
    }

    internal sealed class GatewayFlowEvent
    {
        public DateTime OccurredAt;
        public string Stage;
        public string Source;
        public string Method;
        public string Equipment;
        public string Summary;
        public string RawPayload;
        public string NormalizedPayload;

        public GatewayFlowEvent()
        {
            OccurredAt = DateTime.Now;
            Stage = "状态";
            Source = "-";
            Method = "-";
            Equipment = "-";
            Summary = "";
            RawPayload = "";
            NormalizedPayload = "";
        }
    }

    internal sealed class GatewayRuntime
    {
        private readonly string configPath;
        private readonly object sync = new object();
        private volatile bool stopping;
        private readonly List<HttpServer> servers = new List<HttpServer>();
        private Uploader uploader;
        private readonly List<Thread> listenerThreads = new List<Thread>();
        private Thread uploadThread;
        private PersistentQueue queue;
        private GatewayLog runtimeLog;
        private DateTime lastDeviceReceivedAt = DateTime.MinValue;

        public event Action<GatewayFlowEvent> FlowReceived;
        public Settings CurrentSettings { get; private set; }
        public int PendingCount { get { return queue == null ? 0 : queue.Count; } }
        public bool IsRunning { get; private set; }
        public DateTime LastDeviceReceivedAt { get { return lastDeviceReceivedAt; } }
        public bool IsDeviceIdle
        {
            get
            {
                return IsRunning && lastDeviceReceivedAt != DateTime.MinValue &&
                    DateTime.Now.Subtract(lastDeviceReceivedAt).TotalSeconds >= CurrentSettings.IdleDisconnectSeconds;
            }
        }

        public GatewayRuntime(string configPath)
        {
            this.configPath = configPath;
        }

        public void Start()
        {
            lock (sync)
            {
                if (IsRunning) return;
                CurrentSettings = Settings.Load(configPath);
                if (CurrentSettings.UseTls12)
                {
                    // TLS 1.2 is not named by the .NET 4.0 compiler, but its value is 3072.
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                }
                GatewayLog log = new GatewayLog(CurrentSettings.LogFile, CurrentSettings.RecordRetentionDays);
                runtimeLog = log;
                queue = new PersistentQueue(CurrentSettings.QueueFile, log);
                uploader = new Uploader(queue, new QmsClient(CurrentSettings), CurrentSettings, log, Publish);
                stopping = false;
                uploadThread = new Thread(new ThreadStart(delegate { uploader.Run(delegate { return stopping; }); }));
                uploadThread.IsBackground = true;
                uploadThread.Name = "lyr-qms-uploader";
                servers.Clear();
                listenerThreads.Clear();
                foreach (ListenerChannel channel in CurrentSettings.GetListeners())
                {
                    HttpServer listenerServer = new HttpServer(CurrentSettings, channel, queue, log, Publish);
                    servers.Add(listenerServer);
                    Thread listenerThread = new Thread(new ThreadStart(delegate { RunListener(listenerServer); }));
                    listenerThread.IsBackground = true;
                    listenerThread.Name = "lyr-" + channel.Protocol.ToLowerInvariant() + "-listener";
                    listenerThreads.Add(listenerThread);
                }
                IsRunning = true;
                uploadThread.Start();
                foreach (Thread listenerThread in listenerThreads) listenerThread.Start();
                Publish(new GatewayFlowEvent {
                    Stage = "服务启动",
                    Source = CurrentSettings.ListenerSummary(),
                    Summary = "已启动精德、森泉和科视三类设备监听，并转发到 " + CurrentSettings.QmsBaseUrl + "；当前待转发 " + queue.Count + " 条。"
                });
                log.Info("LYR device listener started. Listeners=" + CurrentSettings.ListenerSummary() + ", Qms=" + CurrentSettings.QmsBaseUrl);
            }
        }

        public void Stop()
        {
            List<Thread> waitListeners;
            Thread waitUploader;
            lock (sync)
            {
                if (!IsRunning && servers.Count == 0 && uploader == null) return;
                stopping = true;
                foreach (HttpServer server in servers) server.Stop();
                waitListeners = new List<Thread>(listenerThreads);
                waitUploader = uploadThread;
                IsRunning = false;
            }
            foreach (Thread waitListener in waitListeners)
            {
                if (waitListener != null && waitListener != Thread.CurrentThread) waitListener.Join(2000);
            }
            if (waitUploader != null && waitUploader != Thread.CurrentThread) waitUploader.Join(2000);
            if (uploader != null) uploader.FlushOnce();
            Publish(new GatewayFlowEvent { Stage = "服务停止", Summary = "监听已停止；待转发队列保留 " + PendingCount + " 条。" });
        }

        public void WaitUntilStopped()
        {
            while (IsRunning) Thread.Sleep(250);
        }

        public void FlushNow()
        {
            if (uploader != null) uploader.FlushOnce();
        }

        private void RunListener(HttpServer listenerServer)
        {
            try
            {
                listenerServer.Run(delegate { return stopping; });
            }
            catch (Exception exception)
            {
                Publish(new GatewayFlowEvent { Stage = "监听失败", Source = listenerServer.Channel.ListenPrefix,
                    Method = listenerServer.Channel.Name, Summary = exception.Message });
                if (runtimeLog != null) runtimeLog.Error("Listener failed. Protocol=" + listenerServer.Channel.Name +
                    ", Prefix=" + listenerServer.Channel.ListenPrefix + ", Error=" + exception.Message);
                // A port conflict for one protocol must not interrupt the other device listeners.
            }
        }

        private void Publish(GatewayFlowEvent flow)
        {
            if (flow != null && string.Equals(flow.Stage, "接收并入队", StringComparison.Ordinal)) lastDeviceReceivedAt = flow.OccurredAt;
            Action<GatewayFlowEvent> callback = FlowReceived;
            if (callback != null) callback(flow);
        }
    }

    internal sealed class PersistentQueue
    {
        private readonly string filePath;
        private readonly GatewayLog log;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
        private readonly object sync = new object();

        public event Action ItemEnqueued;

        public PersistentQueue(string filePath, GatewayLog log)
        {
            this.filePath = filePath;
            this.log = log;
            Load();
        }

        public int Count { get { lock (sync) { return items.Count; } } }

        public void Enqueue(Dictionary<string, object> item)
        {
            lock (sync)
            {
                items.Add(item);
                SaveUnsafe();
            }
            Action callback = ItemEnqueued;
            if (callback != null) callback();
        }

        public List<Dictionary<string, object>> Peek(int maximum)
        {
            lock (sync)
            {
                return items.GetRange(0, Math.Min(maximum, items.Count));
            }
        }

        public void RemoveFirst(int count)
        {
            lock (sync)
            {
                int actual = Math.Min(count, items.Count);
                if (actual == 0) return;
                items.RemoveRange(0, actual);
                SaveUnsafe();
            }
        }

        private void Load()
        {
            if (!File.Exists(filePath)) return;
            try
            {
                object raw = serializer.DeserializeObject(File.ReadAllText(filePath, Encoding.UTF8));
                ArrayList array = raw as ArrayList;
                if (array == null) throw new InvalidDataException("Queue root must be a JSON array.");
                foreach (object entry in array)
                {
                    Dictionary<string, object> item = entry as Dictionary<string, object>;
                    if (item == null) throw new InvalidDataException("Queue item must be a JSON object.");
                    items.Add(item);
                }
                log.Info("Recovered " + items.Count + " pending event(s) from the local queue.");
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("The local queue cannot be read. Preserve and repair it before restarting: " + filePath, exception);
            }
        }

        private void SaveUnsafe()
        {
            string directory = Path.GetDirectoryName(filePath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string temporaryPath = filePath + ".tmp";
            File.WriteAllText(temporaryPath, serializer.Serialize(items), Encoding.UTF8);
            File.Copy(temporaryPath, filePath, true);
            File.Delete(temporaryPath);
        }
    }

    internal sealed class QmsClient
    {
        private readonly Settings settings;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

        public QmsClient(Settings settings) { this.settings = settings; }

        public void Upload(List<Dictionary<string, object>> events)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["events"] = events;
            string url = settings.QmsBaseUrl + (settings.TelemetryPath.StartsWith("/") ? settings.TelemetryPath : "/" + settings.TelemetryPath);
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Accept = "application/json";
            request.Timeout = settings.RequestTimeoutSeconds * 1000;
            request.ReadWriteTimeout = settings.RequestTimeoutSeconds * 1000;
            request.Headers["X-Gateway-Token"] = settings.GatewayToken;
            byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(body));
            request.ContentLength = bytes.Length;
            using (Stream requestStream = request.GetRequestStream()) requestStream.Write(bytes, 0, bytes.Length);
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                    {
                        throw new WebException("QMS returned HTTP " + (int)response.StatusCode + ".");
                    }
                }
            }
            catch (WebException exception)
            {
                string details = "";
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)) details = reader.ReadToEnd();
                }
                throw new WebException("QMS request failed: " + exception.Message + (string.IsNullOrEmpty(details) ? "" : " | " + Truncate(details, 600)), exception);
            }
        }

        private static string Truncate(string value, int max)
        {
            return value != null && value.Length > max ? value.Substring(0, max) + "..." : value;
        }
    }

    internal sealed class Uploader
    {
        private readonly PersistentQueue queue;
        private readonly QmsClient qms;
        private readonly Settings settings;
        private readonly GatewayLog log;
        private readonly AutoResetEvent wakeSignal = new AutoResetEvent(false);
        private readonly Action<GatewayFlowEvent> publish;

        public Uploader(PersistentQueue queue, QmsClient qms, Settings settings, GatewayLog log, Action<GatewayFlowEvent> publish)
        {
            this.queue = queue;
            this.qms = qms;
            this.settings = settings;
            this.log = log;
            this.publish = publish;
            queue.ItemEnqueued += Signal;
        }

        public void Run(Func<bool> shouldStop)
        {
            while (!shouldStop())
            {
                FlushOnce();
                wakeSignal.WaitOne(settings.RetryIntervalSeconds * 1000);
            }
        }

        private void Signal()
        {
            wakeSignal.Set();
        }

        public void FlushOnce()
        {
            while (queue.Count > 0)
            {
                List<Dictionary<string, object>> batch = queue.Peek(settings.BatchSize);
                try
                {
                    qms.Upload(batch);
                    queue.RemoveFirst(batch.Count);
                    log.Info("Forwarded " + batch.Count + " event(s) to QMS. Pending=" + queue.Count);
                    Publish(new GatewayFlowEvent {
                        Stage = "转发成功",
                        Source = settings.QmsBaseUrl,
                        Summary = "已提交 " + batch.Count + " 条事件到 QMS；待转发 " + queue.Count + " 条。",
                        NormalizedPayload = new JavaScriptSerializer().Serialize(batch)
                    });
                }
                catch (Exception exception)
                {
                    log.Warn("QMS is unavailable; retained " + queue.Count + " pending event(s). " + exception.Message);
                    Publish(new GatewayFlowEvent {
                        Stage = "转发待重试",
                        Source = settings.QmsBaseUrl,
                        Summary = "QMS 暂不可用，已保留 " + queue.Count + " 条待转发数据：" + exception.Message,
                        NormalizedPayload = new JavaScriptSerializer().Serialize(batch)
                    });
                    return;
                }
            }
        }

        private void Publish(GatewayFlowEvent flow)
        {
            if (publish != null) publish(flow);
        }
    }

    internal sealed class HttpServer
    {
        private readonly Settings settings;
        private readonly ListenerChannel channel;
        private readonly PersistentQueue queue;
        private readonly GatewayLog log;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
        private readonly HttpListener listener = new HttpListener();
        private readonly Action<GatewayFlowEvent> publish;
        private readonly HosonTranslator hosonTranslator;

        public ListenerChannel Channel { get { return channel; } }

        public HttpServer(Settings settings, ListenerChannel channel, PersistentQueue queue, GatewayLog log, Action<GatewayFlowEvent> publish)
        {
            this.settings = settings;
            this.channel = channel;
            this.queue = queue;
            this.log = log;
            this.publish = publish;
            hosonTranslator = new HosonTranslator(settings.HosonScanResultIsNg);
            serializer.MaxJsonLength = settings.MaxRequestBytes;
            listener.Prefixes.Add(channel.ListenPrefix);
        }

        public void Run(Func<bool> shouldStop)
        {
            listener.Start();
            while (!shouldStop())
            {
                HttpListenerContext context = null;
                try
                {
                    context = listener.GetContext();
                    HttpListenerContext acceptedContext = context;
                    ThreadPool.QueueUserWorkItem(delegate { Handle(acceptedContext); });
                }
                catch (HttpListenerException)
                {
                    if (!shouldStop()) throw;
                }
                catch (ObjectDisposedException)
                {
                    if (!shouldStop()) throw;
                }
            }
        }

        public void Stop()
        {
            try { listener.Close(); } catch { }
        }

        private void Handle(HttpListenerContext context)
        {
            string sourceIp = context.Request.RemoteEndPoint == null ? "unknown" : context.Request.RemoteEndPoint.Address.ToString();
            Dictionary<string, object> envelope = null;
            try
            {
                if (channel.AllowedDeviceIps.Count > 0 && !channel.AllowedDeviceIps.Contains(sourceIp))
                {
                    log.Warn("Rejected request from non-allowlisted IP " + sourceIp + ".");
                    WriteFailure(context, 403, null, "来源设备 IP 未被允许", "Source device IP is not allowed");
                    return;
                }
                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    WriteFailure(context, 405, null, "仅支持 POST 请求", "Only POST is supported");
                    return;
                }
                if (context.Request.ContentLength64 > settings.MaxRequestBytes)
                {
                    throw new ProtocolException("请求体超过最大长度 " + settings.MaxRequestBytes + " 字节。");
                }

                string raw;
                using (StreamReader reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                {
                    raw = reader.ReadToEnd();
                }
                if (Encoding.UTF8.GetByteCount(raw) > settings.MaxRequestBytes) throw new ProtocolException("请求体超过最大长度。");
                envelope = serializer.DeserializeObject(raw) as Dictionary<string, object>;
                if (envelope == null) throw new ProtocolException("请求体必须是 JSON 对象。");

                DateTime receivedAt = DateTime.Now;
                ProtocolDispatchResult result = Dispatch(context.Request.Url == null ? "" : context.Request.Url.AbsolutePath, envelope, sourceIp, receivedAt);
                if (result.EventItem != null) queue.Enqueue(result.EventItem);
                string equipment = !string.IsNullOrEmpty(result.Equipment) ? result.Equipment : GetValue(result.EventItem, "machineName");
                string recordNo = GetValue(result.EventItem, "recordNo");
                log.Info("Accepted " + channel.Name + " " + result.Method + " from " + sourceIp + ", equipment=" + equipment + ", record=" + recordNo + ", pending=" + queue.Count);
                Publish(new GatewayFlowEvent {
                    OccurredAt = receivedAt,
                    Stage = result.EventItem == null ? "请求已响应" : "接收并入队",
                    Source = channel.Name + " / " + sourceIp,
                    Method = result.Method,
                    Equipment = equipment,
                    Summary = result.EventItem == null
                        ? "已按协议返回响应。"
                        : "已写入本地队列，记录号 " + recordNo + "；待转发 " + queue.Count + " 条。",
                    RawPayload = Truncate(raw, 120000),
                    NormalizedPayload = result.EventItem == null ? serializer.Serialize(result.ResponsePayload) : serializer.Serialize(result.EventItem)
                });
                WriteSuccess(context, result);
            }
            catch (ProtocolException exception)
            {
                log.Warn("Rejected request from " + sourceIp + ": " + exception.Message);
                Publish(new GatewayFlowEvent { Stage = "请求拒绝", Source = channel.Name + " / " + sourceIp, Summary = exception.Message });
                WriteFailure(context, 400, envelope, exception.Message, exception.Message);
            }
            catch (ArgumentException exception)
            {
                log.Warn("Rejected malformed request from " + sourceIp + ": " + exception.Message);
                Publish(new GatewayFlowEvent { Stage = "请求拒绝", Source = channel.Name + " / " + sourceIp, Summary = exception.Message });
                WriteFailure(context, 400, envelope, "请求格式不正确", exception.Message);
            }
            catch (Exception exception)
            {
                log.Error("Could not accept request from " + sourceIp + ": " + exception.Message);
                Publish(new GatewayFlowEvent { Stage = "接收失败", Source = channel.Name + " / " + sourceIp, Summary = exception.Message });
                WriteFailure(context, 500, envelope, "网关暂时无法保存数据", "Gateway could not persist the data");
            }
        }

        private ProtocolDispatchResult Dispatch(string path, Dictionary<string, object> envelope, string sourceIp, DateTime receivedAt)
        {
            if (string.Equals(channel.Protocol, "JINGDE_EAP", StringComparison.Ordinal))
            {
                string method = MethodFromPath(path);
                Dictionary<string, object> eventItem = EapTranslator.Translate(method, envelope, sourceIp, receivedAt);
                Dictionary<string, object> header = EapTranslator.FindDictionary(envelope, "Header");
                return ProtocolDispatchResult.ForJingde(method, eventItem, header);
            }
            if (string.Equals(channel.Protocol, "HOSON_MES", StringComparison.Ordinal))
            {
                return hosonTranslator.Translate(envelope, sourceIp, receivedAt);
            }
            if (string.Equals(channel.Protocol, "KST_LDI", StringComparison.Ordinal))
            {
                string method = KstTranslator.MethodFromPath(path);
                Dictionary<string, object> eventItem = KstTranslator.Translate(method, envelope, sourceIp, receivedAt);
                return ProtocolDispatchResult.ForJson(method, eventItem, KstTranslator.SuccessResponse(method));
            }
            throw new ProtocolException("未配置的监听协议：" + channel.Protocol);
        }

        private static string MethodFromPath(string path)
        {
            string normalized = (path ?? "").TrimEnd('/');
            const string prefix = "/RestAPI/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProtocolException("请求地址必须为 /RestAPI/{method}。");
            }
            string method = normalized.Substring(prefix.Length);
            if (string.Equals(method, "ZJDataCollectionReport", StringComparison.OrdinalIgnoreCase)) return "ZJDataCollectionReport";
            if (string.Equals(method, "EQPAlarmReport", StringComparison.OrdinalIgnoreCase)) return "EQPAlarmReport";
            if (string.Equals(method, "EQPStatusChangeReport", StringComparison.OrdinalIgnoreCase)) return "EQPStatusChangeReport";
            throw new ProtocolException("不支持的接口方法：" + method);
        }

        private void WriteSuccess(HttpListenerContext context, ProtocolDispatchResult result)
        {
            if (string.Equals(channel.Protocol, "JINGDE_EAP", StringComparison.Ordinal))
            {
                Dictionary<string, object> header = result.JingdeHeader;
                WriteJingdeResult(context, 200, GetString(header, "MessageName"), GetString(header, "TransactionID"),
                    GetString(header, "UserID"), 1, "", "");
                return;
            }
            WriteJson(context, 200, result.ResponsePayload);
        }

        private void WriteFailure(HttpListenerContext context, int statusCode, Dictionary<string, object> envelope, string messageCh, string messageEn)
        {
            if (string.Equals(channel.Protocol, "JINGDE_EAP", StringComparison.Ordinal))
            {
                Dictionary<string, object> header = envelope == null ? null : EapTranslator.FindDictionary(envelope, "Header");
                WriteJingdeResult(context, statusCode, GetString(header, "MessageName"), GetString(header, "TransactionID"),
                    GetString(header, "UserID"), 0, messageCh, messageEn);
                return;
            }
            if (string.Equals(channel.Protocol, "HOSON_MES", StringComparison.Ordinal))
            {
                WriteJson(context, statusCode, HosonTranslator.FailureResponse(envelope, messageCh));
                return;
            }
            WriteJson(context, statusCode, KstTranslator.FailureResponse(messageCh));
        }

        private void WriteJingdeResult(HttpListenerContext context, int statusCode, string messageName, string transactionId, string userId, int resultCode, string messageCh, string messageEn)
        {
            try
            {
                Dictionary<string, object> header = new Dictionary<string, object>();
                if (!string.IsNullOrEmpty(messageName)) header["MessageName"] = messageName;
                if (!string.IsNullOrEmpty(transactionId)) header["TransactionID"] = transactionId;
                if (!string.IsNullOrEmpty(userId)) header["UserID"] = userId;
                Dictionary<string, object> result = new Dictionary<string, object>();
                result["Code"] = resultCode;
                result["MessageCH"] = messageCh ?? "";
                result["MessageEN"] = messageEn ?? "";
                Dictionary<string, object> response = new Dictionary<string, object>();
                response["Header"] = header;
                response["Body"] = new Dictionary<string, object>();
                response["Result"] = result;
                byte[] output = Encoding.UTF8.GetBytes(serializer.Serialize(response));
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.ContentLength64 = output.Length;
                context.Response.OutputStream.Write(output, 0, output.Length);
                context.Response.OutputStream.Close();
            }
            catch { }
        }

        private void WriteJson(HttpListenerContext context, int statusCode, Dictionary<string, object> response)
        {
            try
            {
                byte[] output = Encoding.UTF8.GetBytes(serializer.Serialize(response ?? new Dictionary<string, object>()));
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentEncoding = Encoding.UTF8;
                context.Response.ContentLength64 = output.Length;
                context.Response.OutputStream.Write(output, 0, output.Length);
                context.Response.OutputStream.Close();
            }
            catch { }
        }

        private static string GetValue(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null) return "";
            object value;
            return dictionary.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        private static string GetString(Dictionary<string, object> dictionary, string key)
        {
            if (dictionary == null) return null;
            object value;
            return dictionary.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private void Publish(GatewayFlowEvent flow)
        {
            if (publish != null) publish(flow);
        }

        private static string Truncate(string text, int maximum)
        {
            return text != null && text.Length > maximum ? text.Substring(0, maximum) + "\r\n...（已截断）" : text;
        }
    }

    internal sealed class ProtocolDispatchResult
    {
        public string Method;
        public string Equipment;
        public Dictionary<string, object> EventItem;
        public Dictionary<string, object> ResponsePayload;
        public Dictionary<string, object> JingdeHeader;

        public static ProtocolDispatchResult ForJingde(string method, Dictionary<string, object> eventItem, Dictionary<string, object> header)
        {
            ProtocolDispatchResult result = new ProtocolDispatchResult();
            result.Method = method;
            result.EventItem = eventItem;
            result.JingdeHeader = header;
            return result;
        }

        public static ProtocolDispatchResult ForJson(string method, Dictionary<string, object> eventItem, Dictionary<string, object> responsePayload)
        {
            ProtocolDispatchResult result = new ProtocolDispatchResult();
            result.Method = method;
            result.EventItem = eventItem;
            result.ResponsePayload = responsePayload;
            return result;
        }
    }

    internal sealed class HosonTranslator
    {
        private readonly int scanResultIsNg;

        public HosonTranslator(int scanResultIsNg)
        {
            this.scanResultIsNg = scanResultIsNg;
        }

        public ProtocolDispatchResult Translate(Dictionary<string, object> envelope, string sourceIp, DateTime receivedAt)
        {
            string method = RequireString(envelope, "method");
            Dictionary<string, object> data = RequireDictionary(envelope, "data");
            string normalizedMethod = method.Trim().ToLowerInvariant();
            string equipment;
            if (normalizedMethod == "login")
            {
                equipment = RequireString(data, "device_code");
                Dictionary<string, object> attributes = NewAttributes(method, envelope, data, sourceIp, receivedAt);
                attributes["hostIp"] = GetString(data, "host_ip");
                Dictionary<string, object> loginEvent = NewEvent(equipment, DeviceTime(envelope, receivedAt), "DEVICE_LOGIN", RecordNo(method, envelope), attributes);
                Dictionary<string, object> loginData = new Dictionary<string, object>();
                loginData["token"] = CreateToken(equipment);
                ProtocolDispatchResult loginResult = ProtocolDispatchResult.ForJson(method, loginEvent,
                    Response(envelope, method, true, "成功", 0, loginData));
                loginResult.Equipment = equipment;
                return loginResult;
            }

            equipment = DeviceFromToken(RequireString(envelope, "token"));
            Dictionary<string, object> eventAttributes = NewAttributes(method, envelope, data, sourceIp, receivedAt);
            Dictionary<string, object> eventItem;
            if (normalizedMethod == "device_status")
            {
                string rawStatus = RequireString(data, "status");
                eventItem = NewEvent(equipment, RequireTime(data, "time"), "STATUS_CHANGED", RecordNo(method, envelope), eventAttributes);
                eventItem["status"] = MapDeviceStatus(rawStatus);
                eventItem["alarmMessage"] = GetString(data, "message");
                Dictionary<string, object> errorInfo = FindDictionary(data, "error_info");
                if (errorInfo != null)
                {
                    eventItem["alarmCode"] = GetString(errorInfo, "error_code");
                    if (string.IsNullOrEmpty(GetString(data, "message"))) eventItem["alarmMessage"] = GetString(errorInfo, "message");
                }
            }
            else if (normalizedMethod == "feed_material")
            {
                eventItem = NewEvent(equipment, DeviceTime(envelope, receivedAt), "MATERIAL_FEED", RecordNo(method, envelope), eventAttributes);
                eventItem["workOrderNo"] = RequireString(data, "lot_no");
                eventAttributes["feedStatus"] = RequireString(data, "feed_status");
            }
            else if (normalizedMethod == "scan_result")
            {
                eventItem = NewEvent(equipment, DeviceTime(envelope, receivedAt), "SCAN_RESULT", RecordNo(method, envelope), eventAttributes);
                eventAttributes["scanResult"] = RequireString(data, "result");
                eventAttributes["scanContent"] = GetString(data, "scan_content");
                ProtocolDispatchResult scanResult = ProtocolDispatchResult.ForJson(method, eventItem, ScanResponse(envelope, method));
                scanResult.Equipment = equipment;
                return scanResult;
            }
            else if (normalizedMethod == "return_labels_result")
            {
                string printResult = RequireString(data, "print_result");
                eventItem = NewEvent(equipment, TimeOrFallback(data, "end_time", receivedAt),
                    printResult == "1" ? "MACHINING_COMPLETED" : "PRINT_RESULT", RecordNo(method, envelope), eventAttributes);
                if (printResult == "1") eventItem["status"] = "COMPLETED";
                eventItem["workOrderNo"] = GetString(data, "lot_no");
                eventItem["alarmCode"] = GetString(data, "error_code");
                eventItem["alarmMessage"] = GetString(data, "error_message");
            }
            else if (normalizedMethod == "carft_param" || normalizedMethod == "craft_param")
            {
                eventItem = NewEvent(equipment, DeviceTime(envelope, receivedAt), "RECIPE_REPORTED", RecordNo(method, envelope), eventAttributes);
            }
            else if (normalizedMethod == "version_info")
            {
                eventItem = NewEvent(equipment, DeviceTime(envelope, receivedAt), "VERSION_REPORTED", RecordNo(method, envelope), eventAttributes);
            }
            else if (normalizedMethod == "get_job_name_by_scan" || normalizedMethod == "check_job_info" ||
                     normalizedMethod == "build_job_info" || normalizedMethod == "get_labels" || normalizedMethod == "get_job_path_by_scan")
            {
                throw new ProtocolException("森泉方法 “" + method + "” 需要 MES 下发业务数据，LYR 监听只保存设备上传数据，尚未配置对应业务响应。");
            }
            else
            {
                throw new ProtocolException("不支持的森泉 method：" + method);
            }
            ProtocolDispatchResult result = ProtocolDispatchResult.ForJson(method, eventItem, Response(envelope, method, true, "成功", 0, null));
            result.Equipment = equipment;
            return result;
        }

        public static Dictionary<string, object> FailureResponse(Dictionary<string, object> envelope, string message)
        {
            string method = envelope == null ? "" : GetString(envelope, "method");
            return Response(envelope, method, false, message, 4001, null);
        }

        private Dictionary<string, object> ScanResponse(Dictionary<string, object> envelope, string method)
        {
            if (scanResultIsNg < 0)
            {
                return Response(envelope, method, false, "未配置扫码判定，拒绝擅自决定打印或下料", 4002, null);
            }
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["is_ng"] = scanResultIsNg;
            return Response(envelope, method, true, "成功", 0, data);
        }

        private static Dictionary<string, object> Response(Dictionary<string, object> envelope, string method, bool success, string message, int errorCode, object data)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["id"] = envelope == null ? "" : GetString(envelope, "id");
            response["timestamp"] = EpochMilliseconds();
            response["version"] = envelope == null ? "1.0.0.2" : (GetString(envelope, "version") ?? "1.0.0.2");
            response["method"] = method ?? "";
            response["success"] = success;
            response["message"] = message ?? "";
            response["error_code"] = errorCode;
            response["data"] = data;
            return response;
        }

        private static Dictionary<string, object> NewAttributes(string method, Dictionary<string, object> envelope,
                                                                  Dictionary<string, object> data, string sourceIp, DateTime receivedAt)
        {
            Dictionary<string, object> attributes = new Dictionary<string, object>();
            attributes["sourceProtocol"] = "HOSON_MES_HTTP_V1";
            attributes["hosonMethod"] = method;
            attributes["sourceIp"] = sourceIp;
            attributes["receivedAt"] = receivedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            attributes["requestData"] = data;
            attributes["rawHosonEnvelope"] = envelope;
            return attributes;
        }

        private static Dictionary<string, object> NewEvent(string equipment, DateTime deviceTime, string eventType,
                                                             string recordNo, Dictionary<string, object> attributes)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["username"] = equipment;
            result["machineName"] = equipment;
            result["deviceTime"] = deviceTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            result["eventType"] = eventType;
            result["recordNo"] = "HOSON-" + recordNo;
            result["attributes"] = attributes;
            return result;
        }

        private static string RecordNo(string method, Dictionary<string, object> envelope)
        {
            return method + "-" + RequireString(envelope, "id");
        }

        private static string CreateToken(string equipment)
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(equipment)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "LYR-" + encoded;
        }

        private static string DeviceFromToken(string token)
        {
            if (string.IsNullOrEmpty(token) || !token.StartsWith("LYR-", StringComparison.Ordinal))
            {
                throw new ProtocolException("森泉 token 无效，请重新调用 login 获取 LYR token。");
            }
            try
            {
                string encoded = token.Substring(4).Replace('-', '+').Replace('_', '/');
                while (encoded.Length % 4 != 0) encoded += "=";
                string equipment = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                if (string.IsNullOrEmpty(equipment)) throw new FormatException();
                return equipment;
            }
            catch
            {
                throw new ProtocolException("森泉 token 无效，请重新调用 login 获取 LYR token。");
            }
        }

        private static string MapDeviceStatus(string value)
        {
            if (value == "1") return "PROCESSING";
            if (value == "2") return "ALARM";
            if (value == "3") return "IDLE";
            throw new ProtocolException("森泉设备状态仅支持 1(运行)、2(故障)、3(待机)。");
        }

        private static DateTime DeviceTime(Dictionary<string, object> envelope, DateTime fallback)
        {
            long milliseconds;
            if (long.TryParse(GetString(envelope, "timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out milliseconds))
            {
                try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds).ToLocalTime(); }
                catch (ArgumentOutOfRangeException) { }
            }
            return fallback;
        }

        private static DateTime RequireTime(Dictionary<string, object> source, string key)
        {
            return ParseTime(RequireString(source, key), key);
        }

        private static DateTime TimeOrFallback(Dictionary<string, object> source, string key, DateTime fallback)
        {
            string value = GetString(source, key);
            return string.IsNullOrEmpty(value) ? fallback : ParseTime(value, key);
        }

        private static DateTime ParseTime(string value, string key)
        {
            DateTime parsed;
            if (!DateTime.TryParseExact(value.Trim(), new string[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                throw new ProtocolException("字段 " + key + " 必须为 yyyy-MM-dd HH:mm:ss 时间格式。");
            }
            return parsed;
        }

        private static long EpochMilliseconds()
        {
            return (long)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private static Dictionary<string, object> RequireDictionary(Dictionary<string, object> source, string key)
        {
            Dictionary<string, object> value = FindDictionary(source, key);
            if (value == null) throw new ProtocolException("缺少对象字段 " + key + "。");
            return value;
        }

        private static Dictionary<string, object> FindDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            return TryGetValue(source, key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string RequireString(Dictionary<string, object> source, string key)
        {
            string value = GetString(source, key);
            if (string.IsNullOrEmpty(value)) throw new ProtocolException("缺少字段 " + key + "。");
            return value.Trim();
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            object value;
            return TryGetValue(source, key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static bool TryGetValue(Dictionary<string, object> source, string key, out object value)
        {
            if (source != null && source.TryGetValue(key, out value)) return true;
            if (source != null)
            {
                foreach (KeyValuePair<string, object> pair in source)
                {
                    if (string.Equals(pair.Key.Trim(), key, StringComparison.OrdinalIgnoreCase)) { value = pair.Value; return true; }
                }
            }
            value = null;
            return false;
        }
    }

    internal static class KstTranslator
    {
        public static string MethodFromPath(string path)
        {
            string method = (path ?? "").Trim('/');
            if (string.Equals(method, "Status", StringComparison.OrdinalIgnoreCase)) return "Status";
            if (string.Equals(method, "OEE", StringComparison.OrdinalIgnoreCase)) return "OEE";
            if (string.Equals(method, "Alarm", StringComparison.OrdinalIgnoreCase)) return "Alarm";
            if (string.Equals(method, "ExposureResult", StringComparison.OrdinalIgnoreCase)) return "ExposureResult";
            if (string.Equals(method, "HeartBeat", StringComparison.OrdinalIgnoreCase)) return "HeartBeat";
            if (string.Equals(method, "Mode", StringComparison.OrdinalIgnoreCase)) return "Mode";
            throw new ProtocolException("不支持的科视 LDI 地址：/" + method);
        }

        public static Dictionary<string, object> Translate(string method, Dictionary<string, object> payload, string sourceIp, DateTime receivedAt)
        {
            string equipment = string.Equals(method, "ExposureResult", StringComparison.Ordinal)
                ? RequireString(payload, "CMACHINE_CODE") : RequireString(payload, "MachineNO");
            DateTime deviceTime = ResolveDeviceTime(method, payload, receivedAt);
            string recordKey = ResolveRecordKey(method, payload, equipment, deviceTime);
            Dictionary<string, object> attributes = new Dictionary<string, object>();
            attributes["sourceProtocol"] = "KST_LDI_HTTP_V1";
            attributes["kstMethod"] = method;
            attributes["sourceIp"] = sourceIp;
            attributes["receivedAt"] = receivedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            attributes["rawKstPayload"] = payload;
            Dictionary<string, object> result = NewEvent(equipment, deviceTime, method, recordKey, attributes);

            if (string.Equals(method, "Status", StringComparison.Ordinal))
            {
                result["eventType"] = "STATUS_CHANGED";
                result["status"] = MapStatus(RequireString(payload, "Status"));
                result["operatorName"] = RequireString(payload, "UserName");
                result["batchNo"] = RequireString(payload, "LotNO");
                result["materialNo"] = RequireString(payload, "CBARCODE");
            }
            else if (string.Equals(method, "OEE", StringComparison.Ordinal))
            {
                result["eventType"] = "OEE_REPORTED";
                result["operatorName"] = RequireString(payload, "UserName");
                result["workOrderNo"] = RequireString(payload, "OrderNO");
                result["materialNo"] = RequireString(payload, "ItemNO");
                result["batchNo"] = RequireString(payload, "Barcode");
                attributes["oee"] = RequireString(payload, "OEE");
                attributes["startTime"] = RequireString(payload, "StartTime");
                attributes["endTime"] = RequireString(payload, "EndTime");
            }
            else if (string.Equals(method, "Alarm", StringComparison.Ordinal))
            {
                result["eventType"] = "ALARM_RAISED";
                result["status"] = "ALARM";
                result["alarmCode"] = RequireString(payload, "EventNO");
                result["alarmMessage"] = RequireString(payload, "EventMessage");
                result["operatorName"] = RequireString(payload, "UserName");
                result["workOrderNo"] = RequireString(payload, "OrderNO");
                result["materialNo"] = RequireString(payload, "ItemNO");
                result["batchNo"] = RequireString(payload, "Barcode");
                attributes["level"] = RequireString(payload, "Level");
                attributes["eventId"] = RequireString(payload, "EventID");
            }
            else if (string.Equals(method, "ExposureResult", StringComparison.Ordinal))
            {
                string exposureResult = RequireString(payload, "CRESULT");
                result["eventType"] = exposureResult == "OK" ? "MACHINING_COMPLETED" : "EXPOSURE_RESULT";
                if (exposureResult == "OK") result["status"] = "COMPLETED";
                result["operatorName"] = RequireString(payload, "CUSER_NAME");
                result["workOrderNo"] = RequireString(payload, "CORDER_NO");
                result["materialNo"] = RequireString(payload, "CITEM_NO");
                result["batchNo"] = RequireString(payload, "CLOT_NO");
                attributes["barcode"] = RequireString(payload, "CBARCODE");
                attributes["result"] = exposureResult;
                attributes["layer"] = RequireString(payload, "CLAYER");
                attributes["face"] = RequireString(payload, "CFACE");
            }
            else if (string.Equals(method, "HeartBeat", StringComparison.Ordinal))
            {
                result["eventType"] = "HEARTBEAT";
                attributes["heartBeat"] = RequireString(payload, "HeartBeat");
            }
            else if (string.Equals(method, "Mode", StringComparison.Ordinal))
            {
                result["eventType"] = "MODE_CHANGED";
                result["operatorName"] = RequireString(payload, "UserName");
                attributes["mode"] = RequireString(payload, "Mode");
            }
            return result;
        }

        public static Dictionary<string, object> SuccessResponse(string method)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["success"] = true;
            response["message"] = "accepted";
            response["method"] = method;
            return response;
        }

        public static Dictionary<string, object> FailureResponse(string message)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["success"] = false;
            response["message"] = message ?? "rejected";
            return response;
        }

        private static Dictionary<string, object> NewEvent(string equipment, DateTime deviceTime, string method, string recordKey, Dictionary<string, object> attributes)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["username"] = equipment;
            result["machineName"] = equipment;
            result["deviceTime"] = deviceTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            result["eventType"] = method;
            result["recordNo"] = "KST-" + method + "-" + recordKey;
            result["attributes"] = attributes;
            return result;
        }

        private static string ResolveRecordKey(string method, Dictionary<string, object> payload, string equipment, DateTime deviceTime)
        {
            string id = GetString(payload, "ID");
            if (!string.IsNullOrEmpty(id)) return id;
            string eventId = GetString(payload, "EventID");
            if (!string.IsNullOrEmpty(eventId)) return eventId;
            return equipment + "-" + (GetString(payload, "CBARCODE") ?? "NO_BARCODE") + "-" + deviceTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        private static DateTime ResolveDeviceTime(string method, Dictionary<string, object> payload, DateTime fallback)
        {
            string value;
            if (string.Equals(method, "OEE", StringComparison.Ordinal)) value = GetString(payload, "EndTime");
            else if (string.Equals(method, "ExposureResult", StringComparison.Ordinal)) value = GetString(payload, "CEND_TIME");
            else value = GetString(payload, "MachineTime");
            if (string.IsNullOrEmpty(value)) return fallback;
            DateTime parsed;
            if (!DateTime.TryParseExact(value.Trim(), new string[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" },
                CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                throw new ProtocolException("科视时间字段必须为 yyyy-MM-dd HH:mm:ss。");
            }
            return parsed;
        }

        private static string MapStatus(string status)
        {
            string value = status.Trim().ToUpperInvariant();
            if (value == "WORK") return "PROCESSING";
            if (value == "WAIT") return "IDLE";
            if (value == "STOP") return "STOPPED";
            if (value == "ALARM") return "ALARM";
            throw new ProtocolException("科视 Status 仅支持 WORK、WAIT、STOP 或 ALARM。");
        }

        private static string RequireString(Dictionary<string, object> source, string key)
        {
            string value = GetString(source, key);
            if (string.IsNullOrEmpty(value)) throw new ProtocolException("缺少科视字段 " + key + "。");
            return value.Trim();
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            object value;
            if (source != null && source.TryGetValue(key, out value) && value != null) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }
    }

    internal static class EapTranslator
    {
        public static Dictionary<string, object> Translate(string method, Dictionary<string, object> envelope, string sourceIp, DateTime receivedAt)
        {
            Dictionary<string, object> header = RequireDictionary(envelope, "Header");
            Dictionary<string, object> body = RequireDictionary(envelope, "Body");
            string messageName = RequireString(header, "MessageName");
            if (!string.Equals(messageName, method, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProtocolException("Header.MessageName 与 URL 中的方法不一致。");
            }
            string transactionId = RequireString(header, "TransactionID");
            string equipmentId = RequireString(body, "EquipmentID");
            string userId = GetString(header, "UserID");
            DateTime deviceTime = TimestampFromTransaction(transactionId, receivedAt);

            Dictionary<string, object> attributes = new Dictionary<string, object>();
            attributes["sourceProtocol"] = "JINGDE_EAP_HTTP_V1";
            attributes["eapMethod"] = method;
            attributes["transactionId"] = transactionId;
            attributes["userId"] = userId;
            attributes["receivedAt"] = receivedAt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
            attributes["sourceIp"] = sourceIp;
            attributes["rawEapEnvelope"] = envelope;

            Dictionary<string, object> eventItem = NewEvent(equipmentId, deviceTime, method, transactionId, attributes);
            if (string.Equals(method, "ZJDataCollectionReport", StringComparison.OrdinalIgnoreCase))
            {
                string lotId = GetString(body, "LotID");
                string startTime = GetString(body, "StartTime");
                string endTime = GetString(body, "EndTime");
                DateTime parsedProductionTime;
                if (TryParseTime(endTime, out parsedProductionTime) || TryParseTime(startTime, out parsedProductionTime))
                {
                    eventItem["deviceTime"] = parsedProductionTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                }
                eventItem["eventType"] = "PRODUCTION_PARAMETERS";
                eventItem["batchNo"] = lotId;
                eventItem["operatorName"] = userId;
                attributes["lotId"] = lotId;
                attributes["startTime"] = startTime;
                attributes["endTime"] = endTime;
                attributes["productionTime"] = GetString(body, "ProductionTime");
                attributes["paramList"] = GetValue(body, "ParamList");
                attributes["breakToolList"] = GetValue(body, "BreakToolList");
            }
            else if (string.Equals(method, "EQPAlarmReport", StringComparison.OrdinalIgnoreCase))
            {
                string alarmStatus = RequireString(body, "AlarmStatus");
                if (alarmStatus == "1")
                {
                    eventItem["eventType"] = "ALARM_RAISED";
                    eventItem["status"] = "ALARM";
                }
                else if (alarmStatus == "2")
                {
                    eventItem["eventType"] = "ALARM_CLEARED";
                    // Alarm clearance is deliberately status-less. The subsequent status-change message remains the authoritative runtime state.
                }
                else
                {
                    throw new ProtocolException("AlarmStatus 仅支持 1(Set) 或 2(Clear)。");
                }
                eventItem["alarmCode"] = RequireString(body, "AlarmID");
                eventItem["alarmMessage"] = GetString(body, "AlarmText");
                attributes["alarmLevel"] = GetValue(body, "AlarmLevel");
                attributes["alarmStatus"] = alarmStatus;
            }
            else if (string.Equals(method, "EQPStatusChangeReport", StringComparison.OrdinalIgnoreCase))
            {
                string rawStatus = RequireString(body, "Status");
                string status = MapStatus(rawStatus);
                eventItem["eventType"] = "STATUS_CHANGED";
                eventItem["status"] = status;
                eventItem["spindleStatus"] = GetString(body, "CurrentAxis");
                attributes["currentAxis"] = GetValue(body, "CurrentAxis");
                attributes["rawStatus"] = rawStatus;
            }
            else
            {
                throw new ProtocolException("不支持的接口方法：" + method);
            }
            return eventItem;
        }

        private static Dictionary<string, object> NewEvent(string equipmentId, DateTime deviceTime, string method, string transactionId, Dictionary<string, object> attributes)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["username"] = equipmentId;
            result["machineName"] = equipmentId;
            result["deviceTime"] = deviceTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            result["eventType"] = method;
            result["recordNo"] = "JINGDE-" + method + "-" + transactionId;
            result["attributes"] = attributes;
            return result;
        }

        private static string MapStatus(string rawStatus)
        {
            if (rawStatus == "1") return "IDLE";
            if (rawStatus == "2") return "PROCESSING";
            if (rawStatus == "3") return "ALARM";
            if (rawStatus == "4") return "STOPPED";
            throw new ProtocolException("Status 仅支持 1(IDLE)、2(RUN)、3(DOWN) 或 4(STOPPED)。");
        }

        private static DateTime TimestampFromTransaction(string transactionId, DateTime fallback)
        {
            DateTime parsed;
            if (transactionId != null && transactionId.Length >= 14 &&
                DateTime.TryParseExact(transactionId.Substring(0, 14), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private static bool TryParseTime(string value, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrEmpty(value)) return false;
            string[] formats = new string[] {
                "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyyMMddHHmmss",
                "yyyy-MM-dd HH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.fff"
            };
            return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out result) ||
                DateTime.TryParse(value.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out result);
        }

        private static Dictionary<string, object> RequireDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            Dictionary<string, object> dictionary;
            if (!TryGetValue(source, key, out value) || (dictionary = value as Dictionary<string, object>) == null)
            {
                throw new ProtocolException("缺少对象字段 " + key + "。");
            }
            return dictionary;
        }

        public static Dictionary<string, object> FindDictionary(Dictionary<string, object> source, string key)
        {
            object value;
            return TryGetValue(source, key, out value) ? value as Dictionary<string, object> : null;
        }

        private static string RequireString(Dictionary<string, object> source, string key)
        {
            string value = GetString(source, key);
            if (string.IsNullOrEmpty(value)) throw new ProtocolException("缺少字段 " + key + "。");
            return value.Trim();
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            object value;
            return TryGetValue(source, key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }

        private static object GetValue(Dictionary<string, object> source, string key)
        {
            object value;
            return TryGetValue(source, key, out value) ? value : null;
        }

        private static bool TryGetValue(Dictionary<string, object> source, string key, out object value)
        {
            if (source.TryGetValue(key, out value)) return true;
            foreach (KeyValuePair<string, object> pair in source)
            {
                if (string.Equals(pair.Key.Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
            value = null;
            return false;
        }
    }

    internal sealed class GatewayForm : Form
    {
        private readonly string configPath;
        private readonly DataGridView flowGrid = new DataGridView();
        private readonly TextBox rawPayload = CreatePayloadBox();
        private readonly TextBox normalizedPayload = CreatePayloadBox();
        private readonly Label rawPayloadLabel = CreatePaneLabel("设备原始请求（EAP JSON）");
        private readonly Label normalizedPayloadLabel = CreatePaneLabel("转换后的 QMS 事件 / 转发批次");
        private readonly ToolStripStatusLabel serviceStatus = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel queueStatus = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel deviceStatus = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel endpointStatus = new ToolStripStatusLabel();
        private GatewayRuntime runtime;
        private System.Windows.Forms.Timer statusTimer;

        public GatewayForm(string configPath)
        {
            this.configPath = configPath;
            Text = "LYR 设备数据监听";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(1080, 700);
            Size = new Size(1320, 820);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            BuildLayout();
            Shown += delegate { StartRuntime(); };
            FormClosing += delegate { StopRuntime(); };
        }

        private void BuildLayout()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 75;
            header.BackColor = Color.FromArgb(27, 99, 168);
            Label title = new Label();
            title.Text = "LYR 设备数据监听";
            title.ForeColor = Color.White;
            title.Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold, GraphicsUnit.Point);
            title.AutoSize = true;
            title.Location = new Point(22, 13);
            header.Controls.Add(title);
            Label subtitle = new Label();
            subtitle.Text = "精德 EAP / 森泉喷印机 / 科视 LDI  →  本地持久化队列  →  QMS 遥测接口";
            subtitle.ForeColor = Color.FromArgb(220, 238, 255);
            subtitle.AutoSize = true;
            subtitle.Location = new Point(24, 45);
            header.Controls.Add(subtitle);

            FlowLayoutPanel tools = new FlowLayoutPanel();
            tools.Dock = DockStyle.Top;
            tools.Height = 51;
            tools.Padding = new Padding(12, 9, 12, 6);
            tools.BackColor = Color.FromArgb(242, 247, 252);
            tools.WrapContents = false;
            tools.Controls.Add(CreateButton("服务配置", delegate { OpenSettings(); }));
            tools.Controls.Add(CreateButton("立即补传", delegate { FlushNow(); }));
            tools.Controls.Add(CreateButton("清空显示", delegate { ClearDisplay(); }));
            tools.Controls.Add(CreateButton("停止服务", delegate { StopRuntime(); }));
            tools.Controls.Add(CreateButton("启动服务", delegate { StartRuntime(); }));

            StatusStrip status = new StatusStrip();
            serviceStatus.Text = "服务未启动";
            queueStatus.Text = "待转发：0";
            endpointStatus.Text = "";
            status.Items.Add(serviceStatus);
            status.Items.Add(new ToolStripStatusLabel("    "));
            status.Items.Add(queueStatus);
            status.Items.Add(new ToolStripStatusLabel("    "));
            status.Items.Add(deviceStatus);
            status.Items.Add(new ToolStripStatusLabel("    "));
            status.Items.Add(endpointStatus);

            flowGrid.Dock = DockStyle.Fill;
            flowGrid.AllowUserToAddRows = false;
            flowGrid.AllowUserToDeleteRows = false;
            flowGrid.AllowUserToResizeRows = false;
            flowGrid.AutoGenerateColumns = false;
            flowGrid.BackgroundColor = Color.White;
            flowGrid.BorderStyle = BorderStyle.FixedSingle;
            flowGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(229, 241, 251);
            flowGrid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            flowGrid.EnableHeadersVisualStyles = false;
            flowGrid.ReadOnly = true;
            flowGrid.RowHeadersVisible = false;
            flowGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            flowGrid.MultiSelect = false;
            AddColumn("时间", 155);
            AddColumn("阶段", 105);
            AddColumn("来源", 145);
            AddColumn("接口", 180);
            AddColumn("设备", 110);
            AddColumn("处理结果", 520);
            flowGrid.SelectionChanged += delegate { ShowSelectedPayload(); };

            SplitContainer payloadSplit = new SplitContainer();
            payloadSplit.Dock = DockStyle.Fill;
            payloadSplit.SplitterDistance = 620;
            Panel rawPanel = new Panel();
            rawPanel.Dock = DockStyle.Fill;
            rawPayloadLabel.Dock = DockStyle.Top;
            rawPanel.Controls.Add(rawPayload);
            rawPanel.Controls.Add(rawPayloadLabel);
            Panel normalizedPanel = new Panel();
            normalizedPanel.Dock = DockStyle.Fill;
            normalizedPayloadLabel.Dock = DockStyle.Top;
            normalizedPanel.Controls.Add(normalizedPayload);
            normalizedPanel.Controls.Add(normalizedPayloadLabel);
            payloadSplit.Panel1.Controls.Add(rawPanel);
            payloadSplit.Panel2.Controls.Add(normalizedPanel);

            SplitContainer mainSplit = new SplitContainer();
            mainSplit.Dock = DockStyle.Fill;
            mainSplit.Orientation = Orientation.Horizontal;
            mainSplit.SplitterDistance = 325;
            mainSplit.Panel1.Controls.Add(flowGrid);
            mainSplit.Panel2.Controls.Add(payloadSplit);

            // Docked controls are laid out in reverse child order. Add the fill area first,
            // then the fixed edges, so the blue header and toolbar reserve their own space.
            Controls.Add(mainSplit);
            Controls.Add(status);
            Controls.Add(tools);
            Controls.Add(header);

            statusTimer = new System.Windows.Forms.Timer();
            statusTimer.Interval = 1000;
            statusTimer.Tick += delegate { RefreshStatus(); };
            statusTimer.Start();
        }

        private static TextBox CreatePayloadBox()
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Fill;
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = ScrollBars.Both;
            box.WordWrap = false;
            box.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
            box.BackColor = Color.White;
            return box;
        }

        private static Label CreatePaneLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Height = 30;
            label.Padding = new Padding(10, 6, 0, 0);
            label.BackColor = Color.FromArgb(242, 247, 252);
            label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            return label;
        }

        private static Button CreateButton(string text, EventHandler click)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.Height = 29;
            button.Margin = new Padding(0, 0, 8, 0);
            button.Padding = new Padding(8, 0, 8, 0);
            button.Click += click;
            return button;
        }

        private void AddColumn(string name, int width)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = name;
            column.Width = width;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            flowGrid.Columns.Add(column);
        }

        private void StartRuntime()
        {
            if (runtime != null && runtime.IsRunning) return;
            try
            {
                runtime = new GatewayRuntime(configPath);
                runtime.FlowReceived += HandleFlow;
                runtime.Start();
                AddFlow(new GatewayFlowEvent {
                    Stage = "管理界面就绪",
                    Summary = "服务已在后台启动；可在本窗口查看原始报文、转换事件和 QMS 转发状态。"
                });
                RefreshStatus();
            }
            catch (Exception exception)
            {
                AddFlow(new GatewayFlowEvent { Stage = "启动失败", Summary = exception.Message });
                MessageBox.Show("无法启动 LYR 设备数据监听：\r\n" + exception.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopRuntime()
        {
            if (runtime == null) return;
            runtime.FlowReceived -= HandleFlow;
            runtime.Stop();
            RefreshStatus();
        }

        private void FlushNow()
        {
            if (runtime == null || !runtime.IsRunning)
            {
                MessageBox.Show("请先启动服务。", "无法补传", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            runtime.FlushNow();
            RefreshStatus();
        }

        private void OpenSettings()
        {
            Settings current;
            try { current = runtime != null && runtime.CurrentSettings != null ? runtime.CurrentSettings.Clone() : Settings.Load(configPath); }
            catch (Exception exception)
            {
                MessageBox.Show("无法读取当前配置：\r\n" + exception.Message, "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using (GatewaySettingsForm dialog = new GatewaySettingsForm(current))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    dialog.CandidateSettings.Save(configPath);
                    StopRuntime();
                    StartRuntime();
                    AddFlow(new GatewayFlowEvent { Stage = "配置已应用", Summary = "已保存到 JingdeEapForwarder.ini，并重新启动三类设备监听。" });
                }
                catch (Exception exception)
                {
                    MessageBox.Show("配置保存或应用失败：\r\n" + exception.Message, "配置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ClearDisplay()
        {
            flowGrid.Rows.Clear();
            rawPayload.Clear();
            normalizedPayload.Clear();
            SetPayloadLabels(null);
        }

        private void HandleFlow(GatewayFlowEvent flow)
        {
            if (IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate { AddFlow(flow); });
            }
            catch (InvalidOperationException) { }
        }

        private void AddFlow(GatewayFlowEvent flow)
        {
            if (flow == null || IsDisposed) return;
            int row = flowGrid.Rows.Add(
                flow.OccurredAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                flow.Stage,
                flow.Source,
                flow.Method,
                flow.Equipment,
                flow.Summary);
            flowGrid.Rows[row].Tag = flow;
            int maximum = 240;
            if (runtime != null && runtime.CurrentSettings != null)
            {
                maximum = Math.Max(60, runtime.CurrentSettings.DisplayColumns * runtime.CurrentSettings.DisplayRows * 60);
            }
            while (flowGrid.Rows.Count > maximum) flowGrid.Rows.RemoveAt(0);
            flowGrid.FirstDisplayedScrollingRowIndex = flowGrid.Rows.Count - 1;
            RefreshStatus();
        }

        private void ShowSelectedPayload()
        {
            if (flowGrid.SelectedRows.Count != 1) return;
            GatewayFlowEvent flow = flowGrid.SelectedRows[0].Tag as GatewayFlowEvent;
            if (flow == null) return;
            rawPayload.Text = string.IsNullOrEmpty(flow.RawPayload) ? "（此状态没有设备原始报文）" : flow.RawPayload;
            normalizedPayload.Text = string.IsNullOrEmpty(flow.NormalizedPayload) ? "（此状态没有转换后的事件数据）" : flow.NormalizedPayload;
            SetPayloadLabels(flow.Equipment);
        }

        private void SetPayloadLabels(string equipment)
        {
            string suffix = string.IsNullOrEmpty(equipment) ? string.Empty : " — 当前设备：" + equipment;
            rawPayloadLabel.Text = "设备原始请求（EAP JSON）" + suffix;
            normalizedPayloadLabel.Text = "转换后的 QMS 事件 / 转发批次" + suffix;
        }

        private void RefreshStatus()
        {
            bool running = runtime != null && runtime.IsRunning;
            serviceStatus.Text = running ? "● 服务运行中" : "● 服务已停止";
            serviceStatus.ForeColor = running ? Color.ForestGreen : Color.Firebrick;
            queueStatus.Text = "待转发：" + (runtime == null ? 0 : runtime.PendingCount).ToString(CultureInfo.InvariantCulture) + " 条";
            if (!running || runtime.LastDeviceReceivedAt == DateTime.MinValue)
            {
                deviceStatus.Text = running ? "设备上报：等待首条数据" : "设备上报：服务未运行";
                deviceStatus.ForeColor = Color.DimGray;
            }
            else if (runtime.IsDeviceIdle)
            {
                deviceStatus.Text = "设备上报：超过 " + runtime.CurrentSettings.IdleDisconnectSeconds + " 秒未收到数据";
                deviceStatus.ForeColor = Color.DarkOrange;
            }
            else
            {
                deviceStatus.Text = "设备上报：最近 " + runtime.LastDeviceReceivedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                deviceStatus.ForeColor = Color.ForestGreen;
            }
            endpointStatus.Text = runtime == null || runtime.CurrentSettings == null ? "" : "监听：" + runtime.CurrentSettings.ListenerSummary() + "    QMS：" + runtime.CurrentSettings.QmsBaseUrl;
        }
    }

    internal sealed class GatewaySettingsForm : Form
    {
        private readonly CheckBox jingdeEnabled = new CheckBox();
        private readonly TextBox jingdeListenPrefix = CreateTextBox();
        private readonly TextBox jingdeAllowedIps = CreateTextBox();
        private readonly CheckBox hosonEnabled = new CheckBox();
        private readonly TextBox hosonListenPrefix = CreateTextBox();
        private readonly TextBox hosonAllowedIps = CreateTextBox();
        private readonly TextBox hosonScanResultIsNg = CreateTextBox();
        private readonly CheckBox kstEnabled = new CheckBox();
        private readonly TextBox kstListenPrefix = CreateTextBox();
        private readonly TextBox kstAllowedIps = CreateTextBox();
        private readonly TextBox qmsBaseUrl = CreateTextBox();
        private readonly TextBox gatewayToken = CreateTextBox();
        private readonly TextBox telemetryPath = CreateTextBox();
        private readonly TextBox batchSize = CreateTextBox();
        private readonly TextBox retryInterval = CreateTextBox();
        private readonly TextBox timeout = CreateTextBox();
        private readonly TextBox maxBytes = CreateTextBox();
        private readonly TextBox displayColumns = CreateTextBox();
        private readonly TextBox displayRows = CreateTextBox();
        private readonly TextBox retentionDays = CreateTextBox();
        private readonly TextBox idleSeconds = CreateTextBox();
        private readonly CheckBox useTls12 = new CheckBox();
        public Settings CandidateSettings { get; private set; }

        public GatewaySettingsForm(Settings settings)
        {
            CandidateSettings = settings;
            Text = "LYR 设备数据监听配置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(715, 700);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            gatewayToken.UseSystemPasswordChar = true;
            Fill(settings);
            BuildLayout();
        }

        private void Fill(Settings settings)
        {
            jingdeEnabled.Text = "启用精德 EAP 端口监听";
            jingdeEnabled.Checked = settings.JingdeEnabled;
            jingdeListenPrefix.Text = settings.JingdeListenPrefix;
            jingdeAllowedIps.Text = Join(settings.JingdeAllowedDeviceIps);
            hosonEnabled.Text = "启用森泉喷印机端口监听";
            hosonEnabled.Checked = settings.HosonEnabled;
            hosonListenPrefix.Text = settings.HosonListenPrefix;
            hosonAllowedIps.Text = Join(settings.HosonAllowedDeviceIps);
            hosonScanResultIsNg.Text = settings.HosonScanResultIsNg.ToString(CultureInfo.InvariantCulture);
            kstEnabled.Text = "启用科视 LDI 端口监听";
            kstEnabled.Checked = settings.KstEnabled;
            kstListenPrefix.Text = settings.KstListenPrefix;
            kstAllowedIps.Text = Join(settings.KstAllowedDeviceIps);
            qmsBaseUrl.Text = settings.QmsBaseUrl;
            gatewayToken.Text = settings.GatewayToken;
            telemetryPath.Text = settings.TelemetryPath;
            batchSize.Text = settings.BatchSize.ToString(CultureInfo.InvariantCulture);
            retryInterval.Text = settings.RetryIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            timeout.Text = settings.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            maxBytes.Text = settings.MaxRequestBytes.ToString(CultureInfo.InvariantCulture);
            displayColumns.Text = settings.DisplayColumns.ToString(CultureInfo.InvariantCulture);
            displayRows.Text = settings.DisplayRows.ToString(CultureInfo.InvariantCulture);
            retentionDays.Text = settings.RecordRetentionDays.ToString(CultureInfo.InvariantCulture);
            idleSeconds.Text = settings.IdleDisconnectSeconds.ToString(CultureInfo.InvariantCulture);
            useTls12.Text = "QMS 使用 TLS 1.2";
            useTls12.Checked = settings.UseTls12;
        }

        private void BuildLayout()
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Top;
            table.Padding = new Padding(18, 16, 18, 8);
            table.AutoSize = true;
            table.ColumnCount = 2;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 182));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddSection(table, "精德 EAP（默认 8002）");
            AddRow(table, "监听开关", jingdeEnabled, "未勾选时不会绑定 8002 端口。 ");
            AddRow(table, "监听地址", jingdeListenPrefix, "例如 http://+:8002/；保留现有精德设备配置。 ");
            AddRow(table, "设备 IP 白名单", jingdeAllowedIps, "逗号分隔；留空代表不限制来源。 ");
            AddSection(table, "森泉 PCB 文字喷印机（默认 6680）");
            AddRow(table, "监听开关", hosonEnabled, "未勾选时不会绑定 6680 端口。 ");
            AddRow(table, "监听地址", hosonListenPrefix, "设备请求以 JSON method 区分：login、device_status、scan_result 等。 ");
            AddRow(table, "设备 IP 白名单", hosonAllowedIps, "建议填写三台喷印机的固定 IP。 ");
            AddRow(table, "扫码判定 is_ng", hosonScanResultIsNg, "-1=拒绝未配置判定；0=需要打印；1=不需要打印。 ");
            AddSection(table, "科视 LDI（默认 9000）");
            AddRow(table, "监听开关", kstEnabled, "未勾选时不会绑定 9000 端口。 ");
            AddRow(table, "监听地址", kstListenPrefix, "接收 /Status、/OEE、/Alarm、/ExposureResult、/HeartBeat、/Mode。 ");
            AddRow(table, "设备 IP 白名单", kstAllowedIps, "逗号分隔；留空代表不限制来源。 ");
            AddSection(table, "QMS 转发配置");
            AddRow(table, "QMS 服务地址", qmsBaseUrl, "例如 http://192.168.1.71:8080。 ");
            AddRow(table, "网关令牌", gatewayToken, "与 QMS 的 qms.integration.gateway.shared-token 一致。 ");
            AddRow(table, "遥测接口路径", telemetryPath, "默认 /api/v1/equipment/telemetry。 ");
            AddRow(table, "每批转发数量", batchSize, "1 - 200。 ");
            AddRow(table, "失败重试间隔（秒）", retryInterval, "QMS 不可用时本地队列会保留数据。 ");
            AddRow(table, "请求超时（秒）", timeout, "1 - 300。 ");
            AddRow(table, "最大请求大小（字节）", maxBytes, "1024 - 10485760。 ");
            AddRow(table, "", useTls12, "");
            AddSection(table, "监控显示配置");
            AddRow(table, "显示列数", displayColumns, "1 - 6；用于计算界面缓存容量。 ");
            AddRow(table, "显示行数", displayRows, "1 - 12；用于计算界面缓存容量。 ");
            AddRow(table, "本地记录保留（天）", retentionDays, "用于现场运维记录的保留策略。 ");
            AddRow(table, "空闲断线阈值（秒）", idleSeconds, "设备长时间无上报时的提示阈值。 ");

            Panel buttons = new Panel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 56;
            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Size = new Size(88, 30);
            cancel.Location = new Point(514, 12);
            Button save = new Button();
            save.Text = "保存并应用";
            save.Size = new Size(105, 30);
            save.Location = new Point(610, 12);
            save.Click += delegate { Save(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.Controls.Add(table);
            Controls.Add(buttons);
            Controls.Add(scroll);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static TextBox CreateTextBox()
        {
            TextBox box = new TextBox();
            box.Dock = DockStyle.Top;
            return box;
        }

        private static void AddSection(TableLayoutPanel table, string title)
        {
            Label label = new Label();
            label.Text = title;
            label.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point);
            label.ForeColor = Color.FromArgb(27, 99, 168);
            label.Padding = new Padding(0, 10, 0, 2);
            label.AutoSize = true;
            table.RowCount++;
            table.Controls.Add(label, 0, table.RowCount - 1);
            table.SetColumnSpan(label, 2);
        }

        private static void AddRow(TableLayoutPanel table, string title, Control control, string hint)
        {
            Label label = new Label();
            label.Text = title;
            label.TextAlign = ContentAlignment.TopRight;
            label.Padding = new Padding(0, 5, 10, 0);
            label.AutoSize = true;
            Panel field = new Panel();
            field.Height = 42;
            field.Dock = DockStyle.Top;
            control.Dock = DockStyle.Top;
            field.Controls.Add(control);
            if (!string.IsNullOrEmpty(hint))
            {
                Label help = new Label();
                help.Text = hint;
                help.ForeColor = Color.DimGray;
                help.AutoSize = true;
                help.Dock = DockStyle.Bottom;
                field.Controls.Add(help);
            }
            table.RowCount++;
            table.Controls.Add(label, 0, table.RowCount - 1);
            table.Controls.Add(field, 1, table.RowCount - 1);
        }

        private void Save()
        {
            try
            {
                CandidateSettings.JingdeEnabled = jingdeEnabled.Checked;
                CandidateSettings.JingdeListenPrefix = jingdeListenPrefix.Text.Trim();
                CandidateSettings.JingdeAllowedDeviceIps = Settings.ParseAllowedDeviceIps(jingdeAllowedIps.Text);
                CandidateSettings.HosonEnabled = hosonEnabled.Checked;
                CandidateSettings.HosonListenPrefix = hosonListenPrefix.Text.Trim();
                CandidateSettings.HosonAllowedDeviceIps = Settings.ParseAllowedDeviceIps(hosonAllowedIps.Text);
                CandidateSettings.HosonScanResultIsNg = ParseNumber(hosonScanResultIsNg, "扫码判定 is_ng", -1, 1);
                CandidateSettings.KstEnabled = kstEnabled.Checked;
                CandidateSettings.KstListenPrefix = kstListenPrefix.Text.Trim();
                CandidateSettings.KstAllowedDeviceIps = Settings.ParseAllowedDeviceIps(kstAllowedIps.Text);
                CandidateSettings.QmsBaseUrl = qmsBaseUrl.Text.Trim().TrimEnd('/');
                CandidateSettings.GatewayToken = gatewayToken.Text.Trim();
                CandidateSettings.TelemetryPath = telemetryPath.Text.Trim();
                CandidateSettings.BatchSize = ParseNumber(batchSize, "每批转发数量", 1, 200);
                CandidateSettings.RetryIntervalSeconds = ParseNumber(retryInterval, "失败重试间隔", 1, 3600);
                CandidateSettings.RequestTimeoutSeconds = ParseNumber(timeout, "请求超时", 1, 300);
                CandidateSettings.MaxRequestBytes = ParseNumber(maxBytes, "最大请求大小", 1024, 10485760);
                CandidateSettings.DisplayColumns = ParseNumber(displayColumns, "显示列数", 1, 6);
                CandidateSettings.DisplayRows = ParseNumber(displayRows, "显示行数", 1, 12);
                CandidateSettings.RecordRetentionDays = ParseNumber(retentionDays, "本地记录保留天数", 1, 3650);
                CandidateSettings.IdleDisconnectSeconds = ParseNumber(idleSeconds, "空闲断线阈值", 1, 3600);
                CandidateSettings.UseTls12 = useTls12.Checked;
                CandidateSettings.Validate();
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception exception)
            {
                MessageBox.Show("请检查配置：\r\n" + exception.Message, "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static int ParseNumber(TextBox box, string name, int minimum, int maximum)
        {
            int value;
            if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < minimum || value > maximum)
            {
                throw new ArgumentException(name + " 必须是 " + minimum + " 到 " + maximum + " 之间的整数。");
            }
            return value;
        }

        private static string Join(HashSet<string> values)
        {
            List<string> list = new List<string>(values);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(",", list.ToArray());
        }
    }

    internal sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    internal static class SelfTest
    {
        public static int Run()
        {
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                Dictionary<string, object> status = serializer.DeserializeObject("{\"Header\":{\"MessageName\":\"EQPStatusChangeReport\",\"TransactionID\":\"20260928101213123456\",\"UserID\":\"01\"},\"Body\":{\"EquipmentID\":\"JD-01\",\"CurrentAxis\":\"4\",\"Status\":\"2\"},\"Result\":{\"Code\":1}}") as Dictionary<string, object>;
                Dictionary<string, object> translated = EapTranslator.Translate("EQPStatusChangeReport", status, "192.168.10.11", new DateTime(2026, 9, 28, 10, 12, 14));
                if (GetText(translated, "machineName") != "JD-01" || GetText(translated, "status") != "PROCESSING" ||
                    GetText(translated, "recordNo") != "JINGDE-EQPStatusChangeReport-20260928101213123456")
                {
                    throw new Exception("Status mapping assertion failed.");
                }
                // The supplied interface sample contains trailing spaces in these three alarm keys; accept both forms.
                Dictionary<string, object> alarm = serializer.DeserializeObject("{\"Header\":{\"MessageName\":\"EQPAlarmReport\",\"TransactionID\":\"20260928101215123456\",\"UserID\":\"01\"},\"Body\":{\"EquipmentID\":\"JD-01\",\"AlarmID\":\"ALRM_00001\",\"AlarmLevel \":\"2\",\"AlarmText \":\"设备紧急停机\",\"AlarmStatus \":\"1\"},\"Result\":{\"Code\":1}}") as Dictionary<string, object>;
                translated = EapTranslator.Translate("EQPAlarmReport", alarm, "192.168.10.11", DateTime.Now);
                if (GetText(translated, "eventType") != "ALARM_RAISED" || GetText(translated, "status") != "ALARM" || GetText(translated, "alarmCode") != "ALRM_00001")
                {
                    throw new Exception("Alarm mapping assertion failed.");
                }

                Dictionary<string, object> hosonLogin = serializer.DeserializeObject("{\"id\":\"h-01\",\"timestamp\":1780380948838,\"version\":\"1.0.0.1\",\"method\":\"login\",\"data\":{\"device_code\":\"HOSON-01\",\"user_name\":\"operator\",\"password\":\"x\",\"host_ip\":\"192.168.1.31\"}}") as Dictionary<string, object>;
                HosonTranslator hoson = new HosonTranslator(0);
                ProtocolDispatchResult hosonLoginResult = hoson.Translate(hosonLogin, "192.168.1.31", DateTime.Now);
                if (GetText(hosonLoginResult.EventItem, "eventType") != "DEVICE_LOGIN" || GetText(hosonLoginResult.EventItem, "machineName") != "HOSON-01")
                {
                    throw new Exception("Hoson login mapping assertion failed.");
                }
                Dictionary<string, object> hosonData = hosonLoginResult.ResponsePayload["data"] as Dictionary<string, object>;
                string hosonToken = GetText(hosonData, "token");
                Dictionary<string, object> hosonStatus = serializer.DeserializeObject("{\"id\":\"h-02\",\"timestamp\":1780380948838,\"version\":\"1.0.0.1\",\"method\":\"device_status\",\"token\":\"" + hosonToken + "\",\"data\":{\"status\":\"2\",\"time\":\"2026-09-28 10:12:14\",\"message\":\"故障\"}}") as Dictionary<string, object>;
                ProtocolDispatchResult hosonStatusResult = hoson.Translate(hosonStatus, "192.168.1.31", DateTime.Now);
                if (GetText(hosonStatusResult.EventItem, "eventType") != "STATUS_CHANGED" || GetText(hosonStatusResult.EventItem, "status") != "ALARM")
                {
                    throw new Exception("Hoson status mapping assertion failed.");
                }

                Dictionary<string, object> kstStatus = serializer.DeserializeObject("{\"ID\":\"KST-01-STATUS-1\",\"MachineNO\":\"KST-01\",\"Status\":\"WORK\",\"MachineTime\":\"2026-09-28 10:12:14\",\"UserName\":\"OP-01\",\"LotNO\":\"LOT-01\",\"CBARCODE\":\"BAR-01\"}") as Dictionary<string, object>;
                translated = KstTranslator.Translate("Status", kstStatus, "192.168.1.41", DateTime.Now);
                if (GetText(translated, "eventType") != "STATUS_CHANGED" || GetText(translated, "status") != "PROCESSING" || GetText(translated, "machineName") != "KST-01")
                {
                    throw new Exception("KST status mapping assertion failed.");
                }

                Settings listenerSettings = new Settings();
                listenerSettings.JingdeEnabled = true;
                listenerSettings.JingdeListenPrefix = "http://+:8002/";
                listenerSettings.JingdeAllowedDeviceIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                listenerSettings.HosonEnabled = false;
                listenerSettings.KstEnabled = false;
                if (listenerSettings.GetListeners().Count != 1 || listenerSettings.ListenerSummary().IndexOf("森泉 未启用", StringComparison.Ordinal) < 0)
                {
                    throw new Exception("Listener enable switch assertion failed.");
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (GatewayForm preview = new GatewayForm(Path.Combine(Path.GetTempPath(), "JingdeEapForwarder-self-test.ini")))
                {
                    preview.CreateControl();
                }
                Console.WriteLine("SELF-TEST PASSED");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("SELF-TEST FAILED: " + exception.Message);
                return 2;
            }
        }

        private static string GetText(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) && item != null ? Convert.ToString(item, CultureInfo.InvariantCulture) : null;
        }
    }
}
