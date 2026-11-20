using System;
using System.Windows;

namespace WorkspaceRestorer.UI
{
    /// <summary>
    /// 编辑页所有自由拖拽物体共用的移动边界。
    ///
    /// 只做“鼠标 + 抓取偏移 → 物体矩形夹取”，不碰控件、不判断落点。
    /// 边界矩形由调用方传入：
    ///   · 新图标标签、新图标卡片 → 编辑页客户区；
    ///   · 画布图标浮卡 → 当前可见的预桌面。
    ///
    /// 关键规则（2026-11-06 定稿）：
    ///   · 边界只限制物体位置，不参与“拖动是否结束”的判定；
    ///   · 鼠标在边界外、物体仍在边界内/贴边时，拖动继续；
    ///   · 贴边回扣锚点，反向立即跟手；
    ///   · 鼠标回到界内当帧 1:1 接住，不再做 48 DIP/帧的追赶滑行。
    /// </summary>
    internal sealed class EditorDragBounds
    {
        private readonly double objectWidth;
        private readonly double objectHeight;
        private double anchorX;
        private double anchorY;
        private double positionX;
        private double positionY;
        private bool outsideX;
        private bool outsideY;
        private bool pinnedX;
        private bool pinnedY;

        internal EditorDragBounds(double objectWidth, double objectHeight)
        {
            this.objectWidth = Math.Max(1.0, objectWidth);
            this.objectHeight = Math.Max(1.0, objectHeight);
        }

        internal double X
        {
            get { return positionX; }
        }

        internal double Y
        {
            get { return positionY; }
        }

        /// <summary>只给状态栏/边界提示用，不能用来结束拖动。</summary>
        internal bool IsPinnedX
        {
            get { return pinnedX; }
        }

        /// <summary>只给状态栏/边界提示用，不能用来结束拖动。</summary>
        internal bool IsPinnedY
        {
            get { return pinnedY; }
        }

        internal void Begin(
            double pointerX, double pointerY,
            double objectX, double objectY,
            Rect bounds)
        {
            if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
            {
                positionX = objectX;
                positionY = objectY;
                anchorX = objectX - pointerX;
                anchorY = objectY - pointerY;
                outsideX = false;
                outsideY = false;
                pinnedX = false;
                pinnedY = false;
                return;
            }
            positionX = Clamp(objectX, bounds.Left, MaxX(bounds));
            positionY = Clamp(objectY, bounds.Top, MaxY(bounds));
            anchorX = positionX - pointerX;
            anchorY = positionY - pointerY;
            outsideX = false;
            outsideY = false;
            pinnedX = false;
            pinnedY = false;
        }

        internal void MoveTo(double pointerX, double pointerY, Rect bounds)
        {
            if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
            {
                return;
            }
            MoveAxis(pointerX, bounds.Left, bounds.Right, objectWidth,
                ref anchorX, ref outsideX, ref positionX, ref pinnedX);
            MoveAxis(pointerY, bounds.Top, bounds.Bottom, objectHeight,
                ref anchorY, ref outsideY, ref positionY, ref pinnedY);
        }

        internal static void ClampTo(
            Rect bounds, double width, double height,
            ref double x, ref double y)
        {
            if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
            {
                return;
            }
            double maxX = Math.Max(bounds.Left, bounds.Right - Math.Max(1.0, width));
            double maxY = Math.Max(bounds.Top, bounds.Bottom - Math.Max(1.0, height));
            x = Clamp(x, bounds.Left, maxX);
            y = Clamp(y, bounds.Top, maxY);
        }

        private double MaxX(Rect bounds)
        {
            return Math.Max(bounds.Left, bounds.Right - objectWidth);
        }

        private double MaxY(Rect bounds)
        {
            return Math.Max(bounds.Top, bounds.Bottom - objectHeight);
        }

        private static void MoveAxis(
            double pointer, double minimum, double maximum, double objectSize,
            ref double anchor, ref bool outside, ref double position, ref bool pinned)
        {
            double maxPosition = Math.Max(minimum, maximum - objectSize);
            if (pointer < minimum || pointer > maximum)
            {
                // 鼠标在边界外：物体钉在最近边界，锚点不回扣。
                // 这只是位置子状态，调用方的拖动状态必须继续。
                position = pointer < minimum ? minimum : maxPosition;
                outside = true;
                pinned = true;
                return;
            }
            if (outside)
            {
                // 从界外回来：当帧重新接住，不做 48 DIP/帧的追赶。
                outside = false;
            }
            // 跟手 = 鼠标 + 抓取偏移；边界只夹“物体矩形”，不夹鼠标点。
            double raw = pointer + anchor;
            double desired = Clamp(raw, minimum, maxPosition);
            // 被边界截掉的位移从锚点扣掉，贴边反向立即跟手。
            double correction = desired - raw;
            anchor += correction;
            position = desired;
            pinned = Math.Abs(correction) > 0.0001;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }
            if (value > maximum)
            {
                return maximum;
            }
            return value;
        }
    }
}
