using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using WorkspaceRestorer.Core.Localization;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 全程序唯一的界面文案查表器。
    ///
    /// 文案放在 Shared/UI/Text/Localization/Resources/{语言}/{功能}/{功能}.json，
    /// 由 Build.ps1 作为嵌入资源打进 EXE；页面只调用 T/F/Count，不写用户可见中文。
    ///
    /// 语言由 LanguageCatalog 自动发现，不写死 zh-CN / en-US；
    /// 新增语言只需要增加资源目录和 _language.json。
    ///
    /// 【即时切换】每次切换语言都生成一份不可变快照，最后一次性替换引用；
    /// 已经打开的界面通过 LocalizedText / 控件订阅 LanguageChanged 刷新，
    /// 不重建主窗口，也不要求重启程序。
    /// </summary>
    internal static class AppText
    {
        private const string ResourcePrefix = ProductIdentity.ResourcePrefix;
        private static readonly object InitializeLock = new object();
        private static TextSnapshot snapshot;

        /// <summary>语言真正切换完成后触发；同一语言重复初始化不触发。</summary>
        internal static event EventHandler LanguageChanged;

        private sealed class TextSnapshot
        {
            public LanguageDefinition Language;
            public Dictionary<string, string> Current;
            public Dictionary<string, string> ChineseFallback;
            public Dictionary<string, string> LanguageFallback;
            public List<string> Missing;
            public List<string> Duplicates;
            public readonly object MissingLock = new object();
        }

        internal static LanguageDefinition CurrentLanguage
        {
            get
            {
                EnsureInitialized();
                return Volatile.Read(ref snapshot).Language;
            }
        }

        internal static string CurrentLanguageTag
        {
            get { return CurrentLanguage.Tag; }
        }

        internal static IList<string> MissingKeys
        {
            get
            {
                EnsureInitialized();
                TextSnapshot value = Volatile.Read(ref snapshot);
                lock (value.MissingLock)
                {
                    return value.Missing.AsReadOnly();
                }
            }
        }

        /// <summary>同一个语言目录里重复出现的 key；测试要求为空。</summary>
        internal static IList<string> DuplicateKeys
        {
            get
            {
                EnsureInitialized();
                return Volatile.Read(ref snapshot).Duplicates.AsReadOnly();
            }
        }

        /// <summary>
        /// 载入指定语言。这个方法允许载入 uiReady=false 的语言，供翻译预览和测试使用；
        /// 用户从设置页切换时必须走 <see cref="TrySetLanguage"/>。
        /// </summary>
        internal static void Initialize(string languageTag)
        {
            TextSnapshot previous;
            TextSnapshot next;
            bool changed;
            lock (InitializeLock)
            {
                next = BuildSnapshot(languageTag);
                previous = snapshot;
                Volatile.Write(ref snapshot, next);

                try
                {
                    Thread.CurrentThread.CurrentUICulture = LanguageTags.ToCulture(next.Language.Tag);
                }
                catch (Exception)
                {
                    // 语言资源不影响程序启动；只影响框架自带的少量文字。
                }

                changed = previous == null
                    || !LanguageTags.Same(previous.Language.Tag, next.Language.Tag);
            }

            // 先刷新所有已经登记的控件，再通知需要重排骨架的窗口。
            LocalizedText.RefreshAll();

            if (!changed)
            {
                return;
            }
            RaiseLanguageEvent(LanguageChanged, "语言切换通知失败。");
        }

        private static void RaiseLanguageEvent(EventHandler handler, string failureMessage)
        {
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(null, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                try
                {
                    WorkspaceRestorer.Infrastructure.AppLog.Write(
                        "[本地化] " + failureMessage, exception);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 用户可选的即时切换：只接受已验收（uiReady=true）的语言。
        /// 找不到或未完成时返回 false，当前语言保持不变。
        /// </summary>
        internal static bool TrySetLanguage(string languageTag)
        {
            LanguageDefinition selected = LanguageCatalog.Find(languageTag);
            if (selected == null || !selected.UiReady)
            {
                return false;
            }
            Initialize(selected.Tag);
            return true;
        }

        internal static string T(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }
            EnsureInitialized();
            TextSnapshot value = Volatile.Read(ref snapshot);
            string text;
            if (value.Current.TryGetValue(key, out text))
            {
                return text;
            }
            if (value.LanguageFallback.TryGetValue(key, out text))
            {
                RecordMissing(value, key);
                return text;
            }
            if (value.ChineseFallback.TryGetValue(key, out text))
            {
                RecordMissing(value, key);
                return text;
            }
            RecordMissing(value, key);
            return key;
        }

        internal static string F(string key, params object[] args)
        {
            string format = T(key);
            if (args == null || args.Length == 0)
            {
                return format;
            }
            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        /// <summary>
        /// 英文等“单数/复数”语言：count == 1 时优先找 key + ".one"；没有就回退 base key。
        /// 中文、日文等不需要单数的语言只提供 base key。
        /// </summary>
        internal static string Count(string key, int count, params object[] args)
        {
            EnsureInitialized();
            TextSnapshot value = Volatile.Read(ref snapshot);
            string selectedKey = key;
            if (value.Language.UsesSingularKey && count == 1)
            {
                string singular = key + ".one";
                if (value.Current.ContainsKey(singular)
                    || value.ChineseFallback.ContainsKey(singular)
                    || value.LanguageFallback.ContainsKey(singular))
                {
                    selectedKey = singular;
                }
            }
            return F(selectedKey, args);
        }

        private static void EnsureInitialized()
        {
            if (Volatile.Read(ref snapshot) != null)
            {
                return;
            }
            lock (InitializeLock)
            {
                if (snapshot == null)
                {
                    Initialize(LanguageTags.Default);
                }
            }
        }

        private static TextSnapshot BuildSnapshot(string languageTag)
        {
            LanguageDefinition selected = LanguageCatalog.Find(languageTag);
            if (selected == null)
            {
                selected = LanguageCatalog.Default;
            }

            TextSnapshot next = new TextSnapshot();
            next.Language = selected;
            next.Current = new Dictionary<string, string>(StringComparer.Ordinal);
            next.ChineseFallback = new Dictionary<string, string>(StringComparer.Ordinal);
            next.LanguageFallback = new Dictionary<string, string>(StringComparer.Ordinal);
            next.Missing = new List<string>();
            next.Duplicates = new List<string>();

            LoadLanguage(LanguageTags.ZhCn, next.ChineseFallback, next.Duplicates);
            if (LanguageTags.Same(selected.Tag, LanguageTags.ZhCn))
            {
                foreach (KeyValuePair<string, string> pair in next.ChineseFallback)
                {
                    next.Current[pair.Key] = pair.Value;
                }
            }
            else
            {
                LoadLanguage(selected.Tag, next.Current, next.Duplicates);
                if (!LanguageTags.Same(selected.FallbackTag, selected.Tag)
                    && !LanguageTags.Same(selected.FallbackTag, LanguageTags.ZhCn))
                {
                    LoadLanguage(selected.FallbackTag, next.LanguageFallback, next.Duplicates);
                }
            }
            return next;
        }

        private static void RecordMissing(TextSnapshot value, string key)
        {
            lock (value.MissingLock)
            {
                if (value.Missing.Contains(key))
                {
                    return;
                }
                value.Missing.Add(key);
            }
            try
            {
                WorkspaceRestorer.Infrastructure.AppLog.Write("[本地化] 缺少文案 key：" + key);
            }
            catch (Exception)
            {
                // 日志失败不能影响界面。
            }
        }

        private static void LoadLanguage(
            string code,
            Dictionary<string, string> target,
            List<string> duplicates)
        {
            Assembly assembly = typeof(AppText).Assembly;
            string prefix = ResourcePrefix + code + ".";
            string[] names = assembly.GetManifestResourceNames();
            Array.Sort(names, StringComparer.Ordinal);
            for (int index = 0; index < names.Length; index++)
            {
                string name = names[index];
                if (!name.StartsWith(prefix, StringComparison.Ordinal)
                    || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("._language.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    using (Stream stream = assembly.GetManifestResourceStream(name))
                    {
                        if (stream == null)
                        {
                            continue;
                        }
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                        {
                            string json = reader.ReadToEnd();
                            JavaScriptSerializer serializer = new JavaScriptSerializer();
                            serializer.MaxJsonLength = int.MaxValue;
                            Dictionary<string, string> values =
                                serializer.Deserialize<Dictionary<string, string>>(json);
                            if (values == null)
                            {
                                continue;
                            }
                            foreach (KeyValuePair<string, string> pair in values)
                            {
                                if (string.IsNullOrEmpty(pair.Key))
                                {
                                    continue;
                                }
                                if (target.ContainsKey(pair.Key))
                                {
                                    string duplicate = code + "/" + pair.Key + " (" + name + ")";
                                    if (!duplicates.Contains(duplicate))
                                    {
                                        duplicates.Add(duplicate);
                                    }
                                    continue;
                                }
                                target[pair.Key] = pair.Value ?? string.Empty;
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    try
                    {
                        WorkspaceRestorer.Infrastructure.AppLog.Write(
                            "[本地化] 读取语言资源失败：" + name, exception);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
