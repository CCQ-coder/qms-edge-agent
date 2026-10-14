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
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

    public QmsSender(string baseUrl, string gatewayToken, string bearerToken, int timeoutSeconds,
        PendingEventStore pending, Action<string> log)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.gatewayToken = gatewayToken;
        this.bearerToken = bearerToken;
        this.timeoutSeconds = timeoutSeconds;
        this.pending = pending;
        this.log = log;
    }

    public void SendPending()
    {
        foreach (Dictionary<string, object> item in pending.Snapshot())
        {
            string recordNo = (string)item["recordNo"];
            try
            {
                Upload(item);
            }
            catch (WebException error)
            {
                HttpWebResponse response = error.Response as HttpWebResponse;
                string detail = response == null ? error.Status.ToString() :
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                if (response != null) response.Close();
                Log("QMS delivery failed for " + recordNo + ": " + detail);
                continue;
            }
            pending.Remove(recordNo);
            Log("QMS accepted " + recordNo);
        }
    }

    private void Upload(Dictionary<string, object> item)
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
        request.ServicePoint.Expect100Continue = false;
        request.Headers["X-Gateway-Token"] = gatewayToken;
        if (!string.IsNullOrEmpty(bearerToken)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearerToken;
        request.ContentLength = bytes.Length;
        using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        {
            if ((int)response.StatusCode < 200 || (int)response.StatusCode >= 300)
                throw new WebException("QMS returned a non-success response.", null, WebExceptionStatus.ProtocolError, response);
        }
    }

    private void Log(string message)
    {
        if (log != null) log(message);
    }
}
