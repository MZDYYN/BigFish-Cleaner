using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CleanC
{
    public class RuleItem
    {
        public string group { get; set; }
        public string name { get; set; }
        public string path { get; set; }
        public string risk { get; set; }          // safe / careful / forbidden
        public bool defaultChecked { get; set; }
        public int olderThanDays { get; set; }   // >0 时只处理这么多天前的文件
        public bool useAccessTime { get; set; }  // true=按"最后访问时间"判断，false=按修改时间
        public string note { get; set; }
    }

    public class RuleGroup
    {
        public string title { get; set; }
        public string summary { get; set; }
    }

    public class RulesFile
    {
        public int version { get; set; } = 1;
        public List<RuleGroup> groups { get; set; } = new List<RuleGroup>();
        public List<RuleItem> items { get; set; } = new List<RuleItem>();
    }

    public static class Rules
    {
        public static string RulesPath =>
            Path.Combine(AppContext.BaseDirectory, "rules.json");

        public static RulesFile Load()
        {
            if (File.Exists(RulesPath))
            {
                try
                {
                    var json = File.ReadAllText(RulesPath);
                    var r = JsonSerializer.Deserialize<RulesFile>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (r != null && r.items.Count > 0) return r;
                }
                catch { /* 规则坏了就用内置的 */ }
            }
            var def = Builtin();
            try { Save(def); } catch { }
            return def;
        }

        public static void Save(RulesFile r)
        {
            var json = JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(RulesPath, json);
        }

        public static string Expand(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return p;
            var s = Environment.ExpandEnvironmentVariables(p);
            if (s.StartsWith("~"))
                s = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + s.Substring(1);
            return s;
        }

        private static RulesFile Builtin()
        {
            var r = new RulesFile();
            r.groups.Add(new RuleGroup { title = "系统缓存", summary = "临时文件、日志、错误报告 —— 删除后系统会自动重建" });
            r.groups.Add(new RuleGroup { title = "NVIDIA 驱动残留", summary = "每次装驱动都会留下安装包与着色器缓存" });
            r.groups.Add(new RuleGroup { title = "浏览器缓存", summary = "只清缓存不动登录态；IndexedDB / Local Storage 保留" });
            r.groups.Add(new RuleGroup { title = "开发工具", summary = "包管理器与解释器的下载缓存" });
            r.groups.Add(new RuleGroup { title = "聊天软件", summary = "微信/QQ 的缓存与旧附件；路径用通配符匹配账号目录" });
            r.groups.Add(new RuleGroup { title = "其它", summary = "聊天缓存与小文件" });
            r.groups.Add(new RuleGroup { title = "只显示（不要删）", summary = "这些是系统组件或你的数据，只列出来看看占多大" });

            void Add(string g, string n, string p, string risk, bool dc, string note = null, int days = 0, bool access = false)
                => r.items.Add(new RuleItem { group = g, name = n, path = p, risk = risk, defaultChecked = dc, olderThanDays = days, useAccessTime = access, note = note });

            Add("系统缓存", "Windows 临时目录", @"C:\Windows\Temp", "safe", true);
            Add("系统缓存", "用户临时目录", @"%TEMP%", "safe", true);
            Add("系统缓存", "系统日志", @"C:\Windows\Logs", "safe", true);
            Add("系统缓存", "错误报告 (WER)", @"C:\ProgramData\Microsoft\Windows\WER", "safe", true);
            Add("系统缓存", "崩溃转储 (Minidump)", @"C:\Windows\Minidump", "safe", true);
            Add("系统缓存", "Windows Update 下载缓存", @"C:\Windows\SoftwareDistribution\Download", "safe", true);
            Add("系统缓存", "内核报告", @"C:\Windows\LiveKernelReports", "safe", true);
            Add("系统缓存", "字体缓存", @"C:\Windows\ServiceProfiles\LocalService\AppData\Local\FontCache", "careful", false);
            Add("系统缓存", "Prefetch", @"C:\Windows\Prefetch", "careful", false, "删了下次开程序略慢");

            Add("NVIDIA 驱动残留", "着色器缓存 DXCache（30 天未访问）", @"%LOCALAPPDATA%\NVIDIA\DXCache", "safe", true, null, 30, true);
            Add("NVIDIA 驱动残留", "GL 着色器缓存", @"%LOCALAPPDATA%\NVIDIA\GLCache", "safe", true);
            Add("NVIDIA 驱动残留", "安装包缓存 Installer2", @"C:\Program Files\NVIDIA Corporation\Installer2", "safe", true);
            Add("NVIDIA 驱动残留", "安装器日志与残留", @"%LOCALAPPDATA%\NVIDIA Corporation", "careful", false);
            Add("NVIDIA 驱动残留", "驱动下载缓存", @"C:\ProgramData\NVIDIA Corporation\Downloader", "safe", true);
            Add("NVIDIA 驱动残留", "解压的安装包 C:\\NVIDIA", @"C:\NVIDIA", "safe", true);

            Add("浏览器缓存", "Edge Cache", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Cache", "safe", true);
            Add("浏览器缓存", "Edge Code Cache (JS)", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Code Cache", "safe", true);
            Add("浏览器缓存", "Edge GPU 缓存", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\GPUCache", "safe", true);
            Add("浏览器缓存", "Edge Service Worker", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\Service Worker", "careful", false, "清了各站要重新缓存");

            Add("开发工具", "pip 缓存", @"%LOCALAPPDATA%\pip\Cache", "safe", true);
            Add("开发工具", "pnpm store", @"%LOCALAPPDATA%\pnpm", "careful", false, "建议用 pnpm store prune");
            Add("开发工具", "npm 缓存", @"%APPDATA%\npm-cache", "safe", true);
            Add("开发工具", "DirectX 着色器缓存", @"%LOCALAPPDATA%\D3DSCache", "safe", true);

            // 聊天软件：只删 30 天前的附件，避免删掉还在看的图
            Add("聊天软件", "微信缓存目录", @"~\Documents\xwechat_files\*\cache", "safe", true);
            Add("聊天软件", "微信 30 天前的聊天附件", @"~\Documents\xwechat_files\*\msg\attach", "careful", false, days: 30);
            Add("聊天软件", "微信 30 天前的聊天视频", @"~\Documents\xwechat_files\*\msg\video", "careful", false, days: 30);
            Add("聊天软件", "微信 30 天前的文件", @"~\Documents\xwechat_files\*\msg\file", "careful", false, days: 30);
            Add("聊天软件", "QQ 缓存", @"~\Documents\Tencent Files\*\nt_qq\nt_data\emoji", "safe", true);
            Add("聊天软件", "QQ 日志", @"~\Documents\Tencent Files\*\nt_qq\nt_data\log", "safe", true);
            Add("聊天软件", "QQ 30 天前的接收数据", @"~\Documents\Tencent Files\*\nt_qq\nt_data", "careful", false, days: 30);
            Add("聊天软件", "微信聊天数据库", @"~\Documents\xwechat_files\*\db_storage", "forbidden", false);

            Add("其它", "Discord PTB 缓存", @"%APPDATA%\discordptb\Cache", "safe", true);
            Add("其它", "缩略图缓存", @"%LOCALAPPDATA%\Microsoft\Windows\Explorer", "careful", false);
            Add("其它", "崩溃转储 CrashDumps", @"%LOCALAPPDATA%\CrashDumps", "safe", true);
            Add("其它", "内存转储 MEMORY.DMP", @"C:\Windows\MEMORY.DMP", "careful", false, "上次蓝屏的完整转储");

            Add("只显示（不要删）", "组件仓库 WinSxS", @"C:\Windows\WinSxS", "forbidden", false, "只能用 DISM 清理");
            Add("只显示（不要删）", "MSI 补丁缓存", @"C:\Windows\Installer", "forbidden", false, "删了卸载/修复会坏");
            Add("只显示（不要删）", "驱动仓库 FileRepository", @"C:\Windows\System32\DriverStore\FileRepository", "forbidden", false, "用 pnputil 删旧驱动");
            Add("只显示（不要删）", "DSH 会话日志", @"~\.dsh\sessions", "forbidden", false, "那是记忆原文");
            Add("只显示（不要删）", "Edge IndexedDB", @"%LOCALAPPDATA%\Microsoft\Edge\User Data\Default\IndexedDB", "forbidden", false, "网页登录态");
            Add("只显示（不要删）", "微信聊天文件", @"~\Documents\xwechat_files", "forbidden", false, "是数据不是缓存");
            return r;
        }
    }
}
