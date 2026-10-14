using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class PendingEventStore
{
    private readonly string path;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    private List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();

    public PendingEventStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        try
        {
            IList raw = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as IList;
            if (raw == null) throw new InvalidDataException("Pending queue must be a JSON array.");
            foreach (object value in raw)
            {
                Dictionary<string, object> item = value as Dictionary<string, object>;
                if (item == null || RecordNo(item) == null) throw new InvalidDataException("Pending queue contains an invalid event.");
                items.Add(item);
            }
        }
        catch (Exception error)
        {
            throw new InvalidDataException("Pending queue cannot be read: " + path, error);
        }
    }

    public bool Enqueue(Dictionary<string, object> eventItem)
    {
        string recordNo = RecordNo(eventItem);
        if (recordNo == null) throw new ArgumentException("Event must contain recordNo.", "eventItem");
        foreach (Dictionary<string, object> item in items)
        {
            if (RecordNo(item) == recordNo) return false;
        }
        List<Dictionary<string, object>> next = new List<Dictionary<string, object>>(items);
        next.Add(eventItem);
        AtomicJson.Write(path, serializer.Serialize(next));
        items = next;
        return true;
    }

    public IList<Dictionary<string, object>> Snapshot()
    {
        return new List<Dictionary<string, object>>(items);
    }

    public void Remove(string recordNo)
    {
        List<Dictionary<string, object>> next = new List<Dictionary<string, object>>(items);
        int index = next.FindIndex(delegate(Dictionary<string, object> item) { return RecordNo(item) == recordNo; });
        if (index < 0) return;
        next.RemoveAt(index);
        AtomicJson.Write(path, serializer.Serialize(next));
        items = next;
    }

    private static string RecordNo(Dictionary<string, object> item)
    {
        object value;
        return item != null && item.TryGetValue("recordNo", out value) ? value as string : null;
    }
}

internal sealed class ScanCheckpointStore
{
    private readonly string path;
    private readonly JavaScriptSerializer serializer = new JavaScriptSerializer();
    private Dictionary<string, string> records = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ScanCheckpointStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        try
        {
            Dictionary<string, object> raw = serializer.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (raw == null) throw new InvalidDataException("Scan state must be a JSON object.");
            foreach (KeyValuePair<string, object> pair in raw)
            {
                string recordNo = pair.Value as string;
                if (string.IsNullOrEmpty(pair.Key) || string.IsNullOrEmpty(recordNo)) throw new InvalidDataException("Scan state contains an invalid record.");
                records.Add(pair.Key, recordNo);
            }
        }
        catch (Exception error)
        {
            throw new InvalidDataException("Scan state cannot be read: " + path, error);
        }
    }

    public bool Contains(string relativePath, string recordNo)
    {
        string recorded;
        return records.TryGetValue(relativePath, out recorded) && recorded == recordNo;
    }

    public void Record(string relativePath, string recordNo)
    {
        if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(recordNo)) throw new ArgumentException("Scan record requires a path and recordNo.");
        if (Contains(relativePath, recordNo)) return;
        Dictionary<string, string> next = new Dictionary<string, string>(records, StringComparer.OrdinalIgnoreCase);
        next[relativePath] = recordNo;
        AtomicJson.Write(path, serializer.Serialize(next));
        records = next;
    }
}

internal static class AtomicJson
{
    public static void Write(string path, string json)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
