using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class HostTarget
{
    public string MachineName { get; set; }
    public string HostName { get; set; }
    public string Method { get; set; }
    public int TcpPort { get; set; }
}

internal sealed class DeviceHosts
{
    public int ProbeIntervalSeconds { get; set; }
    public int FailureThreshold { get; set; }
    public IList<HostTarget> Targets { get; private set; }

    public DeviceHosts()
    {
        ProbeIntervalSeconds = 60;
        FailureThreshold = 3;
        Targets = new List<HostTarget>();
    }

    public static DeviceHosts Load(string path)
    {
        DeviceHosts hosts = new DeviceHosts();
        if (!File.Exists(path)) return hosts;
        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            Dictionary<string, object> root = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Host mapping must be a JSON object.");
            object value;
            if (root.TryGetValue("probeIntervalSeconds", out value)) hosts.ProbeIntervalSeconds = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (root.TryGetValue("failureThreshold", out value)) hosts.FailureThreshold = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (root.TryGetValue("targets", out value))
            {
                IList targets = value as IList;
                if (targets == null) throw new InvalidDataException("Host targets must be an array.");
                foreach (object entry in targets)
                {
                    Dictionary<string, object> row = entry as Dictionary<string, object>;
                    if (row == null) throw new InvalidDataException("Host target must be an object.");
                    hosts.Targets.Add(new HostTarget {
                        MachineName = StringValue(row, "machineName"),
                        HostName = StringValue(row, "hostName"),
                        Method = StringValue(row, "method"),
                        TcpPort = row.ContainsKey("tcpPort") ? Convert.ToInt32(row["tcpPort"], CultureInfo.InvariantCulture) : 0
                    });
                }
            }
            hosts.Validate();
            return hosts;
        }
        catch (Exception error)
        {
            if (error is OutOfMemoryException) throw;
            throw new InvalidDataException("Host mapping cannot be read: " + path, error);
        }
    }

    public void Save(string path)
    {
        Validate();
        List<Dictionary<string, object>> targets = new List<Dictionary<string, object>>();
        foreach (HostTarget target in Targets)
            targets.Add(new Dictionary<string, object> {
                { "machineName", target.MachineName.Trim() },
                { "hostName", (target.HostName ?? "").Trim() },
                { "method", NormalizeMethod(target.Method) },
                { "tcpPort", target.TcpPort }
            });
        Dictionary<string, object> body = new Dictionary<string, object> {
            { "probeIntervalSeconds", ProbeIntervalSeconds },
            { "failureThreshold", FailureThreshold },
            { "targets", targets }
        };
        AtomicJson.Write(path, new JavaScriptSerializer().Serialize(body));
    }

    public void Validate()
    {
        if (ProbeIntervalSeconds < 1 || ProbeIntervalSeconds > 240)
            throw new ArgumentException("ProbeIntervalSeconds must be between 1 and 240.");
        if (FailureThreshold < 1 || FailureThreshold > 20)
            throw new ArgumentException("FailureThreshold must be between 1 and 20.");
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HostTarget target in Targets)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.MachineName) ||
                target.MachineName.IndexOfAny(new char[] { '/', '\\', '\r', '\n' }) >= 0)
                throw new ArgumentException("Each host target needs a device directory name.");
            if (!seen.Add(target.MachineName.Trim())) throw new ArgumentException("Device directory names must be unique.");
            if (!string.IsNullOrWhiteSpace(target.HostName) &&
                (target.HostName.IndexOfAny(new char[] { '/', '\\', '\r', '\n', ' ', '\t' }) >= 0 || target.HostName.Length > 253))
                throw new ArgumentException("HostName must be a host name or IP address.");
            string method = NormalizeMethod(target.Method);
            if (method != "ICMP" && method != "TCP") throw new ArgumentException("Method must be ICMP or TCP.");
            if (method == "TCP" && (target.TcpPort < 1 || target.TcpPort > 65535))
                throw new ArgumentException("TCP target needs a port between 1 and 65535.");
        }
    }

    private static string StringValue(Dictionary<string, object> values, string key)
    {
        object value;
        return values.TryGetValue(key, out value) && value != null ? value as string : null;
    }

    private static string NormalizeMethod(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "ICMP" : value.Trim().ToUpperInvariant();
    }
}
