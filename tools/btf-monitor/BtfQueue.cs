using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Append-only segments keep a large outage queue from being rewritten for every device sample.
internal sealed class BtfQueue : IDisposable
{
    private const long SegmentLimit = 4 * 1024 * 1024;
    private readonly string directory;
    private readonly FileStream ownership;
    private readonly object sync = new object();
    private long readSegment;
    private long readOffset;
    private long writeSegment;
    private long nextOffset = -1;
    private string cached;

    internal BtfQueue(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
        ownership = new FileStream(Path.Combine(this.directory, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            long[] segments = Directory.GetFiles(this.directory, "*.bin").Select(ParseSegment).Where(x => x > 0).OrderBy(x => x).ToArray();
            writeSegment = segments.Length == 0 ? 1 : segments[segments.Length - 1];
            RecoverLastSegment();
            string cursor = Path.Combine(this.directory, "cursor.txt");
            if (File.Exists(cursor))
            {
                string[] parts = File.ReadAllText(cursor, Encoding.ASCII).Trim().Split(',');
                if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out readSegment)
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out readOffset))
                    throw new InvalidDataException("标特福队列游标损坏：" + cursor);
            }
            else { readSegment = segments.Length == 0 ? 1 : segments[0]; readOffset = 0; SaveCursor(); }
            if (readSegment > writeSegment || readOffset < 0 || (File.Exists(SegmentPath(readSegment)) && readOffset > new FileInfo(SegmentPath(readSegment)).Length))
                throw new InvalidDataException("标特福队列游标越界。");
        }
        catch { ownership.Dispose(); throw; }
    }

    private void RecoverLastSegment()
    {
        string path = SegmentPath(writeSegment);
        if (!File.Exists(path)) return;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            byte[] size = new byte[4];
            while (stream.Position < stream.Length)
            {
                long start = stream.Position;
                if (stream.Length - start < 4) { stream.SetLength(start); break; }
                ReadExactly(stream, size);
                int length = BitConverter.ToInt32(size, 0);
                if (length <= 0 || length > 1024 * 1024 || stream.Length - stream.Position < length)
                { stream.SetLength(start); break; }
                stream.Position += length;
            }
            stream.Flush(true);
        }
    }

    internal void Enqueue(string payload)
    {
        if (payload == null) throw new ArgumentNullException("payload");
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        if (bytes.Length == 0 || bytes.Length > 1024 * 1024) throw new ArgumentException("队列记录长度无效。");
        lock (sync)
        {
            string path = SegmentPath(writeSegment);
            long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (length > 0 && length + bytes.Length + 4 > SegmentLimit)
            {
                writeSegment++;
                path = SegmentPath(writeSegment);
            }
            using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                long start = stream.Position;
                try
                {
                    byte[] size = BitConverter.GetBytes(bytes.Length);
                    stream.Write(size, 0, size.Length);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                catch { stream.SetLength(start); throw; }
            }
        }
    }

    internal string Peek()
    {
        lock (sync)
        {
            if (cached != null) return cached;
            while (true)
            {
                string path = SegmentPath(readSegment);
                if (!File.Exists(path))
                {
                    if (readSegment < writeSegment) { MoveToNextSegment(); continue; }
                    return null;
                }
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (readOffset == stream.Length)
                    {
                        if (readSegment < writeSegment) { MoveToNextSegment(); continue; }
                        return null;
                    }
                    if (readOffset > stream.Length || stream.Length - readOffset < 4)
                        throw new InvalidDataException("标特福队列记录不完整：" + path);
                    stream.Position = readOffset;
                    byte[] size = new byte[4];
                    ReadExactly(stream, size);
                    int length = BitConverter.ToInt32(size, 0);
                    if (length <= 0 || length > 1024 * 1024 || stream.Length - stream.Position < length)
                        throw new InvalidDataException("标特福队列记录长度无效：" + path);
                    byte[] body = new byte[length];
                    ReadExactly(stream, body);
                    nextOffset = stream.Position;
                    cached = Encoding.UTF8.GetString(body);
                    return cached;
                }
            }
        }
    }

    internal void Acknowledge()
    {
        lock (sync)
        {
            if (cached == null || nextOffset < 0) throw new InvalidOperationException("没有正在发送的队列记录。");
            long old = readOffset;
            readOffset = nextOffset;
            try { SaveCursor(); }
            catch { readOffset = old; throw; }
            cached = null;
            nextOffset = -1;
        }
    }

    private void MoveToNextSegment()
    {
        long old = readSegment;
        readSegment++;
        readOffset = 0;
        try { SaveCursor(); }
        catch { readSegment = old; throw; }
        File.Delete(SegmentPath(old));
    }

    private void SaveCursor()
    {
        string path = Path.Combine(directory, "cursor.txt");
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, readSegment.ToString(CultureInfo.InvariantCulture) + "," + readOffset.ToString(CultureInfo.InvariantCulture), Encoding.ASCII);
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private string SegmentPath(long number) { return Path.Combine(directory, number.ToString("D10", CultureInfo.InvariantCulture) + ".bin"); }
    private static long ParseSegment(string path)
    {
        long number;
        return long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : 0;
    }
    private static void ReadExactly(Stream stream, byte[] bytes)
    {
        int read = 0;
        while (read < bytes.Length)
        {
            int count = stream.Read(bytes, read, bytes.Length - read);
            if (count <= 0) throw new EndOfStreamException();
            read += count;
        }
    }
    public void Dispose() { ownership.Dispose(); }
}
