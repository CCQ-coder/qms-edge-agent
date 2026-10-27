using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal sealed class BtfServer : IDisposable
{
    private readonly BtfSettings settings;
    private readonly Action<BtfSnapshot> received;
    private readonly Action<string> report;
    private readonly object clientsLock = new object();
    private readonly List<TcpClient> clients = new List<TcpClient>();
    private readonly List<Thread> clientThreads = new List<Thread>();
    private readonly ManualResetEvent receiveGate = new ManualResetEvent(true);
    private readonly BtfDelivery delivery;
    private readonly BtfRecordLog records;
    private TcpListener listener;
    private Thread acceptThread;
    private volatile bool stopping;

    internal BtfServer(BtfSettings settings, Action<BtfSnapshot> received, Action<string> report)
    {
        this.settings = settings; this.received = received; this.report = report;
        delivery = new BtfDelivery(settings, report);
        records = new BtfRecordLog(settings.BaseDirectory, settings.RetentionDays, report);
    }

    internal void Start()
    {
        listener = new TcpListener(IPAddress.Parse(settings.ListenHost), settings.ListenPort);
        try
        {
            listener.Server.ExclusiveAddressUse = true;
            listener.Start(128);
            delivery.Start();
            acceptThread = new Thread(Accept);
            acceptThread.IsBackground = true;
            acceptThread.Name = "btf-accept";
            acceptThread.Start();
            records.ScheduleArchive();
            report("标特福监听已启动：" + settings.ListenHost + ":" + settings.ListenPort);
        }
        catch { listener.Stop(); throw; }
    }

    internal void PauseReceive(bool paused)
    {
        if (paused) receiveGate.Reset(); else receiveGate.Set();
        report(paused ? "已暂停接收设备数据。" : "已恢复接收设备数据。");
    }

    private void Accept()
    {
        while (!stopping)
        {
            TcpClient client;
            try { client = listener.AcceptTcpClient(); }
            catch (SocketException) { if (stopping) break; continue; }
            catch (ObjectDisposedException) { break; }
            lock (clientsLock) clients.Add(client);
            Thread thread = new Thread(delegate() { Handle(client); });
            thread.IsBackground = true;
            thread.Name = "btf-device";
            lock (clientsLock) clientThreads.Add(thread);
            thread.Start();
        }
    }

    private void Handle(TcpClient client)
    {
        BtfMachineState state = new BtfMachineState();
        string remote = client.Client.RemoteEndPoint.ToString();
        report("设备连接：" + remote);
        DateTime lastData = DateTime.UtcNow;
        try
        {
            client.NoDelay = true;
            using (NetworkStream stream = client.GetStream())
            {
                stream.ReadTimeout = 250;
                BtfStreamParser parser = new BtfStreamParser(
                    delegate(string line) { state.ApplyCommand(line); report(state.MachineName + " " + line); },
                    delegate(string frame)
                    {
                        BtfSnapshot snapshot = state.ApplyRecord(frame);
                        records.Append(snapshot);
                        delivery.Enqueue(snapshot);
                        received(snapshot);
                    });
                byte[] buffer = new byte[8192];
                while (!stopping)
                {
                    receiveGate.WaitOne();
                    int count;
                    try { count = stream.Read(buffer, 0, buffer.Length); }
                    catch (IOException error)
                    {
                        if (!IsTimeout(error)) throw;
                        parser.FlushPendingCommand();
                        if ((DateTime.UtcNow - lastData).TotalSeconds >= settings.IdleSeconds)
                        {
                            report((state.MachineName.Length == 0 ? remote : state.MachineName) + " 空闲断线");
                            break;
                        }
                        continue;
                    }
                    if (count <= 0) break;
                    lastData = DateTime.UtcNow;
                    parser.Feed(buffer, 0, count);
                }
            }
        }
        catch (Exception error) { if (!stopping) report((state.MachineName.Length == 0 ? remote : state.MachineName) + " 接收失败：" + error.Message); }
        finally
        {
            try { client.Close(); } catch { }
            lock (clientsLock) { clients.Remove(client); clientThreads.Remove(Thread.CurrentThread); }
            report("设备断开：" + (state.MachineName.Length == 0 ? remote : state.MachineName));
        }
    }

    private static bool IsTimeout(IOException error)
    {
        SocketException socket = error.InnerException as SocketException;
        return socket != null && socket.SocketErrorCode == SocketError.TimedOut;
    }

    public void Dispose()
    {
        stopping = true;
        receiveGate.Set();
        if (listener != null) listener.Stop();
        TcpClient[] open;
        Thread[] threads;
        lock (clientsLock) { open = clients.ToArray(); threads = clientThreads.ToArray(); }
        foreach (TcpClient client in open) try { client.Close(); } catch { }
        if (acceptThread != null && acceptThread != Thread.CurrentThread) acceptThread.Join(3000);
        foreach (Thread thread in threads) if (thread != Thread.CurrentThread) thread.Join(3000);
        delivery.Dispose();
        receiveGate.Dispose();
    }
}

