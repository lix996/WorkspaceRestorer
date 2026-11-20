using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WorkspaceRestorer.Core.Cards.PlanFolders;

namespace WorkspaceRestorer.Infrastructure.PlanMirrors
{
    /// <summary>
    /// 【方案镜像 · 磁盘设施】只做“和文件系统打交道”的小工具：
    /// 读回镜像里的 Id、内容没变就不重写、删掉不再需要的旧目录。
    ///
    /// 这些动作都直接碰磁盘，所以放 Shared\Infrastructure\PlanMirrors，
    /// 不放 Shared\Core（Core 只留文件夹命名和相对路径规则）。
    /// </summary>
    internal static class PlanMirrorFile
    {
        /// <summary>
        /// 读镜像目录里的 json，返回其中的 Id；读不出来返回空字符串。
        ///
        /// 镜像文件是带 BOM 的 UTF-8；DataContractJsonSerializer 直接读 FileStream
        /// 会把 BOM 当正文，读失败后返回空 Id，目录移动就会误判成“Id 对不上”。
        /// 先按文本读（会自动去 BOM），再转成字节交给序列化器。
        /// </summary>
        public static string ReadIdAt<T>(string dataPath) where T : class, IPlanFolderNode
        {
            if (string.IsNullOrWhiteSpace(dataPath) || !File.Exists(dataPath))
            {
                return string.Empty;
            }
            try
            {
                string text = File.ReadAllText(dataPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return string.Empty;
                }

                System.Runtime.Serialization.Json.DataContractJsonSerializer serializer =
                    new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(T));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
                {
                    T value = serializer.ReadObject(stream) as T;
                    return value == null ? string.Empty : (value.Id ?? string.Empty);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>内容相同就不写；写入时带 UTF-8 BOM（和旧镜像保持逐字一致）。</summary>
        public static void WriteIfChanged(string path, string content)
        {
            try
            {
                if (File.Exists(path)
                    && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                {
                    return;
                }
                File.WriteAllText(path, content, new UTF8Encoding(true));
            }
            catch
            {
            }
        }
    }
}
