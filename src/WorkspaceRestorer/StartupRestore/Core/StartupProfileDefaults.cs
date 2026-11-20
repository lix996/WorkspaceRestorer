using System;
using System.Collections.Generic;

namespace WorkspaceRestorer.Models
{
    /// <summary>
    /// “当前工作方案”的默认选择规则（用户 2026-10-04 明确要求）。
    ///
    /// 规则只有两条，顺序不能颠倒：
    /// 1. 用户在界面上选过的方案（catalog.ActiveProfileId 有值且能在列表里找到）优先，永远按用户的来；
    /// 2. 没有任何“用户选过的方案”时，默认用**最新**的方案——也就是最后保存/修改时间
    ///    （UpdatedAtUtc）最晚的那一个，而不是列表里的第一个。
    ///
    /// 单独放一个纯函数，是为了让测试能在不启动界面的情况下验证规则，
    /// 也避免规则散落在界面代码里被改回去。
    /// </summary>
    public static class StartupProfileDefaults
    {
        /// <summary>
        /// 返回应该设为当前方案的方案 Id；列表为空时返回 null。
        /// 不会修改传入的 catalog。
        /// </summary>
        public static string ResolveActiveProfileId(StartupProfileCatalog catalog)
        {
            if (catalog == null || catalog.Profiles == null || catalog.Profiles.Count == 0)
            {
                return null;
            }

            // 规则 1：用户选过的方案优先。
            if (!string.IsNullOrWhiteSpace(catalog.ActiveProfileId))
            {
                for (int index = 0; index < catalog.Profiles.Count; index++)
                {
                    StartupProfile candidate = catalog.Profiles[index];
                    if (candidate != null
                        && string.Equals(candidate.Id, catalog.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate.Id;
                    }
                }
            }

            // 规则 2：最新保存/修改的那一个。时间完全相同时取列表中靠后的那个
            // （后出现的通常是后来新建的），保证结果稳定、可解释。
            StartupProfile newest = null;
            DateTime newestTime = DateTime.MinValue;
            for (int index = 0; index < catalog.Profiles.Count; index++)
            {
                StartupProfile candidate = catalog.Profiles[index];
                if (candidate == null)
                {
                    continue;
                }
                DateTime candidateTime = LastSavedAtUtc(candidate);
                if (newest == null || candidateTime >= newestTime)
                {
                    newest = candidate;
                    newestTime = candidateTime;
                }
            }

            return newest == null ? null : newest.Id;
        }

        /// <summary>
        /// 一份方案“最后一次保存”的时间：取方案自身的 UpdatedAtUtc 与条目里最晚的
        /// StartTimeUtc 中更晚的那个。
        /// 只取 UpdatedAtUtc 不够稳：拖动排序、勾选/取消勾选这类操作也会刷新它，
        /// 而条目里记录的启动时间来自真正的保存/追加动作。
        /// </summary>
        public static DateTime LastSavedAtUtc(StartupProfile profile)
        {
            if (profile == null)
            {
                return DateTime.MinValue;
            }
            DateTime latest = profile.UpdatedAtUtc;
            if (profile.Items != null)
            {
                for (int index = 0; index < profile.Items.Count; index++)
                {
                    SessionItem item = profile.Items[index];
                    if (item != null && item.StartTimeUtc > latest)
                    {
                        latest = item.StartTimeUtc;
                    }
                }
            }
            return latest;
        }

        /// <summary>
        /// 按规则算出并写回 catalog.ActiveProfileId；返回是否发生了改变。
        /// </summary>
        public static bool ApplyActiveProfileId(StartupProfileCatalog catalog)
        {
            string resolved = ResolveActiveProfileId(catalog);
            if (catalog == null)
            {
                return false;
            }
            if (string.Equals(catalog.ActiveProfileId, resolved, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            catalog.ActiveProfileId = resolved;
            return true;
        }
    }
}
