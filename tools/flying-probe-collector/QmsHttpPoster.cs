using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class QmsHttpPoster
{
    private readonly string baseUrl;
    private readonly string gatewayToken;
    private readonly string bearerToken;
    private readonly int timeoutSeconds;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

    internal QmsHttpPoster(string baseUrl, string gatewayToken, string bearerToken, int timeoutSeconds)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        this.gatewayToken = gatewayToken;
        this.bearerToken = bearerToken;
        this.timeoutSeconds = timeoutSeconds;
    }

    internal string Post(string path, object body, out int status)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(serializer.Serialize(body));
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(baseUrl + path);
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
        using (Timer deadline = new Timer(delegate { request.Abort(); }, null, timeoutSeconds * 1000, Timeout.Infinite))
        {
            using (Stream output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
            HttpWebResponse response;
            try { response = (HttpWebResponse)request.GetResponse(); }
            catch (WebException error)
            {
                response = error.Response as HttpWebResponse;
                if (response == null) throw;
            }
            using (response)
            {
                status = (int)response.StatusCode;
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
        }
    }
}
