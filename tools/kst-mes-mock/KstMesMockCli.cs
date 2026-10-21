using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

internal static class KstMesMockCli
{
    private static int Main(string[] args)
    {
        return Run(args);
    }

    internal static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: KstMesMockCli.exe <listen IPv4> <port>");
            return 1;
        }
        try
        {
            int port;
            if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out port))
                throw new ArgumentException("Invalid port.");
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_server_config.json");
            string deviceConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_server_device_config.json");
            string mqttConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_mqtt_config.json");
            string sourceRoutesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kst_source_routes.json");
            KstMesServer server = new KstMesServer();
            KstMqttSettings mqttSettings = KstMqttSettings.Load(mqttConfigPath);
            KstSourceRoutes sourceRoutes = KstSourceRoutes.Load(sourceRoutesPath);
            sourceRoutes.ValidateDeviceMappings(mqttSettings);
            server.ConfigureSourceRoutes(sourceRoutes.TrustedProxyIp, sourceRoutes.HostToCode,
                sourceRoutes.AllowedStaleAssets);
            KstMqttForwarder mqtt = new KstMqttForwarder(mqttSettings,
                delegate(string message) { Console.WriteLine(message); }, sourceRoutes);
            server.RequestAccepted += mqtt.Enqueue;
            server.ExchangeReceived += delegate(KstExchange item)
            {
                Console.WriteLine(item.Time.ToString("yyyy-MM-dd HH:mm:ss") + " " + item.ClientIp + " " + item.Method + " HTTP " + item.Status);
            };
            server.LogWriteFailed += delegate(Exception error)
            {
                Console.Error.WriteLine("KST MES mock log write failed: " + error.Message);
            };
            using (X509Certificate2 certificate = KstTlsCertificate.LoadDefault())
            {
                mqtt.Start();
                try
                {
                    server.Start(args[0], port, KstConfig.Load(configPath), KstConfig.LoadDeviceResponses(deviceConfigPath), certificate);
                    Console.WriteLine("KST MES mock listening with TLS 1.2 on " + args[0] + ":" + port + " (six KST paths)");
                    using (ManualResetEvent stopping = new ManualResetEvent(false))
                    {
                        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stopping.Set(); };
                        stopping.WaitOne();
                    }
                }
                finally { server.Stop(); mqtt.Stop(); }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("KST MES mock failed: " + error.Message);
            return 1;
        }
    }
}
