using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

internal sealed class Settings
{
    public string SourceRoot;
    public int ScanIntervalSeconds;
    public string QmsBaseUrl;
    public string GatewayToken;
    public string BearerToken;
    public int RequestTimeoutSeconds;
    public string QueueFile;
    public string CheckpointFile;
    public string LogFile;

    public static Settings Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Collector configuration was not found.", path);
        return SettingsDocument.Load(path).Validate();
    }

    internal static Settings FromValues(string path, Dictionary<string, string> values)
    {
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path));
        Settings result = new Settings();
        result.SourceRoot = ResolvePath(baseDirectory, Read(values, "SourceRoot", @"\\Scbgx\数字化\飞针机"));
        result.ScanIntervalSeconds = ReadInt(values, "ScanIntervalSeconds", 30, 1, 3600);
        result.QmsBaseUrl = Read(values, "QmsBaseUrl", "").TrimEnd('/');
        result.GatewayToken = Read(values, "GatewayToken", "");
        result.BearerToken = Read(values, "BearerToken", "");
        result.RequestTimeoutSeconds = ReadInt(values, "RequestTimeoutSeconds", 20, 1, 300);
        result.QueueFile = ResolvePath(baseDirectory, Read(values, "QueueFile", @".\data\pending-events.json"));
        result.CheckpointFile = ResolvePath(baseDirectory, Read(values, "CheckpointFile", @".\data\scan-checkpoints.json"));
        result.LogFile = ResolvePath(baseDirectory, Read(values, "LogFile", @".\logs\flying-probe-collector.log"));

        Uri qms;
        if (!Uri.TryCreate(result.QmsBaseUrl, UriKind.Absolute, out qms) ||
            (qms.Scheme != Uri.UriSchemeHttp && qms.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("QmsBaseUrl must be an absolute HTTP or HTTPS URL.");
        if (string.IsNullOrWhiteSpace(result.GatewayToken) ||
            result.GatewayToken == "REPLACE_WITH_QMS_GATEWAY_TOKEN")
            throw new ArgumentException("GatewayToken must contain the QMS gateway token.");
        if (result.QueueFile.Equals(result.CheckpointFile, StringComparison.OrdinalIgnoreCase) ||
            result.QueueFile.Equals(result.LogFile, StringComparison.OrdinalIgnoreCase) ||
            result.CheckpointFile.Equals(result.LogFile, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("QueueFile, CheckpointFile and LogFile must be different paths.");
        return result;
    }

    private static string Read(Dictionary<string, string> values, string key, string fallback)
    {
        string value;
        return values.TryGetValue(key, out value) ? value : fallback;
    }

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback, int minimum, int maximum)
    {
        string raw;
        if (!values.TryGetValue(key, out raw)) return fallback;
        int parsed;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException(key + " must be between " + minimum + " and " + maximum + ".");
        return parsed;
    }

    private static string ResolvePath(string baseDirectory, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A configured path must not be empty.");
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(baseDirectory, value));
    }
}

internal static class Program
{
    private static volatile bool stopping;

    private static int Main(string[] args)
    {
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FlyingProbeCollector.ini");
        if (args.Length != 0)
        {
            if (args.Length != 2 || !string.Equals(args[0], "--config", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Usage: FlyingProbeCollector.exe [--config <path>]");
                return 2;
            }
            configPath = Path.GetFullPath(args[1]);
        }
        try
        {
            Settings settings = Settings.Load(configPath);
            if (settings.QmsBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            Action<string> log = delegate(string message)
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + message;
                string directory = Path.GetDirectoryName(settings.LogFile);
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(settings.LogFile, line + Environment.NewLine, Encoding.UTF8);
                Console.WriteLine(line);
            };
            using (new CollectorOwnership(settings.QueueFile))
            {
                PendingEventStore pending = new PendingEventStore(settings.QueueFile);
                ScanCheckpointStore checkpoints = new ScanCheckpointStore(settings.CheckpointFile);
                ProbeScanner scanner = new ProbeScanner(settings.SourceRoot, pending, checkpoints, log);
                QmsSender sender = new QmsSender(settings.QmsBaseUrl, settings.GatewayToken, settings.BearerToken,
                    settings.RequestTimeoutSeconds, pending, log);
                Console.CancelKeyPress += delegate(object source, ConsoleCancelEventArgs eventArgs)
                {
                    eventArgs.Cancel = true;
                    stopping = true;
                };
                log("Flying probe collector started. Source=" + settings.SourceRoot);
                while (!stopping)
                {
                    RunCycle(scanner.ScanOnce, sender.SendPending, log);
                    int ticks = settings.ScanIntervalSeconds * 4;
                    for (int index = 0; index < ticks && !stopping; index++) Thread.Sleep(250);
                }
                log("Flying probe collector stopped.");
                return 0;
            }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Flying probe collector failed: " + error);
            return 1;
        }
    }

    internal static void RunCycle(Action scan, Action send, Action<string> log)
    {
        try { scan(); }
        catch (Exception error) { log("Scan failed: " + error.Message); }
        try { send(); }
        catch (Exception error) { log("QMS delivery cycle failed: " + error.Message); }
    }
}
