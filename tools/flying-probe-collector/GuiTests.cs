using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

internal static class GuiTests
{
    [STAThread]
    private static int Main()
    {
        try
        {
            InvalidConfigOpensWindow();
            MaskedTokensAndSave();
            StartStopKeepsUiResponsive();
            HistoryViewShowsSavedAndLiveRecords();
            SelectingRowShowsDetailsWithoutSecrets();
            PaginationKeepsRowsBounded();
            HistoryGridRemainsUsableAtDefaultSize();
            Console.WriteLine("PASS 7 GUI tests");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void InvalidConfigOpensWindow()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("待配置"), "invalid config visible");
                Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(path), "pending.json.lock")), "invalid config does not lock queue");
                form.Close();
            }
        });
    }

    private static void MaskedTokensAndSave()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                TextBox gateway = Find<TextBox>(form, "GatewayTokenBox");
                TextBox bearer = Find<TextBox>(form, "BearerTokenBox");
                Assert(gateway.UseSystemPasswordChar && bearer.UseSystemPasswordChar, "tokens are masked");
                gateway.Text = "real-gateway";
                bearer.Text = "real-bearer";
                Find<Button>(form, "SaveButton").PerformClick();
                Assert(Settings.Load(path).GatewayToken == "real-gateway", "saved configuration reloads");
                Assert(!Find<Label>(form, "StatusLabel").Text.Contains("real-gateway"), "status hides token");
                form.Close();
            }
        });
    }

    private static void StartStopKeepsUiResponsive()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                Find<TextBox>(form, "GatewayTokenBox").Text = "real-gateway";
                Find<Button>(form, "SaveButton").PerformClick();
                Find<Button>(form, "StartButton").PerformClick();
                Application.DoEvents();
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("运行中"), "window remains responsive after start");
                Find<Button>(form, "StopButton").PerformClick();
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (Find<Label>(form, "StatusLabel").Text.Contains("正在停止") && DateTime.UtcNow < deadline)
                { Application.DoEvents(); Thread.Sleep(20); }
                Assert(Find<TextBox>(form, "GatewayTokenBox").Enabled, "configuration editable after stop");
                using (new CollectorOwnership(Path.Combine(Path.GetDirectoryName(path), "pending.json"))) { }
                form.Close();
            }
        });
    }

    private static void HistoryViewShowsSavedAndLiveRecords()
    {
        WithConfig(delegate(string path)
        {
            string directory = Path.Combine(Path.GetDirectoryName(path), "history");
            SyncHistory history = new SyncHistory(directory);
            SyncRecord old = SyncRecord.FromEvent("success", Event("R-old"), 200, "ok");
            old.Time = DateTime.Today.AddDays(-1).AddHours(10);
            history.Append(old);
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                view.ShowDay(old.Time);
                Assert(Find<DataGridView>(view, "HistoryGrid").Rows.Count == 1, "saved old-day record visible");
                SyncRecord live = SyncRecord.FromEvent("success", Event("R-live"), 200, "ok");
                history.Append(live);
                view.ShowRecent(); view.AddLive(live);
                Assert(Find<DataGridView>(view, "HistoryGrid").Rows.Count == 1, "live record visible");
            }
        });
    }

    private static void SelectingRowShowsDetailsWithoutSecrets()
    {
        WithConfig(delegate(string path)
        {
            SyncHistory history = new SyncHistory(Path.Combine(Path.GetDirectoryName(path), "history"), "gateway-secret");
            history.Append(SyncRecord.FromEvent("failed", Event("R-1"), 500, "gateway-secret error"));
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                view.ShowRecent();
                Find<DataGridView>(view, "HistoryGrid").Rows[0].Selected = true;
                string detail = Find<TextBox>(view, "RequestDetailBox").Text + Find<TextBox>(view, "ResponseDetailBox").Text;
                Assert(detail.Contains("FPT-01") && detail.Contains("R-1"), "details include event");
                Assert(!detail.Contains("gateway-secret"), "details hide token");
            }
        });
    }

    private static void PaginationKeepsRowsBounded()
    {
        WithConfig(delegate(string path)
        {
            SyncHistory history = new SyncHistory(Path.Combine(Path.GetDirectoryName(path), "history"));
            for (int i = 0; i < 105; i++) history.Append(SyncRecord.FromEvent("queued", Event("R-" + i), 0, ""));
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                view.ShowRecent();
                DataGridView grid = Find<DataGridView>(view, "HistoryGrid");
                Assert(grid.Rows.Count == 100, "first page bounded");
                Find<Button>(view, "OlderButton").PerformClick();
                Assert(grid.Rows.Count == 5, "older page contains remaining records");
            }
        });
    }

    private static void HistoryGridRemainsUsableAtDefaultSize()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                Assert(Find<DataGridView>(form, "HistoryGrid").Height >= 150,
                    "sync records table has space for several rows");
                form.Close();
            }
        });
    }

    private static System.Collections.Generic.Dictionary<string, object> Event(string recordNo)
    {
        System.Collections.Generic.Dictionary<string, object> item = ProbeRecords.CreateEvent("FPT-01", "MAT-1", "FPT-01/MAT-1.txt",
            DateTime.Now.AddMinutes(-1), DateTime.Now, ProbeRecords.Parse("Test_Counters 2 2 0 01:27:09"));
        item["recordNo"] = recordNo;
        return item;
    }

    private static void WithConfig(Action<string> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "fpt-gui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
            string path = Path.Combine(directory, "FlyingProbeCollector.ini");
            File.WriteAllText(path, "SourceRoot=" + source + "\r\nQmsBaseUrl=http://127.0.0.1:1\r\n" +
                "GatewayToken=REPLACE_WITH_QMS_GATEWAY_TOKEN\r\nQueueFile=.\\pending.json\r\n" +
                "CheckpointFile=.\\checkpoint.json\r\nLogFile=.\\collector.log\r\n");
            test(path);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static T Find<T>(Control root, string name) where T : Control
    {
        Control[] results = root.Controls.Find(name, true);
        if (results.Length != 1 || !(results[0] is T)) throw new Exception("Missing control: " + name);
        return (T)results[0];
    }

    private static void Assert(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }
}
