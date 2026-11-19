using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CleanC
{
    public sealed partial class MainWindow : Window
    {
        private readonly List<CleanGroup> _groups = new List<CleanGroup>();
        private RulesFile _rules;
        private bool _busy;
        private static readonly DateTime Now = DateTime.Now;
        private static readonly TimeSpan OldWindow = TimeSpan.FromDays(30);

        // 每组标题上的三个数字标签，勾选变化时只改文字，不重建列表
        private HashSet<string> _pendingSelection;   // 从磁盘读到的上次勾选（等扫描完再套用）
        private readonly Dictionary<CleanGroup, (TextBlock total, TextBlock picked, TextBlock old)> _groupLabels
            = new Dictionary<CleanGroup, (TextBlock, TextBlock, TextBlock)>();

        public MainWindow()
        {
            this.InitializeComponent();
            this.SystemBackdrop = new MicaBackdrop();
            TryResize(1100, 780);
            TrySetIcon();
            ShowElevation();

            _rules = Rules.Load();
            BuildSkeleton();
            _pendingSelection = LoadSelection();
            this.Closed += (s_, e_) => SaveSelection();
            WireEvents();
            Refresh();
            _ = ScanAllAsync();
        }

        // ---------------- 基础 ----------------
        private void TryResize(int w, int h)
        {
            try
            {
                var area = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Primary);
                this.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32
                {
                    X = (area.WorkArea.Width - w) / 2,
                    Y = (area.WorkArea.Height - h) / 2,
                    Width = w,
                    Height = h
                });
            }
            catch { }
        }

        private void TrySetIcon()
        {
            try
            {
                var ico = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(ico)) this.AppWindow.SetIcon(ico);
            }
            catch { }
        }

        private void ShowElevation()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                bool admin = new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                ElevationBadge.Visibility = Visibility.Visible;
                ElevationText.Text = admin ? "以管理员身份运行" : "普通权限（部分项无法清理）";
            }
            catch { }
        }

        private void WireEvents()
        {
            OnlyOldCheck.Checked += (s, e) => Refresh();
            OnlyOldCheck.Unchecked += (s, e) => Refresh();
            ShowCarefulCheck.Checked += (s, e) => Refresh();
            ShowCarefulCheck.Unchecked += (s, e) => Refresh();
            ShowForbiddenCheck.Checked += (s, e) => Refresh();
            ShowForbiddenCheck.Unchecked += (s, e) => Refresh();
            SelectAllButton.Click += (s, e) => { SetAllSafe(true); Refresh(); };
            ScanButton.Click += async (s, e) => await ScanAllAsync();
            CleanButton.Click += async (s, e) => await CleanSelectedAsync();
            ExportButton.Click += (s, e) => ExportReport();
        }

        private void SetBusy(bool busy, string text = null)
        {
            _busy = busy;
            BusyRing.IsActive = busy;
            ScanButton.IsEnabled = !busy;
            CleanButton.IsEnabled = !busy;
            SelectAllButton.IsEnabled = !busy;
            ExportButton.IsEnabled = !busy;
            if (text != null) FooterStatus.Text = text;
        }

        // ---------------- 规则 → 骨架 ----------------
        private void BuildSkeleton()
        {
            _groups.Clear();
            foreach (var g in _rules.groups)
                _groups.Add(new CleanGroup { Title = g.title, Summary = g.summary });

            var byTitle = _groups.ToDictionary(g => g.Title, g => g);
            var fallback = _groups.FirstOrDefault();
            foreach (var it in _rules.items)
            {
                byTitle.TryGetValue(it.group ?? "", out var grp);
                if (grp == null) grp = fallback;
                if (grp == null) continue;
                grp.Items.Add(new CleanItem
                {
                    Name = it.name,
                    RulePath = it.path,
                    Path = Rules.Expand(it.path),
                    Note = it.note,
                    Risk = it.risk == "forbidden" ? Risk.Forbidden : it.risk == "careful" ? Risk.Careful : Risk.Safe,
                    IsChecked = false,
                    Exists = false,
                    OlderThanDays = it.olderThanDays,
                    UseAccessTime = it.useAccessTime
                });
            }
        }

        // ---------------- 扫描 ----------------
        private async Task ScanAllAsync()
        {
            if (_busy) return;
            SetBusy(true, "正在扫描…");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var all = _groups.SelectMany(g => g.Items).ToList();

            // 禁止项：只查存在性，不做递归统计（WinSxS 有 13 万文件，扫了也没用）
            foreach (var it in all.Where(x => x.Risk == Risk.Forbidden))
            {
                var p = Rules.Expand(it.RulePath);
                it.Exists = Directory.Exists(p) || File.Exists(p);
                it.SizeBytes = 0;
                it.FileCount = 0;
                it.FullStats = false;
                it.LastWrite = null;
                it.DateText = "—";
            }

            // 其余项：4 路并发
            var todo = all.Where(x => x.Risk != Risk.Forbidden).ToList();
            int done = 0;
            var sem = new System.Threading.SemaphoreSlim(4);
            var tasks = todo.Select(async it =>
            {
                await sem.WaitAsync();
                try
                {
                    var res = await Engine.ScanAsync(it.RulePath, it.OlderThanDays, it.UseAccessTime);
                    it.Exists = res.Exists;
                    it.SizeBytes = res.Bytes;
                    it.FileCount = res.Files;
                    it.LastWrite = res.Newest;
                    it.FullStats = true;
                    it.DateText = res.Exists && res.Oldest.HasValue
                        ? $"{res.Oldest:yyyy-MM-dd} ~ {res.Newest:MM-dd}"
                        : "—";
                }
                catch { }
                finally
                {
                    sem.Release();
                    int n = System.Threading.Interlocked.Increment(ref done);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_busy) FooterStatus.Text = $"正在扫描 {n}/{todo.Count}：{it.Name}";
                    });
                }
            }).ToList();
            await Task.WhenAll(tasks);
            sem.Dispose();

            SetBusy(false);
            Refresh();

            // 扫描完才套用"上次勾选"（此时 Exists 才准）
            if (_pendingSelection != null)
            {
                ApplySavedSelection();
                Refresh();
            }

            long total = all.Where(x => x.Exists && x.FullStats).Sum(x => x.SizeBytes);
            int exist = all.Count(x => x.Exists);
            ScanInfoText.Text = $"扫描完成：{exist}/{all.Count} 项存在，合计 {Format(total)} · 用时 {sw.Elapsed.TotalSeconds:F1}s · {DateTime.Now:HH:mm:ss}";
        }

        // ---------------- 过滤 + 渲染 ----------------
        private void Refresh()
        {
            bool onlyOld = OnlyOldCheck.IsChecked == true;
            bool showCareful = ShowCarefulCheck.IsChecked == true;
            bool showForbidden = ShowForbiddenCheck.IsChecked == true;

            foreach (var g in _groups)
            {
                foreach (var it in g.Items)
                {
                    bool v = true;
                    if (it.Risk == Risk.Careful && !showCareful) v = false;
                    if (it.Risk == Risk.Forbidden && !showForbidden) v = false;
                    it.Visible = v;
                    if (!v && it.IsChecked) { it.IsChecked = false; it.Manual = false; }
                }
            }

            foreach (var g in _groups)
            {
                foreach (var it in g.Items)
                {
                    if (it.Manual) continue;
                    if (!it.Visible) { it.IsChecked = false; continue; }
                    bool isOld = !it.LastWrite.HasValue || (Now - it.LastWrite.Value) >= OldWindow;
                    it.IsChecked = it.Risk == Risk.Safe && it.Exists && (!onlyOld || isOld);
                }
            }



            GroupsPanel.Children.Clear();
            _groupLabels.Clear();
            foreach (var g in _groups)
            {
                var visible = g.Items.Where(x => x.Visible).ToList();
                if (visible.Count == 0) continue;

                var list = new StackPanel { Spacing = 0, Margin = new Thickness(0, 4, 0, 0) };
                foreach (var it in visible) list.Children.Add(BuildRow(it, onlyOld));

                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                header.Children.Add(new TextBlock { Text = g.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });

                var totalLabel = new TextBlock
                {
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                header.Children.Add(totalLabel);

                var pickedLabel = new TextBlock
                {
                    Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["SystemFillColorSuccessBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                header.Children.Add(pickedLabel);

                var oldLabel = new TextBlock
                {
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                header.Children.Add(oldLabel);

                _groupLabels[g] = (totalLabel, pickedLabel, oldLabel);
                header.Children.Add(new TextBlock
                {
                    Text = g.Summary,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                GroupsPanel.Children.Add(new Expander
                {
                    Header = header,
                    Content = list,
                    IsExpanded = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
            }

            UpdateHeader();
            UpdateGroupLabels();
        }

        private UIElement BuildRow(CleanItem it, bool onlyOld)
        {
            var grid = new Grid
            {
                Padding = new Thickness(0, 10, 0, 10),
                ColumnSpacing = 12,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)   // 让整行都能命中
            };
            for (int i = 0; i < 5; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            bool recent = onlyOld && it.LastWrite.HasValue && (Now - it.LastWrite.Value) < OldWindow;
            bool canCheck = it.Risk != Risk.Forbidden && it.Exists;

            var cb = new CheckBox { IsChecked = it.IsChecked, IsEnabled = canCheck, MinWidth = 0 };
            cb.Checked += (s, e) => { it.IsChecked = true; it.Manual = true; UpdateHeader(); UpdateGroupLabels(); };
            cb.Unchecked += (s, e) => { it.IsChecked = false; it.Manual = true; UpdateHeader(); UpdateGroupLabels(); };
            Grid.SetColumn(cb, 0);
            grid.Children.Add(cb);

            // 整行点击：点在复选框上时交给它自己处理，避免双触发
            grid.Tapped += (s, e) =>
            {
                if (!canCheck) return;
                if (IsInside(e.OriginalSource as DependencyObject, cb)) return;
                it.IsChecked = !it.IsChecked;
                it.Manual = true;
                cb.IsChecked = it.IsChecked;
                UpdateHeader();
                UpdateGroupLabels();
            };

            // 悬停高亮，暗示整行可点
            var hover = Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var hv)
                ? hv as Brush : null;
            if (hover != null)
            {
                grid.PointerEntered += (s, e) => grid.Background = hover;
                grid.PointerExited += (s, e) => grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }

            var namePanel = new StackPanel { Spacing = 2 };
            namePanel.Children.Add(new TextBlock { Text = it.Name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });

            string sub = it.Path;
            if (it.OlderThanDays > 0) sub += it.UseAccessTime
                ? $"　·　只算 {it.OlderThanDays} 天前没访问过的（近期用过的不会列进统计）"
                : $"　·　只算 {it.OlderThanDays} 天前的文件";
            if (!it.Exists) sub = it.Path + "　·　未找到";
            else if (recent) sub = it.Path + "　·　近 30 天有改动（可手动勾选）";
            else if (!string.IsNullOrEmpty(it.Note)) sub = it.Path + "　·　" + it.Note;

            namePanel.Children.Add(new TextBlock
            {
                Text = sub,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(namePanel, 1);
            grid.Children.Add(namePanel);

            var size = new TextBlock
            {
                Text = it.SizeText,
                MinWidth = 96,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"]
            };
            Grid.SetColumn(size, 2);
            grid.Children.Add(size);

            var dates = new TextBlock
            {
                Text = it.DateText,
                MinWidth = 176,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(dates, 3);
            grid.Children.Add(dates);

            var (bg, fg, text) = it.Risk switch
            {
                Risk.Safe => (Microsoft.UI.Colors.LightGreen, Microsoft.UI.Colors.DarkGreen, "安全"),
                Risk.Careful => (Microsoft.UI.Colors.Khaki, Microsoft.UI.Colors.DarkGoldenrod, "谨慎"),
                _ => (Microsoft.UI.Colors.LightPink, Microsoft.UI.Colors.DarkRed, "禁止")
            };
            var badge = new Border
            {
                Background = new SolidColorBrush(bg),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                MinWidth = 44,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = text, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(fg) }
            };
            Grid.SetColumn(badge, 4);
            grid.Children.Add(badge);

            // 右键菜单：转到所在位置 / 删除此处的文件
            grid.RightTapped += (s, e) =>
            {
                e.Handled = true;
                var menu = new MenuFlyout();

                var open = new MenuFlyoutItem { Text = "在文件资源管理器中打开", IsEnabled = it.Exists };
                open.Click += (a, b) => OpenLocation(it);

                var del = new MenuFlyoutItem
                {
                    Text = "删除",
                    IsEnabled = it.Exists && it.Risk != Risk.Forbidden
                };
                del.Click += async (a, b) => await DeleteItemAsync(it);

                menu.Items.Add(open);
                menu.Items.Add(del);
                menu.ShowAt(grid, new FlyoutShowOptions { Position = e.GetPosition(grid) });
            };

            return grid;
        }

        // 判断某个元素是否落在某个祖先里（用于区分"点在复选框上"和"点在行的空白处"）
        private static bool IsInside(DependencyObject child, DependencyObject ancestor)
        {
            while (child != null)
            {
                if (ReferenceEquals(child, ancestor)) return true;
                child = VisualTreeHelper.GetParent(child);
            }
            return false;
        }

        // ---------------- 右键操作 ----------------
        private void OpenLocation(CleanItem it)
        {
            try
            {
                string p = Rules.Expand(it.RulePath);
                if (Directory.Exists(p))
                    Process.Start("explorer.exe", p);
                else if (File.Exists(p))
                    Process.Start("explorer.exe", "/select,\"" + p + "\"");
                else
                    FooterStatus.Text = "路径不存在：" + p;
            }
            catch (Exception ex)
            {
                FooterStatus.Text = "打开失败：" + ex.Message;
            }
        }

        private async Task DeleteItemAsync(CleanItem it)
        {
            var dlg = new ContentDialog
            {
                Title = "删除这一项？",
                Content = new TextBlock
                {
                    Text = $"将删除「{it.Name}」下的 {it.FileCount} 个文件，约 {Format(it.SizeBytes)}。\n\n{it.Path}\n\n操作不可撤销。",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            SetBusy(true, "正在删除 " + it.Name + " …");
            var r = await Engine.CleanAsync(it.RulePath, it.OlderThanDays, it.UseAccessTime);
            string log = Engine.WriteLog(new[] { (it.Name, it.Path, r) }, r.Bytes, r.Deleted);
            SetBusy(false);

            await new ContentDialog
            {
                Title = "删除完成",
                Content = new TextBlock
                {
                    Text = $"释放 {Format(r.Bytes)}，删除 {r.Deleted} 个文件" +
                           (r.Failed > 0 ? $"，{r.Failed} 个被占用跳过" : "") +
                           (string.IsNullOrEmpty(log) ? "" : "\n\n日志：" + log),
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "好",
                XamlRoot = RootGrid.XamlRoot
            }.ShowAsync();

            it.Manual = false;
            await ScanAllAsync();
        }

        private void SetAllSafe(bool value)
        {
            foreach (var g in _groups)
                foreach (var it in g.Items)
                    if (it.Risk == Risk.Safe && it.Visible && it.Exists) { it.IsChecked = value; it.Manual = true; }
        }

        // 只更新组标题里的数字（勾选变化时实时刷新，不重建列表）
        // ---------------- 记住上次勾选 ----------------
        private static string SelectionPath => Path.Combine(AppContext.BaseDirectory, "selection.json");

        private static string KeyOf(CleanGroup g, CleanItem it) => g.Title + "||" + it.Name;

        private void ApplySavedSelection()
        {
            if (_pendingSelection == null) return;
            foreach (var g in _groups)
            {
                foreach (var it in g.Items)
                {
                    if (it.Risk == Risk.Forbidden || !it.Exists) continue;
                    it.IsChecked = _pendingSelection.Contains(KeyOf(g, it));
                    it.Manual = true;   // 标为手动，避免日期规则再覆盖
                }
            }
            _pendingSelection = null;
        }

        private void SaveSelection()
        {
            try
            {
                var keys = new List<string>();
                foreach (var g in _groups)
                    foreach (var it in g.Items)
                        if (it.IsChecked && it.Visible && it.Exists && it.Risk != Risk.Forbidden)
                            keys.Add(KeyOf(g, it));
                File.WriteAllText(SelectionPath,
                    System.Text.Json.JsonSerializer.Serialize(keys, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
            }
            catch { }
        }

        private static HashSet<string> LoadSelection()
        {
            try
            {
                if (!File.Exists(SelectionPath)) return null;
                var json = File.ReadAllText(SelectionPath);
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
                return list == null ? null : new HashSet<string>(list);
            }
            catch { return null; }
        }

        private void UpdateGroupLabels()
        {
            foreach (var g in _groups)
            {
                if (!_groupLabels.TryGetValue(g, out var labels)) continue;
                var visible = g.Items.Where(x => x.Visible).ToList();
                long total = visible.Sum(x => x.SizeBytes);
                var pickedItems = visible.Where(x => x.IsChecked).ToList();
                long pickedBytes = pickedItems.Sum(x => x.SizeBytes);
                long oldBytes = visible.Where(x => x.FullStats && x.LastWrite.HasValue && (Now - x.LastWrite.Value) >= OldWindow)
                                       .Sum(x => x.SizeBytes);

                labels.total.Text = Format(total);
                labels.picked.Text = pickedItems.Count > 0
                    ? $"已选 {pickedItems.Count} 项 · {Format(pickedBytes)}"
                    : "";
                labels.old.Text = oldBytes > 0 ? $"（30 天前 {Format(oldBytes)}）" : "";
            }
        }

        private void UpdateHeader()
        {
            var drive = DriveInfo.GetDrives().FirstOrDefault(d => d.Name.StartsWith("C:"));
            if (drive != null)
            {
                double totalGb = drive.TotalSize / 1024.0 / 1024 / 1024;
                double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
                DiskText.Text = $"已用 {totalGb - freeGb:F1} GB / 共 {totalGb:F1} GB（空闲 {freeGb:F1} GB）";
                DiskBar.Value = (totalGb - freeGb) / totalGb * 100;
            }

            long selected = 0;
            int count = 0;
            foreach (var g in _groups)
                foreach (var it in g.Items)
                    if (it.IsChecked && it.Visible && it.Exists) { selected += it.SizeBytes; count++; }

            EstimateText.Text = $"预计可释放 {Format(selected)}";

            if (!_busy)
                FooterStatus.Text = count == 0
                    ? "已选 0 项"
                    : $"已选 {count} 项 · {Format(selected)}";
        }

        // ---------------- 清理 ----------------
        private async Task CleanSelectedAsync()
        {
            if (_busy) return;

            var picked = _groups.SelectMany(g => g.Items).Where(x => x.IsChecked && x.Visible && x.Exists).ToList();
            if (picked.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "没有选中任何项",
                    Content = "请先勾选要清理的项目，或用「全选安全项」。",
                    CloseButtonText = "知道了",
                    XamlRoot = RootGrid.XamlRoot
                }.ShowAsync();
                return;
            }

            long total = picked.Sum(x => x.SizeBytes);

            var lines = new List<string>();
            foreach (var g in _groups)
            {
                var items = g.Items.Where(x => x.IsChecked && x.Visible && x.Exists).ToList();
                if (items.Count > 0)
                    lines.Add($"· {g.Title}：{items.Count} 项 · {Format(items.Sum(x => x.SizeBytes))}");
            }

            var body = new StackPanel { Spacing = 10, MaxWidth = 460 };
            body.Children.Add(new TextBlock
            {
                Text = $"将清理 {picked.Count} 项，合计 {Format(total)}",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            body.Children.Add(new TextBlock { Text = string.Join("\n", lines), TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
            body.Children.Add(new TextBlock
            {
                Text = "这些是缓存/日志文件，清理后会自动重建；操作不可撤销。",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });

            var dlg = new ContentDialog
            {
                Title = "确认清理？",
                Content = body,
                PrimaryButtonText = "开始清理",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            SetBusy(true, "正在清理…");
            long freed = 0;
            int deleted = 0, failed = 0;
            var results = new List<(string, string, CleanResult)>();

            int i = 0;
            foreach (var it in picked)
            {
                i++;
                FooterStatus.Text = $"正在清理 {i}/{picked.Count}：{it.Name}";
                var r = await Engine.CleanAsync(it.RulePath, it.OlderThanDays, it.UseAccessTime);
                freed += r.Bytes;
                deleted += r.Deleted;
                failed += r.Failed;
                results.Add((it.Name, it.Path, r));
            }

            string log = Engine.WriteLog(results, freed, deleted);
            SetBusy(false);

            var done = new StackPanel { Spacing = 8, MaxWidth = 460 };
            done.Children.Add(new TextBlock
            {
                Text = $"释放 {Format(freed)}，删除 {deleted} 个文件" + (failed > 0 ? $"，{failed} 个被占用跳过" : ""),
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });
            if (!string.IsNullOrEmpty(log))
                done.Children.Add(new TextBlock
                {
                    Text = "日志：" + log,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                });

            await new ContentDialog
            {
                Title = "清理完成",
                Content = done,
                CloseButtonText = "好",
                XamlRoot = RootGrid.XamlRoot
            }.ShowAsync();

            foreach (var it in picked) { it.Manual = false; }
            await ScanAllAsync();
        }

        // ---------------- 导出 ----------------
        private void ExportReport()
        {
            try
            {
                string file = Path.Combine(AppContext.BaseDirectory, $"scan-report-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                using var w = new StreamWriter(file, false, System.Text.Encoding.UTF8);
                w.WriteLine("分类,名称,路径,风险,存在,文件数,大小MB,最早,最新");
                foreach (var g in _groups)
                    foreach (var it in g.Items)
                        w.WriteLine($"{g.Title},{it.Name},{it.Path},{it.Risk},{it.Exists},{it.FileCount}," +
                                    $"{it.SizeBytes / 1024.0 / 1024:F1},{it.DateText}");
                ScanInfoText.Text = "报告已导出：" + file;
            }
            catch (Exception ex)
            {
                ScanInfoText.Text = "导出失败：" + ex.Message;
            }
        }

        private static string Format(long bytes)
        {
            if (bytes <= 0) return "0.0 MB";
            return bytes >= 1024L * 1024 * 1024
                ? $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
                : $"{bytes / 1024.0 / 1024:F1} MB";
        }
    }
}
