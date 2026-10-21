using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal sealed class BtfStreamParser
{
    private static readonly Regex FrameStart = new Regex(@"^\d{4}-\d{1,2}-\d{1,2}\(\d\)\d{1,2}:\d{1,2}:", RegexOptions.CultureInvariant);
    private readonly List<byte> pending = new List<byte>();
    private readonly Action<string> command;
    private readonly Action<string> record;

    internal BtfStreamParser(Action<string> command, Action<string> record)
    {
        this.command = command;
        this.record = record;
    }

    internal void Feed(byte[] bytes, int offset, int count)
    {
        for (int index = offset; index < offset + count; index++) pending.Add(bytes[index]);
        Drain();
        if (pending.Count > 65536) throw new InvalidOperationException("标特福报文超过缓冲上限。");
    }

    // The original client may send one command per TCP write without a newline.
    // Call after an idle read timeout so a split command gets a chance to finish.
    internal void FlushPendingCommand()
    {
        Drain();
        if (pending.Count == 0 || pending[0] != '#') return;
        if (FindRecordStart() >= 0) return;
        string value = Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\0', '\r', '\n');
        if (value.IndexOf(':') < 0) return;
        pending.Clear();
        command(value);
    }

    private void Drain()
    {
        while (pending.Count > 0)
        {
            if (pending[0] == '\r' || pending[0] == '\n' || pending[0] == 0)
            {
                pending.RemoveAt(0);
                continue;
            }
            if (pending[0] == '#')
            {
                int end = pending.IndexOf((byte)'\n');
                int nextCommand = pending.IndexOf((byte)'#', 1);
                int frameStart = FindRecordStart();
                if (nextCommand >= 0 && (end < 0 || nextCommand < end)) end = nextCommand;
                if (frameStart >= 0 && (end < 0 || frameStart < end)) end = frameStart;
                if (end < 0) return;
                bool newline = pending[end] == '\n';
                string value = Encoding.UTF8.GetString(pending.GetRange(0, end).ToArray()).TrimEnd('\r');
                pending.RemoveRange(0, end + (newline ? 1 : 0));
                command(value);
                continue;
            }
            if (pending.Count < 80) return;
            string frame = Encoding.ASCII.GetString(pending.GetRange(0, 80).ToArray());
            pending.RemoveRange(0, 80);
            record(frame);
        }
    }

    private int FindRecordStart()
    {
        for (int index = 1; index < pending.Count - 14; index++)
        {
            if (pending[index] < '0' || pending[index] > '9') continue;
            int length = Math.Min(24, pending.Count - index);
            if (FrameStart.IsMatch(Encoding.ASCII.GetString(pending.GetRange(index, length).ToArray()))) return index;
        }
        return -1;
    }
}

internal sealed class BtfSnapshot
{
    internal string MachineName;
    internal string ProgramName;
    internal string OperatorName;
    internal string SpindleStatus;
    internal string State;
    internal string DeviceTime;
    internal string ScreenText;
    internal string RawRecord;
    internal int WorkCount;
    internal double BootSeconds;
    internal double WorkSeconds;
    internal int FinishedCount;
    internal double ProgressPercent;
    internal double RouteLength;

    internal string ToLegacyJson()
    {
        var values = new Dictionary<string, object>();
        values["id"] = "1";
        values["machinename"] = MachineName;
        values["machinetype"] = "0";
        values["spindlestatus"] = SpindleStatus ?? "";
        values["state"] = State;
        values["updateCurTime"] = DeviceTime;
        values["runtime"] = BootSeconds.ToString("0", CultureInfo.InvariantCulture);
        values["username"] = OperatorName ?? "";
        values["programname"] = ProgramName ?? "";
        values["finHole"] = FinishedCount.ToString(CultureInfo.InvariantCulture);
        values["routLen"] = RouteLength.ToString("0.###", CultureInfo.InvariantCulture);
        values["finper"] = ProgressPercent.ToString("0.0", CultureInfo.InvariantCulture);
        values["screentext"] = ScreenText ?? "";
        return new JavaScriptSerializer().Serialize(new object[] { new Dictionary<string, object> { { "type", "RealtimeInf" } }, values });
    }

    internal string Serialize() { return new JavaScriptSerializer().Serialize(this.ToDictionary()); }

