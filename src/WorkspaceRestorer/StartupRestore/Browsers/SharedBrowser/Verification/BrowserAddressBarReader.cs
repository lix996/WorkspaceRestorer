using System;
using System.Collections.Generic;
using System.Windows.Automation;
using WorkspaceRestorer.Infrastructure;
using WorkspaceRestorer.Services;

namespace WorkspaceRestorer.Browsers
{
    /// <summary>
    /// 只读浏览器窗口的地址栏，给单页恢复的快速就绪判断用。
    ///
    /// 不切标签、不激活窗口、不改地址栏；只找 Edit/ComboBox/Document 里
    /// 像地址栏、并且值是网址的元素。读不到返回 false，调用方回退标题。
    /// </summary>
    internal static class BrowserAddressBarReader
    {
        internal static bool TryReadUrl(IntPtr windowHandle, out string url)
        {
            url = null;
            if (windowHandle == IntPtr.Zero
                || !NativeMethods.IsWindowVisible(windowHandle))
            {
                return false;
            }

            try
            {
                AutomationElement root = AutomationElement.FromHandle(windowHandle);
                if (root == null)
                {
                    return false;
                }

                Condition candidates = new OrCondition(
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Edit),
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.ComboBox),
                    new PropertyCondition(
                        AutomationElement.ControlTypeProperty,
                        ControlType.Document));
                AutomationElementCollection descendants = root.FindAll(
                    TreeScope.Descendants,
                    candidates);
                string fallback = null;
                int inspected = 0;
                for (int index = 0; index < descendants.Count && inspected < 512; index++, inspected++)
                {
                    AutomationElement element = descendants[index];
                    if (element == null)
                    {
                        continue;
                    }

                    ControlType controlType;
                    try
                    {
                        controlType = element.Current.ControlType;
                    }
                    catch
                    {
                        continue;
                    }
                    if (controlType != ControlType.Edit
                        && controlType != ControlType.ComboBox
                        && controlType != ControlType.Document)
                    {
                        continue;
                    }

                    string value = TryReadValue(element);
                    if (string.IsNullOrWhiteSpace(value)
                        || !WebAddressTools.IsWebAddress(value))
                    {
                        continue;
                    }

                    string name = BrowserTabFinder.TryReadAutomationProperty(
                        element,
                        AutomationElement.NameProperty);
                    string automationId = BrowserTabFinder.TryReadAutomationProperty(
                        element,
                        AutomationElement.AutomationIdProperty);
                    string className = BrowserTabFinder.TryReadAutomationProperty(
                        element,
                        AutomationElement.ClassNameProperty);
                    if (LooksLikeAddressBar(name, automationId, className))
                    {
                        url = value.Trim();
                        return true;
                    }

                    // 只有 Edit/ComboBox 才能当地址栏兜底；Document 里可能是网页正文。
                    if (fallback == null
                        && (controlType == ControlType.Edit
                            || controlType == ControlType.ComboBox))
                    {
                        fallback = value.Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    url = fallback;
                    return true;
                }
            }
            catch
            {
                // UIA 读不到就回退标题，不影响恢复。
            }
            return false;
        }

        private static string TryReadValue(AutomationElement element)
        {
            try
            {
                object pattern;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    ValuePattern valuePattern = pattern as ValuePattern;
                    if (valuePattern != null)
                    {
                        return valuePattern.Current.Value;
                    }
                }

                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    TextPattern textPattern = pattern as TextPattern;
                    if (textPattern != null)
                    {
                        return textPattern.DocumentRange.GetText(4096);
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        private static bool LooksLikeAddressBar(
            string name,
            string automationId,
            string className)
        {
            string text = ((name ?? string.Empty)
                + " " + (automationId ?? string.Empty)
                + " " + (className ?? string.Empty)).ToLowerInvariant();
            return text.Contains("address")
                || text.Contains("omnibox")
                || text.Contains("location")
                || text.Contains("url")
                || text.Contains("addresseditbox")
                || text.Contains("地址")
                || text.Contains("搜索栏")
                || text.Contains("搜索框");
        }
    }
}
