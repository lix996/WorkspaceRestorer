using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using WorkspaceRestorer.Core.Localization;
using WorkspaceRestorer.Infrastructure;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 自动发现 EXE 里嵌入的语言目录。
    ///
    /// 目录约定：Resources/{tag}/_language.json
    /// 新增语言只增加这个目录和模块 JSON；Build.ps1 自动嵌入，
    /// 这里自动发现，设置页自动列出，不需要改 C#。
    /// </summary>
    internal static class LanguageCatalog
    {
        private const string ResourcePrefix = ProductIdentity.ResourcePrefix;
        private const string MetadataFileName = "_language.json";
        private static List<LanguageDefinition> languages;

        internal static IList<LanguageDefinition> All()
        {
            EnsureLoaded();
            return languages.AsReadOnly();
        }

        internal static IList<LanguageDefinition> Ready()
        {
            EnsureLoaded();
            List<LanguageDefinition> ready = new List<LanguageDefinition>();
            for (int index = 0; index < languages.Count; index++)
            {
                if (languages[index].UiReady)
                {
                    ready.Add(languages[index]);
                }
            }
            return ready.AsReadOnly();
        }

        internal static LanguageDefinition Default
        {
            get
            {
                EnsureLoaded();
                for (int index = 0; index < languages.Count; index++)
                {
                    if (LanguageTags.Same(languages[index].Tag, LanguageTags.Default))
                    {
                        return languages[index];
                    }
                }
                return languages.Count > 0
                    ? languages[0]
                    : new LanguageDefinition(
                        LanguageTags.Default, "中文（简体）", "Chinese (Simplified)",
                        LanguageTags.Default, true, 0, "none");
            }
        }

        internal static LanguageDefinition Find(string tag)
        {
            EnsureLoaded();
            string normalized = LanguageTags.Normalize(tag);
            for (int index = 0; index < languages.Count; index++)
            {
                if (LanguageTags.Same(languages[index].Tag, normalized))
                {
                    return languages[index];
                }
            }
            return null;
        }

        internal static bool IsSupportedReady(string tag)
        {
            LanguageDefinition definition = Find(tag);
            return definition != null && definition.UiReady;
        }

        private static void EnsureLoaded()
        {
            if (languages != null)
            {
                return;
            }
            List<LanguageDefinition> found = new List<LanguageDefinition>();
            Assembly assembly = typeof(LanguageCatalog).Assembly;
            string[] names = assembly.GetManifestResourceNames();
            for (int index = 0; index < names.Length; index++)
            {
                string name = names[index];
                if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                    || !name.EndsWith("." + MetadataFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string tag = name.Substring(ResourcePrefix.Length);
                int separator = tag.IndexOf('.');
                if (separator <= 0)
                {
                    continue;
                }
                tag = tag.Substring(0, separator);
                LanguageDefinition definition = ReadMetadata(assembly, name, tag);
                if (definition != null)
                {
                    found.Add(definition);
                }
            }

            // 没有元数据时仍然保证默认语言可用，避免资源缺失导致程序启动崩溃。
            bool hasDefault = false;
            for (int index = 0; index < found.Count; index++)
            {
                if (LanguageTags.Same(found[index].Tag, LanguageTags.Default))
                {
                    hasDefault = true;
                    break;
                }
            }
            if (!hasDefault)
            {
                found.Add(new LanguageDefinition(
                    LanguageTags.Default, "中文（简体）", "Chinese (Simplified)",
                    LanguageTags.Default, true, 0, "none"));
            }

            found.Sort(delegate(LanguageDefinition left, LanguageDefinition right)
            {
                int order = left.Order.CompareTo(right.Order);
                if (order != 0)
                {
                    return order;
                }
                return string.Compare(left.Tag, right.Tag, StringComparison.OrdinalIgnoreCase);
            });
            languages = found;
        }

        private static LanguageDefinition ReadMetadata(Assembly assembly, string resourceName, string tag)
        {
            try
            {
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        return null;
                    }
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        string json = reader.ReadToEnd();
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        LanguageMetadata metadata = serializer.Deserialize<LanguageMetadata>(json);
                        if (metadata == null)
                        {
                            return null;
                        }
                        return new LanguageDefinition(
                            string.IsNullOrWhiteSpace(metadata.Tag) ? tag : metadata.Tag,
                            metadata.NativeName,
                            metadata.EnglishName,
                            metadata.Fallback,
                            metadata.UiReady,
                            metadata.Order,
                            metadata.Plural);
                    }
                }
            }
            catch (Exception exception)
            {
                try
                {
                    WorkspaceRestorer.Infrastructure.AppLog.Write(
                        "[本地化] 读取语言元数据失败：" + resourceName, exception);
                }
                catch (Exception)
                {
                }
                return null;
            }
        }

        private sealed class LanguageMetadata
        {
            public string Tag { get; set; }
            public string NativeName { get; set; }
            public string EnglishName { get; set; }
            public string Fallback { get; set; }
            public bool UiReady { get; set; }
            public int Order { get; set; }
            public string Plural { get; set; }
        }
    }
}
