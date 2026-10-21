using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;

internal static class BtfProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BtfMonitor.json");
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        if (args.Length > 0 && args[0] == "--headless") return RunHeadless(path);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new BtfMainForm(path));
        return 0;
    }

    private static int RunHeadless(string path)
    {
        try
        {
            BtfSettings settings = BtfSettings.Load(path);
            using (BtfServer server = new BtfServer(settings, delegate(BtfSnapshot snapshot) { }, Console.WriteLine))
            {
                server.Start();
                ManualResetEvent quit = new ManualResetEvent(false);
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                { eventArgs.Cancel = true; quit.Set(); };
                quit.WaitOne();
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
