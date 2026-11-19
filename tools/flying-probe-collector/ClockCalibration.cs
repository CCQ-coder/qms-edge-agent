using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class ServerClockProbe
{
    private readonly string url;
    private readonly string gatewayToken;
    private readonly int timeoutMilliseconds;

    public ServerClockProbe(string baseUrl, string gatewayToken, int requestTimeoutSeconds)
    {
        url = baseUrl.TrimEnd('/') + "/api/v1/gateway/server-time";
        this.gatewayToken = gatewayToken;
        timeoutMilliseconds = Math.Min(requestTimeoutSeconds, 2) * 1000;
    }

    public bool TrySample(out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Timeout = timeoutMilliseconds;
            request.ReadWriteTimeout = timeoutMilliseconds;
            request.KeepAlive = false;
            request.AllowAutoRedirect = false;
            request.ServicePoint.Expect100Continue = false;
            request.Headers["X-Gateway-Token"] = gatewayToken;
            DateTime started = DateTime.UtcNow;
            using (Timer deadline = new Timer(delegate { request.Abort(); }, null, timeoutMilliseconds, Timeout.Infinite))
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK) return false;
                string body;
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    char[] buffer = new char[1025];
                    int count = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (count > 1024) return false;
                    body = new string(buffer, 0, count);
                }
                DateTime ended = DateTime.UtcNow;
                return TryCalculateOffset(body, started, ended, out offset);
            }
        }
        catch (WebException) { return false; }
        catch (IOException) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool TryCalculateOffset(string body, DateTime startedUtc, DateTime endedUtc, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (startedUtc.Kind != DateTimeKind.Utc || endedUtc.Kind != DateTimeKind.Utc ||
            endedUtc < startedUtc || endedUtc - startedUtc > TimeSpan.FromSeconds(1)) return false;
        Dictionary<string, object> json;
        try { json = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>; }
        catch (ArgumentException) { return false; }
        if (json == null) return false;
        object value;
        string raw = json.TryGetValue("serverTime", out value) ? value as string : null;
        if (string.IsNullOrEmpty(raw) || !raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase)) return false;
        DateTimeOffset serverTime;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out serverTime))
            return false;
        DateTime midpoint = startedUtc.AddTicks((endedUtc - startedUtc).Ticks / 2);
        offset = serverTime.UtcDateTime - midpoint;
        return true;
    }
}

internal static class HostStatusClock
{
    public static bool TryReadOffset(string deviceDirectory, string deviceId, DateTime collectorNowUtc,
        TimeSpan localToQmsOffset, out int offsetSeconds)
    {
        offsetSeconds = 0;
        try
        {
            string path = Path.Combine(deviceDirectory, "host-status.json");
            FileInfo file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists || file.Length > 4096) return false;
            long length = file.Length;
            DateTime writeUtc = file.LastWriteTimeUtc;
            string text = File.ReadAllText(path, Encoding.UTF8);
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != writeUtc) return false;
            Dictionary<string, object> status = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (status == null) return false;
            object version, identity, sampled;
            if (!status.TryGetValue("schemaVersion", out version) || !(version is int) || (int)version != 1 ||
                !status.TryGetValue("deviceId", out identity) ||
                !string.Equals(identity as string, deviceId, StringComparison.OrdinalIgnoreCase) ||
                !status.TryGetValue("sampledAt", out sampled)) return false;
            string raw = sampled as string;
            if (string.IsNullOrEmpty(raw) || !(raw.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
                raw.EndsWith("+00:00", StringComparison.Ordinal))) return false;
            DateTimeOffset sampledAt;
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out sampledAt))
                return false;
            DateTime qmsNowUtc = collectorNowUtc.Add(localToQmsOffset);
            TimeSpan age = qmsNowUtc - writeUtc;
            if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromMinutes(3)) return false;
            double seconds = (sampledAt.UtcDateTime - writeUtc).TotalSeconds;
            if (Math.Abs(seconds) > 7200) return false;
            offsetSeconds = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
