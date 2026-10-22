using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;

internal sealed class HostStatusSender
{
    private readonly QmsHttpPoster poster;
    private readonly Action<string> log;

    internal HostStatusSender(string baseUrl, string gatewayToken, string bearerToken,
        int timeoutSeconds, Action<string> log)
    {
        poster = new QmsHttpPoster(baseUrl, gatewayToken, bearerToken, timeoutSeconds);
        this.log = log;
    }

    internal bool Send(HostStatusSample sample)
    {
        Dictionary<string, object> body = new Dictionary<string, object>();
        body["hostName"] = sample.HostName;
        body["sampledAt"] = sample.SampledAtUtc.ToString("o", CultureInfo.InvariantCulture);
        body["cpuPercent"] = sample.CpuPercent;
        body["memoryTotalBytes"] = sample.MemoryTotalBytes;
        body["memoryAvailableBytes"] = sample.MemoryAvailableBytes;
        body["systemDiskTotalBytes"] = sample.SystemDiskTotalBytes;
        body["systemDiskFreeBytes"] = sample.SystemDiskFreeBytes;
        body["uptimeSeconds"] = sample.UptimeSeconds;
        int status = 0;
        try
        {
            poster.Post("/api/v1/gateway/host-status", body, out status);
        }
        catch (WebException error)
        {
            Log("Host status delivery failed: " + error.Status);
            return false;
        }
        catch (IOException error)
        {
            Log("Host status delivery failed: " + error.GetType().Name);
            return false;
        }
        if (status >= 200 && status < 300) return true;
        Log("Host status delivery failed: HTTP " + status.ToString(CultureInfo.InvariantCulture));
        return false;
    }

    private void Log(string message)
    {
        if (log != null) log(message);
    }
}
