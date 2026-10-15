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
            Console.WriteLine("PASS 3 GUI tests");
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
