using System;
using System.IO;
using System.Windows.Forms;

internal static class GuiProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        string config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FlyingProbeCollector.ini");
        if (args.Length != 0)
        {
            if (args.Length != 2 || !string.Equals(args[0], "--config", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("用法: FlyingProbeCollector.exe [--config 配置文件路径]", "飞针机采集器");
                return 2;
            }
            config = Path.GetFullPath(args[1]);
        }
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new CollectorForm(config));
            return 0;
        }
        catch (Exception error)
        {
            MessageBox.Show("采集器无法启动: " + error.Message, "飞针机采集器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
