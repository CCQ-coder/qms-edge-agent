using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class HostStatusRelay
{
    private readonly string sourceRoot;
    private readonly ScanCheckpointStore checkpoints;
    private readonly Func<HostStatusSample, bool> send;
    private readonly Action<string> log;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

    internal HostStatusRelay(string sourceRoot, string checkpointPath, Func<HostStatusSample, bool> send, Action<string> log)
    {
        this.sourceRoot = sourceRoot;
        checkpoints = new ScanCheckpointStore(checkpointPath);
        this.send = send;
        this.log = log;
    }

    internal void ForwardOnce(Func<bool> shouldStop)
    {
        if (!Directory.Exists(sourceRoot)) return;
        foreach (string directory in Directory.GetDirectories(sourceRoot))
        {
            if (shouldStop()) return;
            string path = Path.Combine(directory, "host-status.json");
            if (!File.Exists(path)) continue;
            try
            {
                byte[] bytes = ReadBounded(path);
                string deviceId = Path.GetFileName(directory);
                if (string.Equals(deviceId, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Device ID collides with listener computer.");
                HostStatusSample sample = Parse(bytes, deviceId);
                string fingerprint;
                using (SHA256 sha = SHA256.Create())
                    fingerprint = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                string key = "host-status/" + deviceId;
                if (checkpoints.Contains(key, fingerprint)) continue;
                if (send(sample)) checkpoints.Record(key, fingerprint);
            }
            catch (Exception error)
            {
                if (log != null) log("Host status relay skipped " + Path.GetFileName(directory) + ": " + error.GetType().Name);
            }
        }
    }

    private static byte[] ReadBounded(string path)
    {
        byte[] buffer = new byte[4097];
        int count = 0;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            while (count < buffer.Length)
            {
                int read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
        }
        if (count == 0 || count > 4096) throw new InvalidDataException("Invalid status size.");
        byte[] result = new byte[count];
        Buffer.BlockCopy(buffer, 0, result, 0, count);
        return result;
    }

    private HostStatusSample Parse(byte[] bytes, string directoryName)
    {
        string json = new UTF8Encoding(false, true).GetString(bytes);
        Dictionary<string, object> fields = serializer.DeserializeObject(json) as Dictionary<string, object>;
        if (fields == null || RequiredLong(fields, "schemaVersion") != 1)
            throw new InvalidDataException("Unsupported status schema.");
        string deviceId = RequiredString(fields, "deviceId");
        if (deviceId.Length > 160 || !string.Equals(deviceId, directoryName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Status identity does not match directory.");
        string timestamp = RequiredString(fields, "sampledAt");
        DateTimeOffset sampledAt;
        if (timestamp.IndexOf('T') < 0 || !(timestamp.EndsWith("Z", StringComparison.Ordinal) ||
            timestamp.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out sampledAt) ||
            sampledAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid UTC sample time.");
        double? cpu = null;
        object cpuValue;
        if (!fields.TryGetValue("cpuPercent", out cpuValue)) throw new InvalidDataException("Missing CPU field.");
        if (cpuValue != null)
        {
            double cpuNumber;
            if (!IsNumber(cpuValue) || !double.TryParse(Convert.ToString(cpuValue, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out cpuNumber) ||
                double.IsNaN(cpuNumber) || double.IsInfinity(cpuNumber) || cpuNumber < 0 || cpuNumber > 100)
                throw new InvalidDataException("Invalid CPU percentage.");
            cpu = cpuNumber;
        }
        long memoryTotal = RequiredLong(fields, "memoryTotalBytes");
        long memoryAvailable = RequiredLong(fields, "memoryAvailableBytes");
        long diskTotal = RequiredLong(fields, "systemDiskTotalBytes");
        long diskFree = RequiredLong(fields, "systemDiskFreeBytes");
        long uptime = RequiredLong(fields, "uptimeSeconds");
        if (memoryTotal <= 0 || memoryAvailable < 0 || memoryAvailable > memoryTotal ||
            diskTotal <= 0 || diskFree < 0 || diskFree > diskTotal || uptime < 0)
            throw new InvalidDataException("Invalid status capacity.");
        return new HostStatusSample(deviceId, sampledAt.UtcDateTime, cpu, (ulong)memoryTotal,
            (ulong)memoryAvailable, diskTotal, diskFree, (ulong)uptime);
    }

    private static string RequiredString(Dictionary<string, object> fields, string key)
    {
        object value;
        string text = fields.TryGetValue(key, out value) ? value as string : null;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Missing status field: " + key);
        return text;
    }

    private static long RequiredLong(Dictionary<string, object> fields, string key)
    {
        object value;
        long number;
        if (!fields.TryGetValue(key, out value) || !IsNumber(value) ||
            !long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number))
            throw new InvalidDataException("Invalid status field: " + key);
        return number;
    }

    private static bool IsNumber(object value)
    {
        return value is int || value is long || value is decimal || value is double;
    }
}
