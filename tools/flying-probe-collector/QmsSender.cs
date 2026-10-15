using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class QmsSender
{
    private readonly string baseUrl;
    private readonly string gatewayToken;
    private readonly string bearerToken;
    private readonly int timeoutSeconds;
    private readonly PendingEventStore pending;
    private readonly Action<string> log;
    private readonly Action<SyncRecord> onRecord;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

    public QmsSender(string baseUrl, string gatewayToken, string bearerToken, int timeoutSeconds,
        PendingEventStore pending, Action<string> log)
        : this(baseUrl, gatewayToken, bearerToken, timeoutSeconds, pending, log, null)
    {
    }

    public QmsSender(string baseUrl, string gatewayToken, string bearerToken, int timeoutSeconds,
        PendingEventStore pending, Action<string> log, Action<SyncRecord> onRecord)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.gatewayToken = gatewayToken;
        this.bearerToken = bearerToken;
        this.timeoutSeconds = timeoutSeconds;
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
        foreach (Dictionary<string, object> item in pending.Snapshot())
        {
            if (shouldStop()) break;
            string recordNo = (string)item["recordNo"];
            try
            {
                int status;
                string response = Upload(item, out status);
                Record("success", item, status, response);
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                int status = response == null ? 0 : (int)response.StatusCode;
                string detail = response == null ? error.Status.ToString() : ReadResponse(response);
                if (response != null) response.Close();
                Record("failed", item, status, detail);
                Log("QMS delivery failed for " + recordNo + ": " + (status == 0 ? detail : status.ToString(CultureInfo.InvariantCulture)));
                continue;
            }
            pending.Remove(recordNo);
            Log("QMS accepted " + recordNo);
        }
    }

    private string Upload(Dictionary<string, object> item, out int status)
    {
        Dictionary<string, object> body = new Dictionary<string, object>();
        body["events"] = new object[] { item };
        byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(body));
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/v1/equipment/telemetry");
        request.Method = "POST";
        request.ContentType = "application/json; charset=utf-8";
        request.Accept = "application/json";
        request.Timeout = timeoutSeconds * 1000;
        request.ReadWriteTimeout = timeoutSeconds * 1000;
        request.KeepAlive = false;
        request.AllowAutoRedirect = false;
        request.ServicePoint.Expect100Continue = false;
        request.Headers["X-Gateway-Token"] = gatewayToken;
        if (!string.IsNullOrEmpty(bearerToken)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearerToken;
        request.ContentLength = bytes.Length;
        using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
        HttpWebResponse response = (HttpWebResponse)request.GetResponse();
        status = (int)response.StatusCode;
        if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
        {
            throw new WebException("QMS returned a non-success response.", null, WebExceptionStatus.ProtocolError, response);
        }
        string detail = ReadResponse(response);
        response.Close();
        return detail;
    }

    private void Record(string stage, Dictionary<string, object> item, int status, string response)
    {
        if (onRecord != null) onRecord(SyncRecord.FromEvent(stage, item, status, response));
    }

    private static string ReadResponse(HttpWebResponse response)
    {
        using (Stream stream = response.GetResponseStream())
        {
            if (stream == null) return "";
            byte[] buffer = new byte[8192];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            return Encoding.UTF8.GetString(buffer, 0, count);
        }
    }

    private void Log(string message)
    {
        if (log != null) log(message);
    }
}
