using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
            FirstRunWithoutIniOpensAndSaves();
            ProbeIntervalOutOfRangeShowsChineseMessage();
            IncompleteConfigSavePersistsUrl();
            UnsavedEditCannotStart();
            FailedSaveDoesNotStartUnpersistedConfig();
            MaskedTokensAndSave();
            StartStopKeepsUiResponsive();
            HistoryViewShowsSavedAndLiveRecords();
            SelectingRowShowsDetailsWithoutSecrets();
            PaginationKeepsRowsBounded();
            HistoryGridRemainsUsableAtDefaultSize();
            RecentViewFollowsNewDay();
            HistoryReadsOffUiThread();
            SourceFailureAppearsInWindow();
            WorkerFailureRestoresControls();
            ClosingWhileStoppingExits();
            DeviceMappingSaveSurvivesRestart();
            InvalidDeviceMappingDoesNotBlockTxtCollection();
            HostEditingStopsDuringCollection();
            UnmappedPresenceShowsPendingConfiguration();
            UnsavedDraftDoesNotInheritSavedProbeResult();
            RenamedDraftDoesNotKeepOldOnlineResult();
            PresenceTimesUseLocalClock();
            WindowKeepsHistoryAccessibleOnSmallScreens();
            Console.WriteLine("PASS 24 GUI tests");
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

    private static void FirstRunWithoutIniOpensAndSaves()
    {
        WithConfig(delegate(string path)
        {
            File.Delete(path);
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("待配置"),
                    "first launch without an INI stays open for configuration");
                Find<Button>(form, "SaveButton").PerformClick();
                Assert(File.Exists(path), "first save creates the live INI");
                Assert(!Find<Button>(form, "StopButton").Enabled,
                    "incomplete first-run configuration does not start collection");
                form.Close();
            }
        });
    }

    private static void ProbeIntervalOutOfRangeShowsChineseMessage()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                SelectPresenceTab(form);
                Find<TextBox>(form, "ProbeIntervalBox").Text = "241";
                Find<Button>(form, "SaveHostsButton").PerformClick();
                string message = Find<Label>(form, "HostConfigStatusLabel").Text;
                Assert(message.Contains("探测间隔") && message.Contains("240 秒"),
                    "probe interval range is explained in Chinese");
                Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(path), "FlyingProbeCollector.hosts.json")),
                    "out-of-range probe interval is not saved");
                form.Close();
            }
        });
    }

    private static void IncompleteConfigSavePersistsUrl()
    {
        WithConfig(delegate(string path)
        {
            const string targetUrl = "http://192.0.2.1:8080";
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                Find<TextBox>(form, "QmsBaseUrlBox").Text = targetUrl;
                Find<Button>(form, "SaveButton").PerformClick();
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("配置已保存"), "incomplete configuration is saved as a draft");
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("网关令牌"), "incomplete configuration names the missing gateway token");
                Assert(!Find<Button>(form, "StopButton").Enabled, "incomplete configuration does not start collector");
                form.Close();
            }
            using (CollectorForm reopened = new CollectorForm(path))
            {
                Assert(Find<TextBox>(reopened, "QmsBaseUrlBox").Text == targetUrl, "QMS URL survives restart without gateway token");
            }
        });
    }

    private static void UnsavedEditCannotStart()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                Find<TextBox>(form, "QmsBaseUrlBox").Text = "http://192.0.2.2:8080";
                Find<Button>(form, "StartButton").PerformClick();
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("未保存"), "start warns about unsaved edits");
                Assert(!Find<Button>(form, "StopButton").Enabled, "unsaved edits do not start collector");
                form.Close();
            }
        });
    }

    private static void FailedSaveDoesNotStartUnpersistedConfig()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                Find<TextBox>(form, "QmsBaseUrlBox").Text = "http://192.0.2.3:8080";
                Find<TextBox>(form, "GatewayTokenBox").Text = "valid-test-token";
                File.Delete(path);
                Directory.CreateDirectory(path);
                Find<Button>(form, "SaveButton").PerformClick();
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("保存失败"), "disk failure is shown as save failure");
                Find<Button>(form, "StartButton").PerformClick();
                Assert(Find<Label>(form, "StatusLabel").Text.Contains("未保存"), "failed save cannot start from in-memory settings");
                Assert(!Find<Button>(form, "StopButton").Enabled, "failed save leaves collector stopped");
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
            using (Form host = new Form())
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                host.Controls.Add(view); host.Show(); view.ShowDay(old.Time);
                WaitFor(delegate { return Find<DataGridView>(view, "HistoryGrid").Rows.Count == 1; }, "saved old-day record visible");
                SyncRecord live = SyncRecord.FromEvent("success", Event("R-live"), 200, "ok");
                history.Append(live);
                view.ShowRecent(); view.AddLive(live);
                WaitFor(delegate { return FirstRecordNo(view) == "R-live"; }, "live record visible");
                host.Close();
            }
        });
    }

    private static void SelectingRowShowsDetailsWithoutSecrets()
    {
        WithConfig(delegate(string path)
        {
            SyncHistory history = new SyncHistory(Path.Combine(Path.GetDirectoryName(path), "history"), "gateway-secret");
            history.Append(SyncRecord.FromEvent("failed", Event("R-1"), 500, "gateway-secret error"));
            using (Form host = new Form())
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                host.Controls.Add(view); host.Show();
                view.ShowRecent();
                WaitFor(delegate { return Find<DataGridView>(view, "HistoryGrid").Rows.Count == 1; }, "record loaded for detail");
                Find<DataGridView>(view, "HistoryGrid").Rows[0].Selected = true;
                string detail = Find<TextBox>(view, "RequestDetailBox").Text + Find<TextBox>(view, "ResponseDetailBox").Text;
                Assert(detail.Contains("FPT-01") && detail.Contains("R-1"), "details include event");
                Assert(!detail.Contains("gateway-secret"), "details hide token");
                host.Close();
            }
        });
    }

    private static void PaginationKeepsRowsBounded()
    {
        WithConfig(delegate(string path)
        {
            SyncHistory history = new SyncHistory(Path.Combine(Path.GetDirectoryName(path), "history"));
            for (int i = 0; i < 105; i++) history.Append(SyncRecord.FromEvent("queued", Event("R-" + i), 0, ""));
            using (Form host = new Form())
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                host.Controls.Add(view); host.Show();
                view.ShowRecent();
                DataGridView grid = Find<DataGridView>(view, "HistoryGrid");
                WaitFor(delegate { return grid.Rows.Count == 100; }, "first page bounded");
                Find<Button>(view, "OlderButton").PerformClick();
                WaitFor(delegate { return grid.Rows.Count == 5; }, "older page contains remaining records");
                host.Close();
            }
        });
    }

    private static void RecentViewFollowsNewDay()
    {
        WithConfig(delegate(string path)
        {
            SyncHistory history = new SyncHistory(Path.Combine(Path.GetDirectoryName(path), "history"));
            SyncRecord old = SyncRecord.FromEvent("queued", Event("R-old"), 0, "");
            old.Time = DateTime.Today.AddDays(-1).AddHours(10);
            history.Append(old);
            using (Form host = new Form())
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                host.Controls.Add(view); host.Show();
                WaitFor(delegate { return FirstRecordNo(view) == "R-old"; }, "old recent record initially visible");
                SyncRecord live = SyncRecord.FromEvent("success", Event("R-today"), 204, "ok");
                history.Append(live);
                view.AddLive(live);
                WaitFor(delegate { return FirstRecordNo(view) == "R-today"; }, "recent view follows new calendar day");
                host.Close();
            }
        });
    }

    private static void HistoryReadsOffUiThread()
    {
        WithConfig(delegate(string path)
        {
            string directory = Path.Combine(Path.GetDirectoryName(path), "history");
            SyncHistory history = new SyncHistory(directory);
            using (Form host = new Form())
            using (SyncHistoryView view = new SyncHistoryView(history))
            {
                host.Controls.Add(view); host.Show(); Application.DoEvents();
                history.Append(SyncRecord.FromEvent("queued", Event("R-large"), 0, ""));
                string file = Path.Combine(directory, DateTime.Today.ToString("yyyy-MM-dd") + ".jsonl");
                string line = File.ReadAllText(file).Trim();
                using (StreamWriter writer = new StreamWriter(file, false))
                    for (int i = 0; i < 50000; i++) writer.WriteLine(line);
                Stopwatch timer = Stopwatch.StartNew();
                view.ShowDay(DateTime.Today);
                timer.Stop();
                Assert(timer.ElapsedMilliseconds < 300, "history page request does not block UI thread");
                WaitFor(delegate { return FirstRecordNo(view) == "R-large"; }, "large history read finishes before cleanup");
                host.Close();
            }
        });
    }

    private static string FirstRecordNo(SyncHistoryView view)
    {
        DataGridView grid = Find<DataGridView>(view, "HistoryGrid");
        return grid.Rows.Count == 0 ? "" : Convert.ToString(grid.Rows[0].Cells[8].Value);
    }

    private static void WaitFor(Func<bool> condition, string description)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
        Assert(condition(), description);
    }

    private static void SourceFailureAppearsInWindow()
    {
        WithConfig(delegate(string path)
        {
            string ini = File.ReadAllText(path).Replace("REPLACE_WITH_QMS_GATEWAY_TOKEN", "real-gateway")
                .Replace("SourceRoot=" + Path.Combine(Path.GetDirectoryName(path), "source"),
                    "SourceRoot=" + Path.Combine(Path.GetDirectoryName(path), "missing-source"));
            File.WriteAllText(path, ini);
            using (CollectorForm form = new CollectorForm(path))
            {
                try
                {
                    form.Show();
                    WaitFor(delegate { return Find<Label>(form, "StatusLabel").Text.Contains("重试"); },
                        "source failure visible in status");
                    Assert(Find<Label>(form, "LastErrorLabel").Text.Contains("扫描"), "source error visible in summary");
                }
                finally { form.Close(); WaitFor(delegate { return form.IsDisposed; }, "form closes after source failure"); }
            }
        });
    }

    private static void WorkerFailureRestoresControls()
    {
        WithConfig(delegate(string path)
        {
            string ini = File.ReadAllText(path).Replace("REPLACE_WITH_QMS_GATEWAY_TOKEN", "real-gateway")
                .Replace("LogFile=.\\collector.log", "LogFile=.");
            File.WriteAllText(path, ini);
            using (CollectorForm form = new CollectorForm(path))
            {
                try
                {
                    form.Show();
                    WaitFor(delegate { return Find<Label>(form, "StatusLabel").Text.Contains("已停止"); },
                        "worker exit visible in status");
                    Assert(Find<TextBox>(form, "GatewayTokenBox").Enabled, "controls restored after worker fault");
                }
                finally { form.Close(); WaitFor(delegate { return form.IsDisposed; }, "form closes after worker fault"); }
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

    private static void ClosingWhileStoppingExits()
    {
        WithConfig(delegate(string path)
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            ManualResetEvent connected = new ManualResetEvent(false);
            ManualResetEvent release = new ManualResetEvent(false);
            Thread server = new Thread(delegate()
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        connected.Set();
                        release.WaitOne(5000);
                        byte[] response = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        stream.Write(response, 0, response.Length);
                    }
                }
                catch (SocketException) { }
            });
            server.IsBackground = true;
            server.Start();
            try
            {
                string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                string ini = File.ReadAllText(path).Replace("http://127.0.0.1:1", url)
                    .Replace("REPLACE_WITH_QMS_GATEWAY_TOKEN", "real-gateway");
                File.WriteAllText(path, ini);
                PendingEventStore queue = new PendingEventStore(Path.Combine(Path.GetDirectoryName(path), "pending.json"));
                queue.Enqueue(Event("R-slow"));
                using (CollectorForm form = new CollectorForm(path))
                {
                    form.Show(); Application.DoEvents();
                    DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!connected.WaitOne(0) && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                    Assert(connected.WaitOne(0), "slow request started");
                    Find<Button>(form, "StopButton").PerformClick();
                    form.Close();
                    release.Set();
                    deadline = DateTime.UtcNow.AddSeconds(5);
                    while (!form.IsDisposed && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(20); }
                    Assert(form.IsDisposed, "closing during stop exits after current request");
                }
            }
            finally { release.Set(); listener.Stop(); server.Join(1000); }
        });
    }

    private static void DeviceMappingSaveSurvivesRestart()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                SelectPresenceTab(form);
                Find<Button>(form, "AddHostButton").PerformClick();
                DataGridView grid = Find<DataGridView>(form, "HostsGrid");
                Assert(grid.Rows.Count == 1, "new host row is visible");
                grid.Rows[0].Cells["MachineName"].Value = "WTD-1";
                grid.Rows[0].Cells["HostName"].Value = "probe-controller.local";
                grid.Rows[0].Cells["ProbeMethod"].Value = "TCP";
                grid.Rows[0].Cells["TcpPort"].Value = "445";
                Find<Button>(form, "SaveHostsButton").PerformClick();
                Assert(File.Exists(Path.Combine(Path.GetDirectoryName(path), "FlyingProbeCollector.hosts.json")),
                    "host mapping is saved beside the selected INI");
                form.Close();
            }
            using (CollectorForm reopened = new CollectorForm(path))
            {
                DataGridView grid = Find<DataGridView>(reopened, "HostsGrid");
                Assert(grid.Rows.Count == 1, "saved mapping reloads after restart");
                Assert(Convert.ToString(grid.Rows[0].Cells["HostName"].Value) == "probe-controller.local",
                    "host name survives restart");
                Assert(Convert.ToString(grid.Rows[0].Cells["ProbeMethod"].Value) == "TCP",
                    "probe method survives restart");
            }
        });
    }

    private static void InvalidDeviceMappingDoesNotBlockTxtCollection()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                SelectPresenceTab(form);
                Find<Button>(form, "AddHostButton").PerformClick();
                DataGridView grid = Find<DataGridView>(form, "HostsGrid");
                grid.Rows[0].Cells["MachineName"].Value = "WTD-2";
                grid.Rows[0].Cells["HostName"].Value = "controller-two.local";
                grid.Rows[0].Cells["ProbeMethod"].Value = "TCP";
                grid.Rows[0].Cells["TcpPort"].Value = "70000";
                Find<Button>(form, "SaveHostsButton").PerformClick();
                Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(path), "FlyingProbeCollector.hosts.json")),
                    "invalid TCP port does not replace saved mapping");
                Assert(Find<Label>(form, "HostConfigStatusLabel").Text.Contains("端口"),
                    "invalid port is explained in the window");
                Find<TextBox>(form, "GatewayTokenBox").Text = "test-gateway";
                Find<Button>(form, "SaveButton").PerformClick();
                Find<Button>(form, "StartButton").PerformClick();
                WaitFor(delegate { return Find<Button>(form, "StopButton").Enabled; },
                    "TXT collection still starts with an invalid unsaved host draft");
                Find<Button>(form, "StopButton").PerformClick();
                WaitFor(delegate { return !Find<Button>(form, "StopButton").Enabled; }, "TXT collection stops");
                form.Close();
            }
        });
    }

    private static void HostEditingStopsDuringCollection()
    {
        WithConfig(delegate(string path)
        {
            string ini = File.ReadAllText(path).Replace("REPLACE_WITH_QMS_GATEWAY_TOKEN", "real-gateway");
            File.WriteAllText(path, ini);
            using (CollectorForm form = new CollectorForm(path))
            {
                form.Show(); Application.DoEvents();
                SelectPresenceTab(form);
                WaitFor(delegate { return Find<Button>(form, "StopButton").Enabled; }, "collector auto-starts");
                Assert(!Find<Button>(form, "AddHostButton").Enabled, "host mappings cannot change while collecting");
                Assert(!Find<Button>(form, "SaveHostsButton").Enabled, "host mappings cannot save while collecting");
                Find<Button>(form, "StopButton").PerformClick();
                WaitFor(delegate { return Find<Button>(form, "AddHostButton").Enabled; }, "host edit enabled after stop");
                form.Close();
            }
        });
    }

    private static void UnmappedPresenceShowsPendingConfiguration()
    {
        using (DevicePresenceView view = new DevicePresenceView(new DeviceHosts(), delegate(DeviceHosts ignored) { }))
        {
            view.Update(new PresenceReport {
                MachineName = "WTD-unmapped",
                ObservedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(8)),
                NetworkState = "UNKNOWN",
                SourceState = "READABLE"
            }, null);
            DataGridView grid = Find<DataGridView>(view, "HostsGrid");
            Assert(grid.Rows.Count == 1, "discovered device appears without an explicit mapping");
            Assert(Convert.ToString(grid.Rows[0].Cells["NetworkState"].Value) == "待配置",
                "unmapped control computer is not called online");
            Assert(!view.HasUnsavedChanges, "live status updates do not alter the saved host mapping");
        }
    }

    private static void UnsavedDraftDoesNotInheritSavedProbeResult()
    {
        DeviceHosts hosts = new DeviceHosts();
        hosts.Targets.Add(new HostTarget { MachineName = "WTD-1", HostName = "saved-host.local", Method = "ICMP" });
        using (DevicePresenceView view = new DevicePresenceView(hosts, delegate(DeviceHosts ignored) { }))
        {
            DataGridView grid = Find<DataGridView>(view, "HostsGrid");
            grid.Rows[0].Cells["HostName"].Value = "unsaved-host.local";
            view.Update(new PresenceReport {
                MachineName = "WTD-1", HostName = "saved-host.local",
                ObservedAt = DateTimeOffset.Now, NetworkState = "ONLINE", SourceState = "READABLE"
            }, new PresenceSendResult { HttpStatus = 200, Accepted = true });
            Assert(Convert.ToString(grid.Rows[0].Cells["ActiveHostName"].Value) == "saved-host.local",
                "window identifies the host that was actually probed");
            Assert(Convert.ToString(grid.Rows[0].Cells["NetworkState"].Value) != "在线",
                "unsaved host draft cannot borrow a saved host's online result");
        }
    }

    private static void RenamedDraftDoesNotKeepOldOnlineResult()
    {
        DeviceHosts hosts = new DeviceHosts();
        hosts.Targets.Add(new HostTarget { MachineName = "WTD-A", HostName = "saved-host.local", Method = "ICMP" });
        using (DevicePresenceView view = new DevicePresenceView(hosts, delegate(DeviceHosts ignored) { }))
        {
            DataGridView grid = Find<DataGridView>(view, "HostsGrid");
            PresenceReport report = new PresenceReport {
                MachineName = "WTD-A", HostName = "saved-host.local",
                ObservedAt = DateTimeOffset.Now, NetworkState = "ONLINE", SourceState = "READABLE"
            };
            view.Update(report, new PresenceSendResult { HttpStatus = 200, Accepted = true });
            Assert(Convert.ToString(grid.Rows[0].Cells["NetworkState"].Value) == "在线",
                "saved mapping initially displays online");
            grid.Rows[0].Cells["MachineName"].Value = "WTD-B";
            view.Update(report, new PresenceSendResult { HttpStatus = 200, Accepted = true });
            Assert(Convert.ToString(grid.Rows[0].Cells["NetworkState"].Value) != "在线",
                "renamed unsaved mapping cannot retain old online result");
        }
    }

    private static void PresenceTimesUseLocalClock()
    {
        DateTimeOffset observed = new DateTimeOffset(2026, 10, 20, 1, 2, 3, TimeSpan.Zero);
        DateTimeOffset changed = observed.AddMinutes(-5);
        using (DevicePresenceView view = new DevicePresenceView(new DeviceHosts(), delegate(DeviceHosts ignored) { }))
        {
            view.Update(new PresenceReport {
                MachineName = "WTD-2", ObservedAt = observed, LastTxtChangeAt = changed,
                NetworkState = "UNKNOWN", SourceState = "READABLE"
            }, null);
            DataGridView grid = Find<DataGridView>(view, "HostsGrid");
            Assert(Convert.ToString(grid.Rows[0].Cells["LastProbeAt"].Value) ==
                observed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "probe time uses local clock");
            Assert(Convert.ToString(grid.Rows[0].Cells["LastTxtChangeAt"].Value) ==
                changed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), "TXT time uses local clock");
        }
    }

    private static void WindowKeepsHistoryAccessibleOnSmallScreens()
    {
        WithConfig(delegate(string path)
        {
            using (CollectorForm form = new CollectorForm(path))
            {
                Assert(form.MinimumSize.Height <= 720, "window fits a 768 pixel display");
                TabControl tabs = Find<TabControl>(form, "ContentTabs");
                Assert(tabs.TabPages.Count == 2, "history and device state have separate full-height tabs");
            }
        });
    }

    private static void SelectPresenceTab(CollectorForm form)
    {
        TabControl tabs = Find<TabControl>(form, "ContentTabs");
        tabs.SelectedTab = Find<TabPage>(form, "PresenceTab");
        Application.DoEvents();
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
        finally
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try { Directory.Delete(directory, true); break; }
                catch (IOException) { if (attempt == 49) throw; Thread.Sleep(20); }
            }
        }
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
