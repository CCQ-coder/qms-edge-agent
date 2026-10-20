using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class PresenceSendResult
{
    public int HttpStatus { get; set; }
    public bool Accepted { get; set; }
    public string Error { get; set; }
}

internal sealed class PresenceSender
{
    private readonly string baseUrl;
    private readonly string gatewayToken;
    private readonly int timeoutMilliseconds;

    public PresenceSender(string baseUrl, string gatewayToken, int timeoutSeconds)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.gatewayToken = gatewayToken;
        timeoutMilliseconds = Math.Min(timeoutSeconds * 1000, 5000);
    }

    public PresenceSendResult Send(PresenceReport report)
    {
        try
        {
        Dictionary<string, object> body = new Dictionary<string, object> {
            { "machineName", report.MachineName },
            { "observedAt", Format(report.ObservedAt) },
            { "networkState", report.NetworkState },
            { "sourceState", report.SourceState },
            { "lastTxtChangeAt", report.LastTxtChangeAt.HasValue ? Format(report.LastTxtChangeAt.Value) : null }
        };
        byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/v1/equipment/presence/flying-probe");
        request.Method = "POST";
        request.ContentType = "application/json; charset=utf-8";
        request.Accept = "application/json";
        request.Timeout = timeoutMilliseconds;
        request.ReadWriteTimeout = timeoutMilliseconds;
        request.KeepAlive = false;
        request.AllowAutoRedirect = false;
        request.ServicePoint.Expect100Continue = false;
        request.Headers["X-Gateway-Token"] = gatewayToken;
        request.ContentLength = bytes.Length;
            using (Timer deadline = new Timer(delegate { request.Abort(); }, null, timeoutMilliseconds, Timeout.Infinite))
            {
                using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                HttpWebResponse response;
                try { response = (HttpWebResponse)request.GetResponse(); }
                catch (WebException error)
                {
                    response = error.Response as HttpWebResponse;
                    if (response == null) return new PresenceSendResult { Error = error.Status.ToString() };
                }
                using (response)
                {
                    int status = (int)response.StatusCode;
                    string content = ReadResponse(response);
                    if (status < 200 || status >= 300)
                        return new PresenceSendResult { HttpStatus = status, Error = Safe(content.Length == 0 ? "HTTP " + status : content) };
                    try
                    {
                        Dictionary<string, object> envelope = new JavaScriptSerializer().DeserializeObject(content) as Dictionary<string, object>;
                        object data;
                        Dictionary<string, object> payload = envelope != null && envelope.TryGetValue("data", out data)
                            ? data as Dictionary<string, object> : null;
                        object accepted;
                        if (payload != null && payload.TryGetValue("accepted", out accepted) && accepted is bool && (bool)accepted)
                            return new PresenceSendResult { HttpStatus = status, Accepted = true, Error = "" };
                    }
                    catch (ArgumentException) { }
                    return new PresenceSendResult { HttpStatus = status, Error = "QMS response did not confirm acceptance." };
                }
            }
        }
        catch (WebException error) { return new PresenceSendResult { Error = error.Status.ToString() }; }
        catch (IOException error) { return new PresenceSendResult { Error = error.GetType().Name }; }
        catch (Exception error)
        {
            if (error is OutOfMemoryException) throw;
            return new PresenceSendResult { Error = error.GetType().Name };
        }
    }

    private string Safe(string content)
    {
        if (!string.IsNullOrEmpty(gatewayToken)) content = content.Replace(gatewayToken, "[REDACTED]");
        return content.Length <= 512 ? content : content.Substring(0, 512);
    }

    private static string Format(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string ReadResponse(HttpWebResponse response)
    {
        using (Stream input = response.GetResponseStream())
        {
            if (input == null) return "";
            byte[] buffer = new byte[4096];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = input.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count += read;
            }
            return Encoding.UTF8.GetString(buffer, 0, count);
        }
    }
}
