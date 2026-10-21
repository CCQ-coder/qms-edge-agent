using System;
using System.Reflection;
using System.Windows.Forms;

internal static class StartupFormTests
{
    [STAThread]
    private static int Main()
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RejectsAnIpAddressNotAssignedToThisComputer();
            WildcardBindingAdvertisesAReachableAddress();
            Console.WriteLine("KST startup form address checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static void RejectsAnIpAddressNotAssignedToThisComputer()
    {
        using (MockForm form = new MockForm())
        {
            TextBox ipBox = Field<TextBox>(form, "ipBox");
            Label statusLabel = Field<Label>(form, "statusLabel");
            KstMesServer server = Field<KstMesServer>(form, "server");
            ipBox.Text = "192.0.2.77";
            MethodInfo start = typeof(MockForm).GetMethod("StartService", BindingFlags.Instance | BindingFlags.NonPublic);
            bool started = (bool)start.Invoke(form, new object[] { false });
            Assert(!started && !server.IsRunning, "nonlocal IP must not start the service");
            Assert(statusLabel.Text.Contains("不属于本机") && statusLabel.Text.Contains("本机可用地址"),
                "startup error must identify the invalid bind address and show local alternatives");
        }
    }

    private static void WildcardBindingAdvertisesAReachableAddress()
    {
        using (MockForm form = new MockForm())
        {
            TextBox ipBox = Field<TextBox>(form, "ipBox");
            TextBox urlBox = Field<TextBox>(form, "urlBox");
            ipBox.Text = "0.0.0.0";
            string localAddress = KstListenAddress.DefaultAddress();
            if (localAddress.Length > 0)
                Assert(urlBox.Text.StartsWith("https://" + localAddress + ":4524/", StringComparison.Ordinal),
                    "wildcard binding should advertise an address a client can use");
        }
    }

    private static T Field<T>(MockForm form, string name) where T : class
    {
        FieldInfo field = typeof(MockForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new Exception("Missing field: " + name);
        return field.GetValue(form) as T;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
