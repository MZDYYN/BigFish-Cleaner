using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CleanC
{
    public class ScanResult
    {
        public bool Exists;
        public long Bytes;
        public int Files;
        public DateTime? Oldest;
        public DateTime? Newest;
    }

    public class CleanResult
    {
        public long Bytes;
        public int Deleted;
        public int Failed;
        public List<string> Errors = new List<string>();
    }

    public static class Engine
    {
        // 把 "C:\a\*\b" 这类通配符展开成真实目录列表
        public static List<string> Resolve(string rawPath)
        {
            var list = new List<string>();
            string p = Rules.Expand(rawPath);
            if (string.IsNullOrWhiteSpace(p)) return list;

            if (!p.Contains('*'))
            {
                if (Directory.Exists(p) || File.Exists(p)) list.Add(p);
                return list;
            }

            string dir = Path.GetDirectoryName(p);
            string pat = Path.GetFileName(p);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(pat)) return list;
            foreach (var sub in Resolve(dir))
            {
                try
                {
                    foreach (var d in Directory.GetDirectories(sub, pat)) list.Add(d);
                }
                catch { }
            }
            return list;
        }

        private static bool MatchAge(DateTime stamp, int olderThanDays)
            => olderThanDays <= 0 || (DateTime.Now - stamp).TotalDays >= olderThanDays;

        private static DateTime StampOf(FileInfo fi, bool useAccess)
            => useAccess ? fi.LastAccessTime : fi.LastWriteTime;

        // ---------- 扫描 ----------
        public static Task<ScanResult> ScanAsync(string rawPath, int olderThanDays = 0, bool useAccessTime = false)
            => Task.Run(() => Scan(rawPath, olderThanDays, useAccessTime));

        public static ScanResult Scan(string rawPath, int olderThanDays = 0, bool useAccessTime = false)
        {
            var r = new ScanResult();
            foreach (var p in Resolve(rawPath))
            {
                r.Exists = true;
                if (File.Exists(p))
                {
                    try
                    {
                        var fi = new FileInfo(p);
                        if (!MatchAge(StampOf(fi, useAccessTime), olderThanDays)) continue;
                        r.Files = 1;
                        r.Bytes = fi.Length;
                        r.Oldest = fi.LastWriteTime;
                        r.Newest = fi.LastWriteTime;
                    }
                    catch { }
                    continue;
                }

                foreach (var f in EnumerateFiles(p))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (!MatchAge(StampOf(fi, useAccessTime), olderThanDays)) continue;
                        r.Files++;
                        r.Bytes += fi.Length;
                        var t = StampOf(fi, useAccessTime);
                        if (r.Oldest == null || t < r.Oldest) r.Oldest = t;
                        if (r.Newest == null || t > r.Newest) r.Newest = t;
                    }
                    catch { }
                }
            }
            return r;
        }

        private static IEnumerable<string> EnumerateFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }
                foreach (var f in files) yield return f;

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var s in subs) stack.Push(s);
            }
        }

        // ---------- 清理 ----------
        public static Task<CleanResult> CleanAsync(string rawPath, int olderThanDays = 0, bool useAccessTime = false)
            => Task.Run(() => Clean(rawPath, olderThanDays, useAccessTime));

        public static CleanResult Clean(string rawPath, int olderThanDays = 0, bool useAccessTime = false)
        {
            var r = new CleanResult();
            foreach (var p in Resolve(rawPath))
            {
                if (File.Exists(p))
                {
                    TryDelete(p, olderThanDays, useAccessTime, r);
                    continue;
                }
                if (!Directory.Exists(p)) continue;

                foreach (var f in EnumerateFiles(p))
                    TryDelete(f, olderThanDays, useAccessTime, r);

                try
                {
                    foreach (var d in Directory.GetDirectories(p, "*", SearchOption.AllDirectories)
                                                .OrderByDescending(x => x.Length))
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(d).Any())
                                Directory.Delete(d, false);
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return r;
        }

        private static void TryDelete(string file, int olderThanDays, bool useAccessTime, CleanResult r)
        {
            try
            {
                var fi = new FileInfo(file);
                if (!MatchAge(StampOf(fi, useAccessTime), olderThanDays)) return;
                long len = fi.Length;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                r.Deleted++;
                r.Bytes += len;
            }
            catch (Exception ex)
            {
                r.Failed++;
                if (r.Errors.Count < 8) r.Errors.Add(Path.GetFileName(file) + " — " + ex.GetType().Name);
            }
        }

        // ---------- 日志 ----------
        public static string WriteLog(IEnumerable<(string name, string path, CleanResult res)> results, long totalBytes, int totalDeleted)
        {
            try
            {
                string file = Path.Combine(AppContext.BaseDirectory, $"clean-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                using var w = new StreamWriter(file, false, System.Text.Encoding.UTF8);
                w.WriteLine($"清C盘 清理日志  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine(new string('-', 60));
                foreach (var (name, path, res) in results)
                {
                    w.WriteLine($"[{name}] {path}");
                    w.WriteLine($"    删除 {res.Deleted} 个文件 / 释放 {res.Bytes / 1024.0 / 1024:F1} MB" +
                                (res.Failed > 0 ? $"，失败 {res.Failed} 个" : ""));
                    foreach (var e in res.Errors) w.WriteLine("    跳过: " + e);
                }
                w.WriteLine(new string('-', 60));
                w.WriteLine($"合计：删除 {totalDeleted} 个文件，释放 {totalBytes / 1024.0 / 1024:F1} MB");
                return file;
            }
            catch { return null; }
        }
    }
}
