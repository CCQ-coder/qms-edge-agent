using System;
using System.Drawing;
using System.IO;

internal static class IconTests
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Usage: IconTests <icon-file> <exe-file>");
            Assert(File.Exists(args[0]), "LYR icon asset exists");
            Assert(File.Exists(args[1]), "built GUI executable exists");

            using (Icon source = new Icon(args[0]))
            using (Icon embedded = Icon.ExtractAssociatedIcon(args[1]))
            using (Bitmap sourceBitmap = Render(source))
            using (Bitmap embeddedBitmap = embedded.ToBitmap())
            {
                Assert(sourceBitmap.GetPixel(16, 16).ToArgb() == embeddedBitmap.GetPixel(16, 16).ToArgb(), "built GUI executable embeds the LYR icon center");
                Assert(sourceBitmap.GetPixel(16, 28).ToArgb() == embeddedBitmap.GetPixel(16, 28).ToArgb(), "built GUI executable embeds the LYR icon accent");
            }

            Console.WriteLine("LYR application icon checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
            return 1;
        }
    }

    private static Bitmap Render(Icon icon)
    {
        Bitmap bitmap = new Bitmap(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.DrawIcon(icon, new Rectangle(0, 0, 32, 32));
        return bitmap;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Failed: " + message);
    }
}
