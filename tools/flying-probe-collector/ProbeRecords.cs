using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

internal sealed class ProbeCounters
{
    public readonly int Tested;
    public readonly int Good;
    public readonly int Bad;
    public readonly string LastTestTime;

    public ProbeCounters(int tested, int good, int bad, string lastTestTime)
    {
        Tested = tested;
        Good = good;
        Bad = bad;
        LastTestTime = lastTestTime;
    }
}

internal static class ProbeRecords
{
    private static readonly Regex LastTestTimePattern = new Regex(@"^[0-9]+:[0-5][0-9]:[0-5][0-9]$", RegexOptions.CultureInvariant);

    public static ProbeCounters Parse(string content)
    {
        if (content == null) throw new ArgumentNullException("content");
        ProbeCounters counters = null;
        using (StringReader reader = new StringReader(content))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                string[] values = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length == 0 || values[0] != "Test_Counters") continue;
                if (counters != null || values.Length != 5) throw new FormatException("Test_Counters must contain exactly four values on one line.");
                int tested;
                int good;
                int bad;
                if (!int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out tested) ||
                    !int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out good) ||
                    !int.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out bad) ||
                    !LastTestTimePattern.IsMatch(values[4]))
                {
                    throw new FormatException("Test_Counters contains an invalid count or time.");
                }
                counters = new ProbeCounters(tested, good, bad, values[4]);
            }
        }
        if (counters == null) throw new FormatException("Test_Counters was not found.");
        return counters;
    }

    public static Dictionary<string, object> CreateEvent(string machine, string material, string relativePath,
        DateTime createdLocal, DateTime modifiedLocal, ProbeCounters counters)
    {
        if (counters == null) throw new ArgumentNullException("counters");
        string sourceFile = relativePath.Replace('\\', '/');
        string startTime = createdLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        string updatedAt = modifiedLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        string identity = string.Join("\n", new string[] {
            sourceFile.ToUpperInvariant(),
            createdLocal.Ticks.ToString(CultureInfo.InvariantCulture),
            modifiedLocal.Ticks.ToString(CultureInfo.InvariantCulture),
            counters.Tested.ToString(CultureInfo.InvariantCulture),
            counters.Good.ToString(CultureInfo.InvariantCulture),
            counters.Bad.ToString(CultureInfo.InvariantCulture),
            counters.LastTestTime
        });
        byte[] hash;
        using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(identity));
        StringBuilder recordNo = new StringBuilder("FPT-");
        foreach (byte item in hash) recordNo.Append(item.ToString("x2", CultureInfo.InvariantCulture));

        Dictionary<string, object> attributes = new Dictionary<string, object>();
        attributes["sourceProtocol"] = "FLYING_PROBE_TXT_V1";
        attributes["startTime"] = startTime;
        attributes["updatedAt"] = updatedAt;
        attributes["testedBoardCount"] = counters.Tested;
        attributes["goodBoardCount"] = counters.Good;
        attributes["badBoardCount"] = counters.Bad;
        attributes["lastTestTime"] = counters.LastTestTime;
        attributes["sourceFile"] = sourceFile;

        Dictionary<string, object> eventItem = new Dictionary<string, object>();
        eventItem["machineName"] = machine;
        eventItem["materialNo"] = material;
        eventItem["deviceTime"] = updatedAt;
        eventItem["eventType"] = "FLYING_PROBE_TEST_COUNTERS";
        eventItem["machiningCount"] = counters.Tested;
        eventItem["recordNo"] = recordNo.ToString();
        eventItem["attributes"] = attributes;
        return eventItem;
    }
}
