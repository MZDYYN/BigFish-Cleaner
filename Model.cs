using System;

namespace CleanC
{
    public enum Risk { Safe, Careful, Forbidden }

    public class CleanItem
    {
        public string Name { get; set; }
        public string RulePath { get; set; }     // rules.json 里的原始路径（含环境变量）
        public string Path { get; set; }         // 展开后显示用
        public string Note { get; set; }
        public long SizeBytes { get; set; }
        public int FileCount { get; set; }
        public bool Exists { get; set; }
        public bool FullStats { get; set; } = true;
        public int OlderThanDays { get; set; }   // >0 时只统计/清理这么多天前的文件
        public bool UseAccessTime { get; set; }  // true=按最后访问时间判断   // 是否做了完整统计（禁止项只查存在性）
        public DateTime? LastWrite { get; set; }
        public string DateText { get; set; } = "—";
        public Risk Risk { get; set; }
        public bool IsChecked { get; set; }
        public bool Manual { get; set; }
        public bool Visible { get; set; } = true;

        public string SizeText => !Exists ? "未找到"
            : !FullStats ? "未统计"
            : SizeBytes >= 1024L * 1024 * 1024
                ? $"{SizeBytes / 1024.0 / 1024 / 1024:F2} GB"
                : $"{SizeBytes / 1024.0 / 1024:F1} MB";
    }

    public class CleanGroup
    {
        public string Title { get; set; }
        public string Summary { get; set; }
        public System.Collections.Generic.List<CleanItem> Items { get; } = new System.Collections.Generic.List<CleanItem>();
    }
}
