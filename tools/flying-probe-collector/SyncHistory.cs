using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class SyncRecord
{
    public DateTime Time { get; set; }
    public string Stage { get; set; }
    public string RecordNo { get; set; }
    public string Machine { get; set; }
    public string Material { get; set; }
    public int Tested { get; set; }
    public int Good { get; set; }
    public int Bad { get; set; }
    public string LastTestTime { get; set; }
    public int HttpStatus { get; set; }
    public string Request { get; set; }
    public string Response { get; set; }

    public static SyncRecord FromEvent(string stage, Dictionary<string, object> item, int httpStatus, string response)
    {
        object attributesValue;
        Dictionary<string, object> attributes = item.TryGetValue("attributes", out attributesValue)
            ? attributesValue as Dictionary<string, object> : null;
        Dictionary<string, object> body = new Dictionary<string, object>();
        body["events"] = new object[] { item };
        return new SyncRecord {
            Time = DateTime.Now, Stage = stage, RecordNo = Read(item, "recordNo"),
            Machine = Read(item, "machineName"), Material = Read(item, "materialNo"),
            Tested = ReadInt(attributes, "testedBoardCount"), Good = ReadInt(attributes, "goodBoardCount"),
            Bad = ReadInt(attributes, "badBoardCount"), LastTestTime = Read(attributes, "lastTestTime"),
            HttpStatus = httpStatus, Request = new JavaScriptSerializer().Serialize(body), Response = response ?? ""
        };
    }

    private static string Read(Dictionary<string, object> values, string key)
    {
        object value;
        return values != null && values.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
    }

    private static int ReadInt(Dictionary<string, object> values, string key)
    {
        object value;
        return values != null && values.TryGetValue(key, out value) && value != null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0;
    }
}

internal sealed class SyncHistory
{
    private readonly string directory;
    private readonly string[] secrets;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private readonly object appendLock = new object();

    public SyncHistory(string directory, params string[] secrets)
    {
        this.directory = directory;
        this.secrets = secrets ?? new string[0];
    }

    public void Append(SyncRecord record)
    {
        if (record == null) throw new ArgumentNullException("record");
        lock (appendLock)
        {
            record.Response = Limit(Redact(record.Response ?? ""), 8192);
            record.Request = Redact(record.Request ?? "");
            record.RecordNo = Redact(record.RecordNo ?? "");
            record.Machine = Redact(record.Machine ?? "");
            record.Material = Redact(record.Material ?? "");
            record.LastTestTime = Redact(record.LastTestTime ?? "");
            string json = Redact(serializer.Serialize(record));
            string path = Path.Combine(directory, record.Time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            Directory.CreateDirectory(directory);
            using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(json);
                writer.Flush();
                stream.Flush(true);
            }
        }
    }

    public IList<SyncRecord> ReadPage(DateTime day, int skip, int take, out int invalidLines)
    {
        if (skip < 0 || take < 1 || take > 500) throw new ArgumentOutOfRangeException("take");
        if (skip > int.MaxValue - take) throw new ArgumentOutOfRangeException("skip");
        invalidLines = 0;
        Queue<SyncRecord> window = new Queue<SyncRecord>();
        string path = Path.Combine(directory, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        if (!File.Exists(path)) return new List<SyncRecord>();
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    SyncRecord record = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<SyncRecord>(line);
                    if (record == null || record.Time == default(DateTime) || string.IsNullOrEmpty(record.Stage))
                        throw new InvalidDataException("Invalid history row.");
                    if (record.Time.Kind == DateTimeKind.Utc)
                        record.Time = DateTime.SpecifyKind(record.Time.AddHours(8), DateTimeKind.Unspecified);
                    window.Enqueue(record);
                    if (window.Count > skip + take) window.Dequeue();
                }
                catch (Exception error)
                {
                    if (error is OutOfMemoryException) throw;
                    invalidLines++;
                }
            }
        }
        List<SyncRecord> rows = new List<SyncRecord>(window);
        rows.Reverse();
        if (skip >= rows.Count) return new List<SyncRecord>();
        return rows.GetRange(skip, Math.Min(take, rows.Count - skip));
    }

    public DateTime LatestDay()
    {
        DateTime latest = DateTime.MinValue;
        if (!Directory.Exists(directory)) return DateTime.Today;
        foreach (string path in Directory.GetFiles(directory, "*.jsonl"))
        {
            DateTime day;
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out day) && day > latest) latest = day;
        }
        return latest == DateTime.MinValue ? DateTime.Today : latest;
    }

    private string Redact(string value)
    {
        foreach (string secret in secrets)
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "[REDACTED]");
        return value;
    }

    private static string Limit(string value, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= bytes) return value;
        int length = value.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(value.Substring(0, length)) > bytes) length--;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value.Substring(0, length);
    }
}
