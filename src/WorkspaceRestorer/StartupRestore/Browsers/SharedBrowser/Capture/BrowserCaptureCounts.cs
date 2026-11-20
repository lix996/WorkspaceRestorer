using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 纯计数：从“标签名列表 + 已读到网址的标签序号”算有多少个空白新标签页被忽略。
    /// 不碰 UIA/HWND，便于单元测试；真正的读取仍由 SessionCaptureService 完成。
    /// </summary>
    internal static class BrowserCaptureCounts
    {
        internal static int CountIgnoredBlankTabs(
            IList<string> tabNames,
            ICollection<int> readTabIndexes)
        {
            if (tabNames == null || tabNames.Count == 0)
            {
                return 0;
            }

            HashSet<int> read = new HashSet<int>();
            if (readTabIndexes != null)
            {
                foreach (int index in readTabIndexes)
                {
                    if (index >= 0)
                    {
                        read.Add(index);
                    }
                }
            }

            int count = 0;
            for (int index = 0; index < tabNames.Count; index++)
            {
                if (read.Contains(index))
                {
                    continue;
                }
                if (BrowserTitleProfile.IsBlankBrowserTabName(tabNames[index]))
                {
                    count++;
                }
            }
            return count;
        }
    }
}
