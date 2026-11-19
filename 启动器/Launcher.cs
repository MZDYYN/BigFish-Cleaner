using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;

namespace BigFishCleanerLauncher
{
    internal static class Program
    {
        private const string AppName = "大肥鱼C盘清理工具v0.1";
        private const string PayloadResource = "payload.zip";
        private const string MarkerFile = "payload.version";
        private const string Version = "0.1";

        [STAThread]
        private static void Main()
        {
            try
            {
                string baseDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BigFishCleaner", "v" + Version);

                string exePath = Path.Combine(baseDir, AppName + ".exe");

                if (!IsExtracted(baseDir) || !File.Exists(exePath))
                {
                    Extract(baseDir);
                    File.WriteAllText(Path.Combine(baseDir, MarkerFile), Version);
                }

                if (!File.Exists(exePath))
                {
                    MessageBox.Show("解压后没有找到程序文件：\n" + exePath, AppName,
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = baseDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动失败：\n" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool IsExtracted(string baseDir)
        {
            try
            {
                string marker = Path.Combine(baseDir, MarkerFile);
                return File.Exists(marker) && File.ReadAllText(marker).Trim() == Version;
            }
            catch { return false; }
        }

        private static void Extract(string baseDir)
        {
            if (Directory.Exists(baseDir))
            {
                try { Directory.Delete(baseDir, true); } catch { }
            }
            Directory.CreateDirectory(baseDir);

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
            {
                if (s == null) throw new InvalidOperationException("程序包损坏（找不到内嵌数据）");
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        string target = Path.GetFullPath(Path.Combine(baseDir, entry.FullName));
                        if (!target.StartsWith(Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase))
                            continue; // 防目录穿越

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        string dir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        entry.ExtractToFile(target, true);
                    }
                }
            }
        }
    }
}