    internal Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object> {
            {"machine", MachineName}, {"program", ProgramName}, {"operator", OperatorName},
            {"spindles", SpindleStatus}, {"state", State}, {"time", DeviceTime},
            {"alarm", ScreenText}, {"raw", RawRecord}, {"workCount", WorkCount},
            {"bootSeconds", BootSeconds}, {"workSeconds", WorkSeconds},
            {"finishedCount", FinishedCount}, {"progress", ProgressPercent}, {"route", RouteLength}
        };
    }

    internal static BtfSnapshot Deserialize(string json)
    {
        var values = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
        if (values == null) throw new FormatException("队列中的标特福快照无效。");
        return new BtfSnapshot {
            MachineName = Text(values, "machine"), ProgramName = Text(values, "program"),
            OperatorName = Text(values, "operator"), SpindleStatus = Text(values, "spindles"),
            State = Text(values, "state"), DeviceTime = Text(values, "time"),
            ScreenText = Text(values, "alarm"), RawRecord = Text(values, "raw"),
            WorkCount = Number(values, "workCount"), BootSeconds = Decimal(values, "bootSeconds"),
            WorkSeconds = Decimal(values, "workSeconds"), FinishedCount = Number(values, "finishedCount"),
            ProgressPercent = Decimal(values, "progress"), RouteLength = Decimal(values, "route")
        };
    }

    private static string Text(Dictionary<string, object> values, string key)
    {
        object value;
        return values.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
    }
    private static int Number(Dictionary<string, object> values, string key) { return (int)Decimal(values, key); }
    private static double Decimal(Dictionary<string, object> values, string key)
    {
        object value;
        return values.TryGetValue(key, out value) && value != null ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0;
    }
}

internal sealed class BtfMachineState
{
    private static readonly Regex RecordPattern = new Regex(
        @"^(\d{4}-\d{1,2}-\d{1,2})\(\d\)(\d{1,2}):(\d{1,2}):\s*(.+)$", RegexOptions.CultureInvariant);
    private string machineName = "";
    private string programName = "";
    private string operatorName = "";
    private string screenText = "";
    private int spindleCount;
    private uint spindleMask;
    private double routeLength;

    internal string MachineName { get { return machineName; } }

    internal void ApplyCommand(string line)
    {
        int separator = line.IndexOf(':');
        if (separator < 0 || line.Length == 0 || line[0] != '#') return;
        string key = line.Substring(1, separator - 1);
        string value = line.Substring(separator + 1).Trim();
        if (key == "Username") machineName = value;
        else if (key == "Filename") programName = value;
        else if (key == "Operator") operatorName = value;
        else if (key == "ScreenText") screenText = value;
        else if (key == "RoutLength") double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out routeLength);
        else if (key == "Spindles")
        {
            string[] parts = value.Split(',');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out spindleCount);
                uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out spindleMask);
                spindleCount = Math.Min(Math.Max(spindleCount, 0), 32);
            }
        }
    }

    internal BtfSnapshot ApplyRecord(string frame)
    {
        if (string.IsNullOrWhiteSpace(machineName)) throw new FormatException("统计记录到达前缺少 #Username。");
        if (frame == null || Encoding.ASCII.GetByteCount(frame) != 80) throw new FormatException("统计记录必须为 80 字节。");
        Match match = RecordPattern.Match(frame.TrimEnd('\0', ' ', '\r', '\n'));
        if (!match.Success) throw new FormatException("统计记录的时间格式无效。");
        DateTime date;
        if (!DateTime.TryParseExact(match.Groups[1].Value + " " + match.Groups[2].Value.PadLeft(2, '0') + ":" + match.Groups[3].Value.PadLeft(2, '0'),
            "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            throw new FormatException("统计记录的日期无效。");
        string[] fields = match.Groups[4].Value.Split(',');
        if (fields.Length != 7) throw new FormatException("统计记录字段数应为 7。");
        int workCount, finishedCount;
        double boot, work, progress, recordRoute;
        uint flags;
        if (!int.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out workCount)
            || !double.TryParse(fields[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out boot)
            || !double.TryParse(fields[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out work)
            || !int.TryParse(fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out finishedCount)
            || !double.TryParse(fields[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out progress)
            || !uint.TryParse(fields[5].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out flags)
            || !double.TryParse(fields[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out recordRoute))
            throw new FormatException("统计记录数值无效。");
        if (workCount < 0 || boot < 0 || work < 0 || finishedCount < 0 || progress < 0 || progress > 100 || recordRoute < 0)
            throw new FormatException("统计记录数值超出范围。");
        StringBuilder spindle = new StringBuilder();
        for (int i = 0; i < spindleCount; i++) spindle.Append((spindleMask & (1u << i)) != 0 ? '1' : '0');
        string state = (flags & 8) != 0 ? "STOP" : (flags & 4) != 0 ? "WAIT" : (flags & 2) != 0 ? "ALAM" : (flags & 1) != 0 ? "WORK" : "IDLE";
        return new BtfSnapshot {
            MachineName = machineName, ProgramName = programName, OperatorName = operatorName,
            ScreenText = screenText, SpindleStatus = spindle.ToString(), State = state,
            DeviceTime = date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            WorkCount = workCount, BootSeconds = boot, WorkSeconds = work,
            FinishedCount = finishedCount, ProgressPercent = progress,
            RouteLength = routeLength > 0 ? routeLength : recordRoute, RawRecord = frame
        };
    }
}
