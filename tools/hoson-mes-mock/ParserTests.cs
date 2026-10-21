using System;
using System.IO;
using System.Reflection;
using System.Text;

internal static class ParserTests
{
    private static readonly MethodInfo ReadRequest = typeof(HosonMesServer).GetMethod("ReadRequest", BindingFlags.NonPublic | BindingFlags.Static);

    private static int Main()
    {
        try
        {
            byte[] ignored;
            object probe = Parse("GET /api/mes/invoke HTTP/1.1\r\nHost: local\r\n\r\n", out ignored);
            Assert(Field(probe, "Method") == "GET", "bodyless request method is parsed");
            Assert(Field(probe, "Path") == "/api/mes/invoke", "bodyless request path is parsed");
            Assert(Field(probe, "Body") == "", "bodyless request has an empty body");

            AssertStatus("POST /api/mes/invoke HTTP/1.1\r\nContent-Length: 2\r\nContent-Length: 2\r\n\r\n{}", 400, "duplicate content lengths are rejected");
            AssertStatus("POST /api/mes/invoke HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n", 501, "unsupported transfer encoding is explicit");

            byte[] written;
            object request = Parse("POST /api/mes/invoke HTTP/1.1\r\nContent-Length: 2\r\nExpect: 100-continue\r\n\r\n{}", out written);
            Assert(Field(request, "Body") == "{}", "continue request body is parsed");
            Assert(Encoding.ASCII.GetString(written).Contains("HTTP/1.1 100 Continue"), "continue response is sent before reading a delayed body");

            Console.WriteLine("HTTP request parser checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static object Parse(string text, out byte[] bytes)
    {
        using (MemoryStream input = new MemoryStream())
        {
            byte[] source = Encoding.ASCII.GetBytes(text);
            input.Write(source, 0, source.Length);
            input.Position = 0;
            object request = ReadRequest.Invoke(null, new object[] { input });
            bytes = input.ToArray();
            return request;
        }
    }

    private static void AssertStatus(string text, int expected, string message)
    {
        try
        {
            byte[] ignored;
            Parse(text, out ignored);
            throw new Exception("Failed: " + message + " (no error)");
        }
        catch (TargetInvocationException error)
        {
            FieldInfo status = error.InnerException.GetType().GetField("Status", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            int actual = (int)status.GetValue(error.InnerException);
            Assert(actual == expected, message + " (status " + actual + ")");
        }
    }

    private static string Field(object request, string name)
    {
        FieldInfo field = request.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return field == null ? null : field.GetValue(request) as string;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Failed: " + message);
    }
}