internal sealed class BtfRecordLog
{
    private readonly string root;
    private readonly int retentionDays;
    private readonly Action<string> report;
    private readonly object sync = new object();
    private DateTime lastCleanDay;
    private bool archiveRunning;
    private bool archiveRequested;

    internal BtfRecordLog(string baseDirectory, int retentionDays, Action<string> report)
    {
        root = Path.Combine(baseDirectory, "records");
        this.retentionDays = retentionDays;
        this.report = report;
        Directory.CreateDirectory(root);
        lastCleanDay = DateTime.Today;
    }

    private void CleanExpired(DateTime today)
    {
        foreach (string directory in Directory.GetDirectories(root))
        {
            DateTime day;
            if (!DateTime.TryParseExact(Path.GetFileName(directory), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out day) || day.Date >= today.AddDays(-retentionDays)) continue;
            if (day.Date >= DateTime.Today.AddDays(-retentionDays)) continue;
            try { Directory.Delete(directory, true); }
            catch (Exception error) { report("过期记录清理失败：" + directory + " " + error.Message); }
        }
    }

    internal void Append(BtfSnapshot snapshot)
    {
        bool newDay = false;
        lock (sync)
        {
            DateTime today = DateTime.Today;
            if (today != lastCleanDay) { lastCleanDay = today; newDay = true; }
            string date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string directory = Path.Combine(root, date);
            Directory.CreateDirectory(directory);
            string safeName = snapshot.MachineName;
            foreach (char invalid in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(invalid, '_');
            string path = Path.Combine(directory, safeName + ".jsonl");
            using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                byte[] line = new UTF8Encoding(false).GetBytes(snapshot.Serialize() + "\n");
                stream.Write(line, 0, line.Length);
                stream.Flush(true);
            }
        }
        if (newDay) ScheduleArchive();
    }

    internal void ScheduleArchive()
    {
        lock (sync)
        {
            archiveRequested = true;
            if (archiveRunning) return;
            archiveRunning = true;
        }
        ThreadPool.QueueUserWorkItem(delegate(object ignored)
        {
            while (true)
            {
                lock (sync)
                {
                    if (!archiveRequested) { archiveRunning = false; return; }
                    archiveRequested = false;
                }
                DateTime today = DateTime.Today;
                try { CleanExpired(today); }
                catch (Exception error) { report("过期记录清理失败：" + error.Message); }
                try { CompressClosedDays(today); }
                catch (Exception error) { report("历史记录压缩失败：" + error.Message); }
            }
        });
    }

    internal void CompressClosedDays(DateTime today)
    {
        string[] directories;
        lock (sync) directories = Directory.GetDirectories(root);
        foreach (string directory in directories)
        {
            DateTime day;
            if (!DateTime.TryParseExact(Path.GetFileName(directory), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out day) || day.Date >= today.Date) continue;
            foreach (string source in Directory.GetFiles(directory, "*.jsonl"))
            {
                string temporary = source + ".gz.tmp";
                try
                {
                    long length;
                    DateTime modified;
                    using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (FileStream output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        length = input.Length;
                        modified = File.GetLastWriteTimeUtc(source);
                        using (GZipStream gzip = new GZipStream(output, CompressionMode.Compress, true))
                        {
                            byte[] buffer = new byte[81920];
                            long remaining = length;
                            while (remaining > 0)
                            {
                                int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                                if (count == 0) throw new IOException("原始记录在压缩期间被截断");
                                gzip.Write(buffer, 0, count);
                                remaining -= count;
                            }
                        }
                        output.Flush(true);
                    }
                    lock (sync)
                    {
                        FileInfo current = new FileInfo(source);
                        if (day.Date >= DateTime.Today || current.Length != length || current.LastWriteTimeUtc != modified)
                            throw new IOException("原始记录在压缩期间发生变化");
                        string archive = source + ".gz";
                        for (int part = 1; File.Exists(archive); part++)
                            archive = source + "." + part.ToString(CultureInfo.InvariantCulture) + ".gz";
                        // shortcut: a crash before deleting the source can leave a duplicate volume; add a recovery marker if this becomes frequent.
                        File.Move(temporary, archive);
                        try { File.Delete(source); }
                        catch
                        {
                            try { File.Delete(archive); } catch { }
                            throw;
                        }
                    }
                }
                catch (Exception error)
                {
                    try { File.Delete(temporary); } catch { }
                    report("历史记录压缩失败：" + source + " " + error.Message);
                }
            }
        }
    }
}
