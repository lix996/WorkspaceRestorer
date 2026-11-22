using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WorkspaceRestorer.Core;
using WorkspaceRestorer.Core.Localization;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services.Updates;
using WorkspaceRestorer.UI;
using WorkspaceRestorer.UI.Shared;

namespace WorkspaceRestorer.Tests
{
    internal static partial class TestRunner
    {
        private static void RunLocalizationTests()
        {
            Run("本地化：中英文 key、版本/关于文案和缺失 key", delegate
            {
                AppText.Initialize(LanguageTags.EnUs);
                AssertEqual("Language", AppText.T(SettingsTextKeys.LanguageLabel));
                AssertEqual("Theme", AppText.T(SettingsTextKeys.ThemeLabel));
                AssertEqual("Preferences", AppText.T(SettingsTextKeys.NavPreferences));
                AssertEqual("About", AppText.T(AboutTextKeys.Title));
                AssertEqual("v" + AppInfo.Version, AppText.F(AboutTextKeys.VersionFormat, AppInfo.Version));
                AssertEqual("Not configured", AppText.T(AboutTextKeys.NotConfigured));
                AssertEqual("Opened your default mail app.", AppText.T(AboutTextKeys.EmailOpened));
                AssertEqual(0, AppText.MissingKeys.Count);
                AssertEqual("en-US", AppText.CurrentLanguage.Tag);
                Assert(LanguageCatalog.All().Count >= 2, "至少应发现中文和英文两个语言目录。");
                Assert(LanguageCatalog.Find(LanguageTags.EnUs) != null, "英文语言目录没有被自动发现。");
                AssertEqual("zh-CN", LanguageCatalog.Find("en-US").FallbackTag);
                Assert(LanguageCatalog.Default.Tag == "zh-CN", "默认语言必须是中文。");

                HelpContent languageHelp = TooltipResolver.Resolve(GeneralSettingsToolTips.LanguageRow);
                AssertEqual("Language", languageHelp.Title);
                Assert(languageHelp.Detail.IndexOf("interface", StringComparison.OrdinalIgnoreCase) >= 0,
                    "通用页提示必须跟随当前语言解析。");

                HelpDefinition[] settingsHelp = new HelpDefinition[]
                {
                    SettingsToolTips.StartupAutoRestoreRow,
                    SettingsToolTips.ScanTerminalsRow,
                    SettingsToolTips.LoadGateRow,
                    SettingsToolTips.StartupInitialDelayRow,
                    SettingsToolTips.StartupItemDelayRow,
                    SettingsToolTips.StartupWebDelayRow,
                    SettingsToolTips.ApplicationsAutoRestoreRow,
                    SettingsToolTips.ApplicationsWaitForLoadRow,
                    SettingsToolTips.ApplicationsInitialDelayRow,
                    SettingsToolTips.ApplicationsItemDelayRow,
                    SettingsToolTips.ApplicationsWebDelayRow,
                    SettingsToolTips.LayoutAutoRestoreRow,
                    SettingsToolTips.LayoutInitialDelayRow,
                    SettingsToolTips.GeneralScrollSpeedRow,
                    SettingsToolTips.AutoStartRow,
                    SettingsToolTips.StartupPriorityRow,
                    SettingsToolTips.ResetAllRow,
                    SettingsToolTips.CpuLimitRow,
                    SettingsToolTips.CpuWaitRow,
                    SettingsToolTips.MemoryLimitRow,
                    SettingsToolTips.MemoryWaitRow
                };
                for (int index = 0; index < settingsHelp.Length; index++)
                {
                    HelpContent help = TooltipResolver.Resolve(settingsHelp[index]);
                    Assert(help != null && !string.IsNullOrWhiteSpace(help.Title),
                        "设置行提示标题没有解析出来，index=" + index);
                    Assert(!string.IsNullOrWhiteSpace(help.Detail),
                        "设置行提示详情没有解析出来，index=" + index);
                }
                TooltipDefinition[] settingsTips = new TooltipDefinition[]
                {
                    SettingsToolTips.ApplicationsAutoRestore,
                    SettingsToolTips.ApplicationsWaitForLoad,
                    SettingsToolTips.LayoutAutoRestore,
                    SettingsToolTips.LayoutInitialDelay,
                    SettingsToolTips.CpuLimit,
                    SettingsToolTips.CpuWait,
                    SettingsToolTips.MemoryLimit,
                    SettingsToolTips.MemoryWait,
                    SettingsToolTips.StartupInitialDelay,
                    SettingsToolTips.StartupItemDelay,
                    SettingsToolTips.StartupWebDelay,
                    SettingsToolTips.ApplicationsInitialDelay,
                    SettingsToolTips.ApplicationsItemDelay,
                    SettingsToolTips.ApplicationsWebDelay,
                    SettingsToolTips.ProfileName,
                    SettingsToolTips.AutoStart,
                    SettingsToolTips.StartupAutoRestore,
                    SettingsToolTips.LoadGate,
                    SettingsToolTips.ScanTerminals,
                    SettingsToolTips.ApplicationsAutoStart,
                    SettingsToolTips.DragScrollSpeedValue,
                    SettingsToolTips.DragScrollSpeedSlider,
                    SettingsToolTips.ResetAll,
                    SettingsToolTips.StartupPriority
                };
                for (int index = 0; index < settingsTips.Length; index++)
                {
                    ToolTipContent tip = TooltipResolver.Resolve(settingsTips[index]);
                    Assert(tip != null && !string.IsNullOrWhiteSpace(tip.Description),
                        "设置控件提示没有解析出来，index=" + index);
                    if (tip.SetAccessibleName)
                    {
                        Assert(!string.IsNullOrWhiteSpace(tip.Title),
                            "设置控件提示标题没有解析出来，index=" + index);
                    }
                }

                AssertEqual("Copy the current profile or icon layout",
                    ShortcutCatalog.All()[0].Title);
                AssertEqual("Drag to reorder directly",
                    ShortcutCatalog.All()[4].Title);
                AssertEqual("Drag", AppText.T("settings.shortcuts.drag.word"));

                ToolTipContent startupNavigation = TooltipResolver.Resolve(ShellToolTips.StartupNavigation);
                AssertEqual("Startup & Restore", startupNavigation.Title);
                ToolTipContent savingOverlay = TooltipResolver.Resolve(ShellToolTips.SavingOverlay);
                Assert(savingOverlay.Description.IndexOf("minimized", StringComparison.OrdinalIgnoreCase) >= 0,
                    "外壳提示正文没有跟随当前语言解析。");
                AssertEqual(0, AppText.MissingKeys.Count);
                AssertEqual("Restore work profile after launch",
                    TooltipResolver.Resolve(SettingsToolTips.StartupAutoRestoreRow).Title);
            });

            Run("本地化：回收站页面、状态和提示 key", delegate
            {
                AppText.Initialize(LanguageTags.EnUs);
                AssertEqual("Recycle Bin", AppText.T(RecycleBinTextKeys.PageTitle));
                AssertEqual("Keep for", AppText.T(RecycleBinTextKeys.RetentionLabel));
                AssertEqual("Recycle bin item", AppText.T(RecycleBinTextKeys.RowUntitled));
                AssertEqual("Permanently deleted in 3 days",
                    AppText.F(RecycleBinTextKeys.RowRemainingFormat, 3));
                HelpContent retention = TooltipResolver.Resolve(RecycleBinToolTips.RetentionHelp);
                AssertEqual("Recycle bin retention", retention.Title);
                ToolTipContent selectAll = TooltipResolver.Resolve(RecycleBinToolTips.SelectAll(true));
                AssertEqual("Select all", selectAll.Title);
                Assert(selectAll.Description.IndexOf("three sections", StringComparison.OrdinalIgnoreCase) >= 0,
                    "回收站全选提示没有跟随当前语言解析。");
                AssertEqual(0, AppText.MissingKeys.Count);

                AppText.Initialize(LanguageTags.ZhCn);
                AssertEqual("回收站", AppText.T(RecycleBinTextKeys.PageTitle));
                AssertEqual("3 天后彻底删除",
                    AppText.F(RecycleBinTextKeys.RowRemainingFormat, 3));
            });

            Run("本地化：自启动应用页面基础文案 key", delegate
            {
                AppText.Initialize(LanguageTags.EnUs);
                AssertEqual("Startup Apps", AppText.T(ApplicationsTextKeys.PageTitle));
                AssertEqual("Scan Desktop", AppText.T(ApplicationsTextKeys.ScanDesktop));
                AssertEqual("Startup Plan", AppText.T(ApplicationsTextKeys.PlanNameFallback));
                AssertEqual("No startup plans yet",
                    AppText.T(ApplicationsTextKeys.PageEmptyHeading));
                Assert(
                    AppText.T(ApplicationsTextKeys.PageEmptyHint).Contains("Settings"),
                    "英文空状态说明应指向 Settings。");
                AssertEqual(0, AppText.MissingKeys.Count);
                AppText.Initialize(LanguageTags.ZhCn);
                AssertEqual("自启动应用", AppText.T(ApplicationsTextKeys.PageTitle));
                AssertEqual("还没有自启动方案",
                    AppText.T(ApplicationsTextKeys.PageEmptyHeading));
                Assert(
                    AppText.T(ApplicationsTextKeys.PageEmptyHint).Contains("设置"),
                    "中文空状态说明应指向设置。");
            });

            Run("本地化：公共对话框、搜索和文本菜单 key", delegate
            {
                AppText.Initialize(LanguageTags.EnUs);
                AssertEqual("Confirm", AppText.T(ShellTextKeys.DialogConfirmTitle));
                AssertEqual("Yes", AppText.T(ShellTextKeys.DialogYes));
                AssertEqual("Import", AppText.T(ShellTextKeys.DialogIntakeImport));
                AssertEqual("Not now", AppText.T(ShellTextKeys.DialogIntakeDefer));
                AssertEqual("Move to Unrecognized", AppText.T(ShellTextKeys.DialogIntakeQuarantine));
                AssertEqual("Search Profiles", AppText.T(ShellTextKeys.SearchDialogSearch));
                AssertEqual("Copy", AppText.T(ShellTextKeys.TextMenuCopy));
                AssertEqual("Shortcut feedback", AppText.T(ShellTextKeys.ShortcutFeedbackName));
                AssertEqual(0, AppText.MissingKeys.Count);

                AppText.Initialize(LanguageTags.ZhCn);
                AssertEqual("请确认", AppText.T(ShellTextKeys.DialogConfirmTitle));
                AssertEqual("导入", AppText.T(ShellTextKeys.DialogIntakeImport));
                AssertEqual("暂不导入", AppText.T(ShellTextKeys.DialogIntakeDefer));
                AssertEqual("移到“无法识别”", AppText.T(ShellTextKeys.DialogIntakeQuarantine));
                AssertEqual("搜索方案", AppText.T(ShellTextKeys.SearchDialogSearch));
                AssertEqual("复制", AppText.T(ShellTextKeys.TextMenuCopy));
            });

            Run("本地化：一个功能一个目录、key 不重复、页面绑定即时切换", delegate
            {
                Assembly assembly = typeof(AppText).Assembly;
                string[] languages = new[] { LanguageTags.ZhCn, LanguageTags.EnUs };
                string[] features = new[]
                {
                    "Common", "Settings", "Shell", "StartupRestore",
                    "Applications", "DesktopLayout", "RecycleBin", "Restore"
                };
                for (int languageIndex = 0; languageIndex < languages.Length; languageIndex++)
                {
                    string language = languages[languageIndex];
                    Assert(HasEmbeddedResource(
                        assembly,
                        "WorkspaceRestorer.Text." + language + "._language.json"),
                        "缺少语言元数据：" + language);
                    for (int featureIndex = 0; featureIndex < features.Length; featureIndex++)
                    {
                        string feature = features[featureIndex];
                        string name = "WorkspaceRestorer.Text." + language + "."
                            + feature + "." + feature + ".json";
                        Assert(HasEmbeddedResource(assembly, name),
                            "缺少功能文案文件：" + name);
                    }
                    Assert(!HasEmbeddedResource(
                        assembly,
                        "WorkspaceRestorer.Text." + language + ".settings.json"),
                        "旧的扁平 settings.json 不应继续存在。");
                }

                AppText.Initialize(LanguageTags.ZhCn);
                AssertEqual(0, AppText.DuplicateKeys.Count);
                AssertEqual("通用设置", AppText.T(SettingsTextKeys.GeneralTitle));

                TextBlock title = new TextBlock();
                LocalizedText.Text(title, SettingsTextKeys.GeneralTitle);
                ComboBoxItem darkItem = new ComboBoxItem();
                LocalizedText.Content(darkItem, SettingsTextKeys.ThemeDark);
                AssertEqual("通用设置", title.Text);
                AssertEqual("深色", darkItem.Content as string);

                AppText.Initialize(LanguageTags.EnUs);
                AssertEqual("Preferences", title.Text);
                AssertEqual("Dark", darkItem.Content as string);
                AssertEqual(0, AppText.DuplicateKeys.Count);

                AppText.Initialize(LanguageTags.ZhCn);
                AssertEqual("通用设置", title.Text);
                AssertEqual("深色", darkItem.Content as string);

                Assert(AppText.TrySetLanguage(LanguageTags.EnUs),
                    "已验收的英文应能从设置页直接切换。");
                AssertEqual(LanguageTags.EnUs, AppText.CurrentLanguageTag);
                Assert(AppText.TrySetLanguage(LanguageTags.ZhCn),
                    "测试结束应能切回中文。");
                AssertEqual(LanguageTags.ZhCn, AppText.CurrentLanguageTag);
            });

            Run("时间戳：中文旧格式、英文新格式、旧名字剥离和反复追加", delegate
            {
                TimeZoneInfo china = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
                DateTime local = new DateTime(2026, 11, 14, 18, 37, 0, DateTimeKind.Unspecified);

                AssertEqual(
                    "北京时间 2026/11/14日 18:37分",
                    TimeText.FormatLocalStamp(local, china));

                AssertEqual(
                    "China Standard Time 2026/11/14 18:37",
                    TimeText.FormatLocalStamp(local, china, LanguageTags.EnUs));

                string zhName = TimeText.AppendStamp(
                    "工作方案", "工作方案", local, china, LanguageTags.ZhCn);
                AssertEqual("工作方案_北京时间 2026/11/14日 18:37分", zhName);
                AssertEqual("工作方案", TimeText.StripStamp(zhName));

                string enName = TimeText.AppendStamp(
                    "Work Profile", "Work Profile", local, china, LanguageTags.EnUs);
                AssertEqual("Work Profile_China Standard Time 2026/11/14 18:37", enName);
                AssertEqual("Work Profile", TimeText.StripStamp(enName));

                string enAgain = TimeText.AppendStamp(
                    enName, "Work Profile", local.AddMinutes(1), china, LanguageTags.EnUs);
                AssertEqual(
                    "Work Profile_China Standard Time 2026/11/14 18:38",
                    enAgain);
            });

            Run("主题：浅色/深色语义 Brush 可以来回切换", delegate
            {
                ThemePalette.Apply(false);
                System.Windows.Media.Color light = AppTheme.Surface.Color;
                ThemePalette.Apply(true);
                System.Windows.Media.Color dark = AppTheme.Surface.Color;
                Assert(light != dark, "深色主题的表面色必须和浅色不同。");
                AssertEqual("dark", ThemeModes.ToCode(ThemeModes.Parse("dark")));
                AssertEqual("system", ThemeModes.ToCode(ThemeModes.Parse("system")));
                ThemePalette.Apply(false);
            });

            Run("检查更新：已配置 GitHub，测试模式不访问网络", delegate
            {
                Assert(UpdateChecker.IsConfigured, "GitHub 地址已配置，应启用检查更新。");
                UpdateResult result = UpdateChecker.Check("1.0.0");
                AssertEqual("Suppressed", result.Outcome.ToString());
                AssertEqual("1.0.0", UpdateChecker.NormalizeVersion("v1.0.0"));
                AssertEqual("1.2.3", UpdateChecker.NormalizeVersion("v1.2.3-beta"));
                Assert(GitHubReleaseSource.IsRateLimited("0", string.Empty),
                    "X-RateLimit-Remaining=0 应识别为限流。");
                Assert(GitHubReleaseSource.IsRateLimited("57", "API rate limit exceeded"),
                    "GitHub 限流正文应识别为限流。");
                Assert(!GitHubReleaseSource.IsRateLimited("57", string.Empty),
                    "仍有配额时不能误报限流。");
            });

            Run("可选文字：TextBlock 选择、全选、清空和换文字", delegate
            {
                SelectableText text = new SelectableText { Text = "Hello world" };
                text.Select(0, 5);
                AssertEqual(5, text.SelectionLength);
                AssertEqual("Hello", text.SelectedText);

                text.SelectAllText();
                AssertEqual(11, text.SelectionLength);
                AssertEqual("Hello world", text.SelectedText);

                text.ClearSelection();
                AssertEqual(0, text.SelectionLength);

                text.Select(6, 5);
                AssertEqual("world", text.SelectedText);
                // 文字被重新赋值时，旧 TextPointer 必须自动清掉。
                text.Text = "Changed";
                AssertEqual(0, text.SelectionLength);
            });

            Run("可选文字：反向拖选保留选区", delegate
            {
                SelectableText text = new SelectableText { Text = "Hello world" };
                TextPointer anchor = text.ContentStart.GetPositionAtOffset(12, LogicalDirection.Forward);
                TextPointer end = text.ContentStart.GetPositionAtOffset(7, LogicalDirection.Forward);
                Assert(anchor != null && end != null, "反向选区必须能拿到 TextPointer。");

                MethodInfo setSelection = typeof(SelectableText).GetMethod(
                    "SetSelection",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert(setSelection != null, "SelectableText.SetSelection 必须存在。");
                setSelection.Invoke(text, new object[] { anchor, end });

                Assert(text.HasSelection, "从右往左拖出的选区必须仍被识别为有选中。");
                AssertEqual(5, text.SelectionLength);
                AssertEqual("world", text.SelectedText);
            });

            // 恢复中文，避免影响后续共用 AppText 的测试/界面。
            AppText.Initialize(LanguageTags.ZhCn);
        }

        private static bool HasEmbeddedResource(Assembly assembly, string name)
        {
            string[] names = assembly.GetManifestResourceNames();
            for (int index = 0; index < names.Length; index++)
            {
                if (string.Equals(names[index], name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static Button FindButtonByAutomationName(DependencyObject root, string name)
        {
            Button button = root as Button;
            if (button != null
                && string.Equals(
                    System.Windows.Automation.AutomationProperties.GetName(button),
                    name,
                    StringComparison.Ordinal))
            {
                return button;
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                Button found = FindButtonByAutomationName(
                    VisualTreeHelper.GetChild(root, index), name);
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private static bool ContainsText(DependencyObject root, string value)
        {
            TextBlock block = root as TextBlock;
            if (block != null && string.Equals(block.Text, value, StringComparison.Ordinal))
            {
                return true;
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                if (ContainsText(VisualTreeHelper.GetChild(root, index), value))
                {
                    return true;
                }
            }
            return false;
        }

        private static string SaveWindowScreenshot(Window window, string fileName)
        {
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string path = Path.Combine(Path.GetTempPath(), fileName);
            using (FileStream stream = File.Create(path))
            {
                encoder.Save(stream);
            }
            return path;
        }

        private static Color SampleWindowPixel(Window window, int x, int y)
        {
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }

        private static void RunSettingsUiSmokeTests()
        {
            Run("界面冒烟：设置页、通用页、快捷键页、关于页和即时主题", delegate
            {
                Application app = Application.Current;
                if (app == null)
                {
                    app = new Application();
                    ThemeService.Apply(ThemeMode.Light);
                    AppTheme.Apply(app);
                }
                ApplicationController controller = new ApplicationController();
                try
                {
                    controller.UpdateTheme("light");
                    MainWindow window = new MainWindow(controller);
                    System.Reflection.FieldInfo active = typeof(MainWindow).GetField(
                        "activeSettingsSectionId",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    System.Reflection.MethodInfo refresh = typeof(MainWindow).GetMethod(
                        "RefreshSettingsPanel",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Assert(active != null && refresh != null, "找不到设置页刷新入口。");

                    active.SetValue(window, "general-app");
                    refresh.Invoke(window, null);
                    active.SetValue(window, "shortcuts");
                    refresh.Invoke(window, null);
                    active.SetValue(window, "general-app");
                    refresh.Invoke(window, null);

                    AssertEqual("通用设置", AppText.T(SettingsTextKeys.GeneralTitle));

                    ComboBox language = typeof(MainWindow).GetField(
                        "languageBox",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .GetValue(window) as ComboBox;
                    ComboBox theme = typeof(MainWindow).GetField(
                        "themeBox",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .GetValue(window) as ComboBox;
                    Assert(language != null && theme != null, "通用页下拉框没有创建。");
                    AssertEqual(2, language.Items.Count);
                    Assert(LanguageCatalog.Find(LanguageTags.EnUs).UiReady, "英文已验收，应出现在下拉列表。");

                    // 主题必须即时生效：窗口已经显示后再切深色，内容也要变。
                    theme.SelectedIndex = 1;
                    AssertEqual("dark", controller.Settings.Theme);

                    // 生成临时截图，便于人工检查新设置页；不写入仓库。
                    window.Show();
                    window.UpdateLayout();

                    // 【2026-11-15】恢复进度条回旧版顶部位置：临时显示一次，
                    // 量它在窗口上部、宽 260，并留一张截图；随后收起，不影响后面的设置页截图。
                    ProgressBar progress = typeof(MainWindow).GetField(
                        "progressBar",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .GetValue(window) as ProgressBar;
                    Assert(progress != null, "找不到恢复进度条。");
                    progress.Visibility = Visibility.Visible;
                    window.UpdateLayout();
                    Point progressPoint = progress.TranslatePoint(new Point(0.0, 0.0), window);
                    Assert(progressPoint.Y < 120.0,
                        "恢复进度条应回到窗口上部，实际 Y=" + progressPoint.Y.ToString("0.0"));
                    Assert(Math.Abs(progress.ActualWidth - 260.0) <= 1.0,
                        "恢复进度条宽度应为 260，实际 " + progress.ActualWidth.ToString("0.0"));
                    Console.WriteLine("恢复进度条截图：" + SaveWindowScreenshot(window, "csk-progress-top.png"));
                    progress.Visibility = Visibility.Collapsed;
                    window.UpdateLayout();

                    Button settingsButton = FindButtonByAutomationName(window, "设置");
                    Assert(settingsButton != null, "找不到左下角“设置”按钮。");
                    settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Button generalButton = FindButtonByAutomationName(window, "通用");
                    Assert(generalButton != null, "找不到设置导航里的“通用”按钮。");
                    generalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();

                    // 关于固定为设置导航的最后一项，且前面有一条分隔线。
                    // 以后有人在列表中间或上面新增普通分区，这条断言会盯着顺序。
                    WrapPanel navHost = typeof(MainWindow).GetField(
                        "settingsNavHost",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        .GetValue(window) as WrapPanel;
                    Assert(navHost != null && navHost.Children.Count >= 2, "设置导航没有建出来。");
                    Button lastNav = navHost.Children[navHost.Children.Count - 1] as Button;
                    Assert(lastNav != null && AutomationProperties.GetName(lastNav) == "关于",
                        "设置导航最后一项必须是“关于”。");
                    Assert(navHost.Children[navHost.Children.Count - 2] is Border,
                        "“关于”上面必须有一条分隔线。");

                    Color darkSample = SampleWindowPixel(window, 5, 5);
                    theme.SelectedIndex = 0;
                    AssertEqual("light", controller.Settings.Theme);
                    window.UpdateLayout();
                    Color lightSample = SampleWindowPixel(window, 5, 5);
                    Assert(darkSample != lightSample, "切换浅色/深色后，窗口内容颜色必须立即变化。");
                    theme.SelectedIndex = 1;
                    window.UpdateLayout();

                    string shot = SaveWindowScreenshot(window, "csk-settings-smoke.png");
                    Console.WriteLine("截图：" + shot);
                    Button aboutButton = FindButtonByAutomationName(window, "关于");
                    Assert(aboutButton != null, "找不到设置导航里的“关于”按钮。");
                    aboutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert(FindButtonByAutomationName(window, "检查更新") != null,
                        "关于页没有显示检查更新按钮。");
                    Button githubButton = FindButtonByAutomationName(window, "打开 GitHub 项目主页");
                    Button emailButton = FindButtonByAutomationName(window, "写邮件");
                    Assert(githubButton != null, "关于页没有显示 GitHub 项目主页按钮。");
                    Assert(emailButton != null, "关于页没有显示写邮件按钮。");
                    Assert(!string.IsNullOrWhiteSpace(
                        AutomationProperties.GetHelpText(githubButton)),
                        "GitHub 项目主页按钮缺少可访问提示。");
                    Assert(!string.IsNullOrWhiteSpace(
                        AutomationProperties.GetHelpText(emailButton)),
                        "写邮件按钮缺少可访问提示。");
                    Assert(!ContainsText(window, "数据目录"),
                        "关于页不应显示数据目录。");
                    Console.WriteLine("关于页截图：" + SaveWindowScreenshot(window, "csk-about-smoke.png"));
                    Button shortcutButton = FindButtonByAutomationName(window, "快捷键");
                    Assert(shortcutButton != null, "找不到设置导航里的“快捷键”按钮。");
                    shortcutButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    string shortcutShot = SaveWindowScreenshot(window, "csk-shortcuts-smoke.png");
                    Console.WriteLine("截图：" + shortcutShot);

                    ShortcutSettingsPager pager = typeof(MainWindow).GetField(
                        "shortcutPager",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window)
                        as ShortcutSettingsPager;
                    Assert(pager != null, "快捷键页没有创建分页条。");
                    Assert(pager.Visibility == Visibility.Visible,
                        "快捷键页有内容时分页条应显示。");
                    AssertEqual(7, pager.TotalCount);
                    AssertEqual(1, pager.PageCount);
                    AssertEqual(1, pager.CurrentPage);
                    AssertEqual(10, pager.PageSize);
                    Button pagerFirst = FindPagerElement(window, "ShortcutPagerFirst") as Button;
                    Button pagerPrevious = FindPagerElement(window, "ShortcutPagerPrev") as Button;
                    Button pagerNext = FindPagerElement(window, "ShortcutPagerNext") as Button;
                    Button pagerLast = FindPagerElement(window, "ShortcutPagerLast") as Button;
                    ComboBox pagerRows = FindPagerElement(window, "ShortcutPagerRows") as ComboBox;
                    StackPanel rowsList = typeof(MainWindow).GetField(
                        "shortcutSettingsList",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as StackPanel;
                    Assert(pagerFirst != null && pagerPrevious != null
                        && pagerNext != null && pagerLast != null,
                        "快捷键页分页条的四个箭头没有全部创建。");
                    Assert(!pagerFirst.IsEnabled && !pagerPrevious.IsEnabled
                        && !pagerNext.IsEnabled && !pagerLast.IsEnabled,
                        "只有 1 页时四个箭头都应禁用。");
                    Assert(FindPagerElement(window, "ShortcutPagerPage1") != null,
                        "应显示页码 1。");
                    Assert(FindPagerElement(window, "ShortcutPagerPage2") == null,
                        "只有 7 行时不应显示页码 2。");
                    Assert(FindPagerElement(window, "ShortcutPagerPage6") == null,
                        "页码只显示当前页附近 5 个，不应出现页码 6。");
                    Assert(pagerRows != null, "快捷键页分页条没有“每页”下拉框。");
                    AssertEqual(10, Convert.ToInt32(pagerRows.SelectedItem));
                    Assert(ContainsText(window, "每页"),
                        "快捷键页分页条没有显示“每页”小字。");
                    Assert(rowsList != null && rowsList.Children.Count == 7,
                        "快捷键页应只显示 7 条真实快捷键，不应再有虚拟行。");
                    Assert(!ContainsText(window, "虚拟快捷键"),
                        "取消虚拟快捷键后不应再显示虚拟行。");

                    Grid pagerHost = typeof(MainWindow).GetField(
                        "shortcutPagerHost",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as Grid;
                    AdaptiveTwoPane settingsPane = typeof(MainWindow).GetField(
                        "settingsPane",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as AdaptiveTwoPane;
                    Assert(pagerHost != null && pagerHost.Visibility == Visibility.Visible,
                        "快捷键页分页条底栏没有显示。");
                    Assert(ReferenceEquals(pager.Parent, pagerHost),
                        "分页条没有挂到固定底栏，仍留在表格内容里。");
                    Assert(settingsPane != null, "找不到设置页两栏容器。");
                    Point hostBottom = pagerHost.TranslatePoint(
                        new Point(0.0, pagerHost.ActualHeight), window);
                    Point paneBottom = settingsPane.TranslatePoint(
                        new Point(0.0, settingsPane.ActualHeight), window);
                    Assert(Math.Abs(hostBottom.Y - paneBottom.Y) <= 2.0,
                        "分页条底栏没有贴住设置内容区底部，实际差 "
                        + (hostBottom.Y - paneBottom.Y).ToString("0.0"));
                    Assert(pagerRows.ActualHeight >= 36.0,
                        "每页下拉框高度不足，实际 " + pagerRows.ActualHeight.ToString("0.0"));
                    Assert(pagerRows.ActualWidth >= 88.0,
                        "每页下拉框宽度不足，实际 " + pagerRows.ActualWidth.ToString("0.0"));

                    ScrollViewer rowsScroll = typeof(MainWindow).GetField(
                        "shortcutTableScroll",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as ScrollViewer;
                    FrameworkElement tableHeader = typeof(MainWindow).GetField(
                        "shortcutTableHeader",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as FrameworkElement;
                    ScrollViewer settingsScroll = typeof(MainWindow).GetField(
                        "settingsContentScroll",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as ScrollViewer;
                    Assert(rowsScroll != null && tableHeader != null
                        && rowsList != null && rowsList.Children.Count > 0,
                        "快捷键页行滚动区或表头没有创建。");
                    Assert(rowsScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                        "快捷键行列表没有启用竖向滚动条。");
                    Assert(settingsScroll != null
                        && settingsScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                        "快捷键页不应再让外层设置页滚动。");

                    // 7 条真实快捷键：每页 5 条时正好分成 2 页，用来验证翻页、切片和每页设置落盘。
                    pagerRows.SelectedItem = 5;
                    window.UpdateLayout();
                    AssertEqual(5, pager.PageSize);
                    AssertEqual(5, controller.Settings.ShortcutPageSize);
                    AssertEqual(2, pager.PageCount);
                    AssertEqual(5, rowsList.Children.Count);
                    Assert(pagerNext.IsEnabled && pagerLast.IsEnabled,
                        "第 1/2 页时“下一页/最后一页”应可用。");

                    pagerNext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    AssertEqual(2, pager.CurrentPage);
                    AssertEqual(2, rowsList.Children.Count);
                    Assert(pagerFirst.IsEnabled && pagerPrevious.IsEnabled,
                        "第 2/2 页时“第一页/上一页”应可用。");

                    pagerFirst.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    AssertEqual(1, pager.CurrentPage);
                    AssertEqual(5, rowsList.Children.Count);

                    pagerRows.SelectedItem = 10;
                    window.UpdateLayout();
                    AssertEqual(10, pager.PageSize);
                    AssertEqual(10, controller.Settings.ShortcutPageSize);
                    AssertEqual(1, pager.CurrentPage);
                    AssertEqual(1, pager.PageCount);
                    AssertEqual(7, rowsList.Children.Count);

                    Button generalNav = FindButtonByAutomationName(window, "通用");
                    Assert(generalNav != null, "找不到“通用”分区导航。");
                    generalNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert(pagerHost.Visibility == Visibility.Collapsed,
                        "切到通用页后分页条底栏应折叠。");
                    Assert(settingsScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                        "切到通用页后外层设置页应恢复滚动。");
                    Button shortcutsNav = FindButtonByAutomationName(window, "快捷键");
                    Assert(shortcutsNav != null, "找不到“快捷键”分区导航。");
                    shortcutsNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    window.UpdateLayout();
                    Assert(pagerHost.Visibility == Visibility.Visible,
                        "切回快捷键页后分页条底栏应恢复。");
                    Assert(settingsScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
                        "切回快捷键页后外层设置页应停止滚动。");
                    theme.SelectedIndex = 0;
                    AssertEqual("light", controller.Settings.Theme);

                    // 即时语言：不改控制器设置，直接切换 AppText，验证已经创建的
                    // 窗口标题、问号提示和绑定控件会在同一帧内更新，而不是等重启。
                    AppText.Initialize(LanguageTags.ZhCn);
                    Button liveHelp = HelpHint.CreateHelpButton(GeneralSettingsToolTips.LanguageRow);
                    Assert(AutomationProperties.GetName(liveHelp).Contains("界面语言"),
                        "中文问号无障碍名字没有生成。");
                    AppText.Initialize(LanguageTags.EnUs);
                    AssertEqual("Workspace Restorer", window.Title);
                    Assert(AutomationProperties.GetName(liveHelp).Contains("Language"),
                        "问号无障碍名字没有跟随语言即时切换。");
                    AssertEqual("Preferences", AppText.T(SettingsTextKeys.GeneralTitle));
                    Assert(FindButtonByAutomationName(window, "Settings") != null,
                        "左栏“设置”的无障碍名字没有跟随语言即时切换。");
                    Assert(FindButtonByAutomationName(window, "Startup & Restore") != null,
                        "左栏“启动与恢复”的无障碍名字没有跟随语言即时切换。");
                    AssertEqual(0, AppText.MissingKeys.Count);
                    window.Dispatcher.Invoke(
                        new Action(delegate { }),
                        System.Windows.Threading.DispatcherPriority.Background);
                    window.UpdateLayout();
                    Console.WriteLine("英文设置截图：" + SaveWindowScreenshot(window, "csk-en-settings.png"));
                    Button appsNav = FindButtonByAutomationName(window, "Startup Apps");
                    if (appsNav != null)
                    {
                        appsNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        Console.WriteLine("英文自启动应用截图：" + SaveWindowScreenshot(window, "csk-en-applications.png"));
                    }
                    Button layoutsNav = FindButtonByAutomationName(window, "Icon Layouts");
                    if (layoutsNav != null)
                    {
                        layoutsNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        Console.WriteLine("英文图标布局截图：" + SaveWindowScreenshot(window, "csk-en-layouts.png"));
                    }
                    Button recycleNav = FindButtonByAutomationName(window, "Recycle Bin");
                    if (recycleNav != null)
                    {
                        recycleNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        Console.WriteLine("英文回收站截图：" + SaveWindowScreenshot(window, "csk-en-recycle.png"));
                    }
                    Button startupNav = FindButtonByAutomationName(window, "Startup & Restore");
                    if (startupNav != null)
                    {
                        startupNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        Console.WriteLine("英文启动与恢复截图：" + SaveWindowScreenshot(window, "csk-en-startup.png"));
                    }
                    AppText.Initialize(LanguageTags.ZhCn);
                    AssertEqual("启动恢复器", window.Title);
                    Assert(AutomationProperties.GetName(liveHelp).Contains("界面语言"),
                        "切回中文后问号无障碍名字没有恢复。");
                    Assert(FindButtonByAutomationName(window, "设置") != null,
                        "切回中文后左栏“设置”的无障碍名字没有恢复。");

                    // 【2026-11-16】恢复卡片：无修饰 Enter = 立即恢复，Esc = 取消恢复。
                    // 直接模拟卡片显示 + 倒计时，验证的是真正的窗口方法，不用等一次真恢复。
                    Border overlay = typeof(MainWindow).GetField(
                        "restoreOverlay",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as Border;
                    Assert(overlay != null, "找不到恢复卡片。");
                    FieldInfo countdownField = typeof(ApplicationController).GetField(
                        "countdownSeconds",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    FieldInfo skippedField = typeof(ApplicationController).GetField(
                        "restoreCountdownSkipped",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert(countdownField != null && skippedField != null,
                        "找不到恢复倒计时状态字段。");
                    int oldCountdown = (int)countdownField.GetValue(controller);
                    bool oldSkipped = (bool)skippedField.GetValue(controller);
                    try
                    {
                        overlay.Visibility = Visibility.Visible;
                        countdownField.SetValue(controller, 3);
                        skippedField.SetValue(controller, false);
                        PresentationSource source = PresentationSource.FromVisual(window);
                        Assert(source != null, "窗口还没有 PresentationSource，无法构造键盘事件。");
                        KeyEventArgs enter = new KeyEventArgs(
                            Keyboard.PrimaryDevice, source, 0, Key.Return);
                        enter.RoutedEvent = Keyboard.PreviewKeyDownEvent;
                        MethodInfo restoreKeyHandler = typeof(MainWindow).GetMethod(
                            "HandleRestoreCardKey",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert(restoreKeyHandler != null, "找不到恢复卡片键盘处理方法。");
                        bool handled = (bool)restoreKeyHandler.Invoke(
                            window, new object[] { enter });
                        Assert(handled, "恢复倒计时中按 Enter 应该被处理。");
                        Assert((bool)skippedField.GetValue(controller),
                            "恢复倒计时中按 Enter 应等于点“立即恢复”。");
                    }
                    finally
                    {
                        overlay.Visibility = Visibility.Collapsed;
                        countdownField.SetValue(controller, oldCountdown);
                        skippedField.SetValue(controller, oldSkipped);
                    }

                    window.Hide();
                }
                finally
                {
                    controller.Dispose();
                }
            });
        }
    }
}
