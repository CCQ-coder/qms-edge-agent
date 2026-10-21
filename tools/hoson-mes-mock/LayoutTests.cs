using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

internal static class LayoutTests
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (MockForm form = new MockForm())
            {
                try
                {
                    VerifyAutomaticDefaultStart(form);
                    Assert(form.ClientSize == new Size(1560, 940), "default client size is preserved");
                    Assert(form.MinimumSize == new Size(1000, 700), "minimum window size is preserved");
                    form.ClientSize = new Size(1560, 940);
                    form.CreateControl();
                    form.PerformLayout();
                    TableLayoutPanel layout = (TableLayoutPanel)form.Controls[0];
                    VerifyRowDistribution(form, layout, "default size");

                    form.Size = form.MinimumSize;
                    form.PerformLayout();
                    VerifyRowDistribution(form, layout, "minimum size");

                    form.ClientSize = new Size(1560, 1140);
                    form.PerformLayout();
                    VerifyRowDistribution(form, layout, "enlarged size");
                }
                finally
                {
                    HosonMesServer server = Field<HosonMesServer>(form, "server");
                    if (server != null) server.Stop();
                }
            }

            Console.WriteLine("Automatic startup and responsive window layout checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Failed: " + message);
    }

    private static void VerifyAutomaticDefaultStart(MockForm form)
    {
        TextBox ipBox = Field<TextBox>(form, "ipBox");
        TextBox portBox = Field<TextBox>(form, "portBox");
        Button startButton = Field<Button>(form, "startButton");
        Button stopButton = Field<Button>(form, "stopButton");
        Label statusLabel = Field<Label>(form, "statusLabel");

        Assert(ipBox.Text == "0.0.0.0", "automatic service listens on all IPv4 interfaces");
        Assert(portBox.Text == "4523", "automatic service uses the device default port");
        Assert(!ipBox.Enabled && !portBox.Enabled, "automatic service locks its endpoint controls (status: " + statusLabel.Text + ")");
        Assert(!startButton.Enabled && stopButton.Enabled, "automatic service exposes only stop while running");
        Assert(statusLabel.Text == "运行中", "automatic service is running when the window opens");
        HosonMesServer server = Field<HosonMesServer>(form, "server");
        Assert(server != null && server.IsRunning, "automatic service binds the default endpoint");
    }

    private static T Field<T>(MockForm form, string name) where T : class
    {
        FieldInfo field = typeof(MockForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field == null ? null : field.GetValue(form) as T;
    }

    private static void VerifyRowDistribution(MockForm form, TableLayoutPanel layout, string size)
    {
        int[] rows = layout.GetRowHeights();
        int remainingHeight = rows[1] + rows[2];
        int expectedResponseHeight = (int)Math.Round(remainingHeight * 0.52d);
        Assert(Math.Abs(rows[1] - expectedResponseHeight) <= 1, "response configuration uses 52% of remaining height at " + size);
        Assert(rows[1] > 0 && rows[2] > 0, "response configuration and log remain usable at " + size);
    }
}
