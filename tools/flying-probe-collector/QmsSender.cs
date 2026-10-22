using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;

internal sealed class QmsSender
{
    private readonly QmsHttpPoster poster;
    private readonly PendingEventStore pending;
    private readonly Action<string> log;
    private readonly Action<SyncRecord> onRecord;
    public bool LastSendFailed { get; private set; }

    public QmsSender(string baseUrl, string gatewayToken, string bearerToken, int timeoutSeconds,
        PendingEventStore pending, Action<string> log)
        : this(baseUrl, gatewayToken, bearerToken, timeoutSeconds, pending, log, null)
    {
    }

    public QmsSender(string baseUrl, string gatewayToken, string bearerToken, int timeoutSeconds,
        PendingEventStore pending, Action<string> log, Action<SyncRecord> onRecord)
    {
        poster = new QmsHttpPoster(baseUrl, gatewayToken, bearerToken, timeoutSeconds);
        this.pending = pending;
        this.log = log;
        this.onRecord = onRecord;
    }

    public void SendPending()
    {
        SendPending(delegate { return false; });
    }

    public void SendPending(Func<bool> shouldStop)
    {
        LastSendFailed = false;
        foreach (Dictionary<string, object> item in pending.Snapshot())
        {
            if (shouldStop()) break;
            string recordNo = (string)item["recordNo"];
            int status = 0;
            string detail;
            try
            {
                detail = Upload(item, out status);
            }
            catch (WebException error)
            {
                LastSendFailed = true;
                Record("failed", item, 0, error.Status.ToString());
                Log("QMS delivery failed for " + recordNo + ": " + error.Status);
                continue;
            }
            catch (IOException error)
            {
                LastSendFailed = true;
                Record("failed", item, status, error.GetType().Name);
                Log("QMS response failed for " + recordNo + ": " + error.GetType().Name);
                continue;
            }
            if (status < 200 || status >= 300)
            {
                LastSendFailed = true;
                Record("failed", item, status, detail);
                Log("QMS delivery failed for " + recordNo + ": " + status.ToString(CultureInfo.InvariantCulture));
                continue;
            }
            Record("success", item, status, detail);
            pending.Remove(recordNo);
            Log("QMS accepted " + recordNo);
        }
    }

    private string Upload(Dictionary<string, object> item, out int status)
    {
        Dictionary<string, object> body = new Dictionary<string, object>();
        body["events"] = new object[] { item };
        return poster.Post("/api/v1/equipment/telemetry", body, out status);
    }

    private void Record(string stage, Dictionary<string, object> item, int status, string response)
    {
        if (onRecord != null) onRecord(SyncRecord.FromEvent(stage, item, status, response));
    }

    private void Log(string message)
    {
        if (log != null) log(message);
    }
}
