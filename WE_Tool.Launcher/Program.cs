using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var appExe = Path.Combine(appDir, "app", "WE_Tool.exe");

        if (!File.Exists(appExe))
        {
            MessageBox.Show(
                $"Application not found:\n{appExe}",
                "WE Tool",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Process.Start(new ProcessStartInfo(appExe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(appExe)
        });
    }
}
