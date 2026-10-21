using System;
using System.Globalization;
using System.IO;
using System.Threading;

internal static class HosonMesMockCli
{
    private static int Main(string[] args)
    {
        return Run(args);
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: HosonMesMockCli.exe <listen IPv4> <port>");
            return 1;
        }
        try
        {
            int port;
            if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out port))
                throw new ArgumentException("Invalid port.");
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mes_server_config.json");
            string deviceConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mes_server_device_config.json");
            HosonMesServer server = new HosonMesServer();
            server.ExchangeReceived += delegate(MesExchange item)
            {
                Console.WriteLine(item.Time.ToString("yyyy-MM-dd HH:mm:ss") + " " + item.ClientIp + " " + item.Method + " HTTP " + item.Status);
            };
            server.LogWriteFailed += delegate(Exception error)
            {
                Console.Error.WriteLine("Hoson MES mock log write failed: " + error.Message);
            };
            server.Start(args[0], port, MesConfig.Load(configPath), MesConfig.LoadDeviceResponses(deviceConfigPath));
            Console.WriteLine("Hoson MES mock listening on " + args[0] + ":" + port + "/api/mes/invoke");
            using (ManualResetEvent stopping = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stopping.Set(); };
                stopping.WaitOne();
            }
            server.Stop();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Hoson MES mock failed: " + error.Message);
            return 1;
        }
    }
}
