using System.Windows;

namespace HerdrBubble;

/// <summary>
/// Pure geometry and easing helpers for the bubble-to-terminal transition overlay.
/// All rectangles are expressed in physical screen pixels.
/// </summary>
public static class WindowTransitionGeometry
{
    public static Rect ComputeBubbleOriginRect(Rect bubbleWindowRectPx, Rect targetRectPx)
    {
        double centerX = bubbleWindowRectPx.Left + bubbleWindowRectPx.Width * 0.5;
        double centerY = bubbleWindowRectPx.Top + bubbleWindowRectPx.Height * 0.5;

        double bubbleCorePx = bubbleWindowRectPx.Width > 0
            ? bubbleWindowRectPx.Width * (62.0 / 120.0)
            : 62.0;
        double baseSize = Math.Clamp(bubbleCorePx * 0.58, 22.0, 52.0);

        double aspect = (!targetRectPx.IsEmpty && targetRectPx.Width > 0 && targetRectPx.Height > 0)
            ? Math.Clamp(targetRectPx.Width / targetRectPx.Height, 0.55, 1.9)
            : 1.0;

        double width = Math.Max(18.0, aspect >= 1.0 ? baseSize : baseSize * aspect);
        double height = Math.Max(18.0, aspect >= 1.0 ? baseSize / aspect : baseSize);

        return new Rect(centerX - width * 0.5, centerY - height * 0.5, width, height);
    }

    /// <summary>
    /// Computes where a target window should be placed so it sits anchored beside the
    /// floating bubble, clamped inside the monitor work area.
    /// </summary>
    public static Rect ComputeAnchoredWindowRect(
        Rect bubbleWindowRectPx,
        Rect targetVisibleRectPx,
        Rect monitorWorkAreaPx)
    {
        double bubbleCenterX = bubbleWindowRectPx.Left + bubbleWindowRectPx.Width * 0.5;
        double bubbleCenterY = bubbleWindowRectPx.Top + bubbleWindowRectPx.Height * 0.5;

        double bubbleCorePx = bubbleWindowRectPx.Width > 0
            ? bubbleWindowRectPx.Width * (62.0 / 120.0)
            : 62.0;
        double scale = bubbleCorePx / 62.0;

        double bubbleCoreLeft = bubbleCenterX - bubbleCorePx * 0.5;
        double bubbleCoreRight = bubbleCenterX + bubbleCorePx * 0.5;

        double gap = Math.Round(14.0 * scale);
        double margin = Math.Round(12.0 * scale);

        double fallbackW = Math.Min(960.0, Math.Max(420.0, monitorWorkAreaPx.Width * 0.55));
        double fallbackH = Math.Min(640.0, Math.Max(320.0, monitorWorkAreaPx.Height * 0.6));

        double rawW = (!targetVisibleRectPx.IsEmpty && targetVisibleRectPx.Width > 0)
            ? targetVisibleRectPx.Width
            : fallbackW;
        double rawH = (!targetVisibleRectPx.IsEmpty && targetVisibleRectPx.Height > 0)
            ? targetVisibleRectPx.Height
            : fallbackH;

        double maxW = Math.Max(320.0, monitorWorkAreaPx.Width - 2.0 * margin);
        double maxH = Math.Max(220.0, monitorWorkAreaPx.Height - 2.0 * margin);

        double width = Math.Clamp(rawW, Math.Min(320.0, maxW), maxW);
        double height = Math.Clamp(rawH, Math.Min(220.0, maxH), maxH);

        bool bubbleOnRightHalf = bubbleCenterX > (monitorWorkAreaPx.Left + monitorWorkAreaPx.Width * 0.5);

        double desiredLeft = bubbleOnRightHalf
            ? bubbleCoreLeft - gap - width
            : bubbleCoreRight + gap;
        double desiredTop = bubbleCenterY - height * 0.5;

        double minLeft = monitorWorkAreaPx.Left + margin;
        double maxLeft = Math.Max(minLeft, monitorWorkAreaPx.Right - margin - width);
        double minTop = monitorWorkAreaPx.Top + margin;
        double maxTop = Math.Max(minTop, monitorWorkAreaPx.Bottom - margin - height);

        double clampedLeft = Math.Clamp(desiredLeft, minLeft, maxLeft);
        double clampedTop = Math.Clamp(desiredTop, minTop, maxTop);

        return new Rect(
            Math.Round(clampedLeft),
            Math.Round(clampedTop),
            Math.Round(width),
            Math.Round(height));
    }

    public static Rect ComputePredictedRestoreRect(
        Rect normalPosWorkspacePx,
        bool restoreToMaximized,
        Rect primaryMonitorBoundsPx,
        Rect primaryMonitorWorkAreaPx,
        Rect targetMonitorWorkAreaPx)
    {
        if (restoreToMaximized && !targetMonitorWorkAreaPx.IsEmpty)
        {
            return targetMonitorWorkAreaPx;
        }

        if (normalPosWorkspacePx.IsEmpty
            || normalPosWorkspacePx.Width <= 0
            || normalPosWorkspacePx.Height <= 0)
        {
            return targetMonitorWorkAreaPx;
        }

        double offsetX = primaryMonitorWorkAreaPx.Left - primaryMonitorBoundsPx.Left;
        double offsetY = primaryMonitorWorkAreaPx.Top - primaryMonitorBoundsPx.Top;

        return new Rect(
            normalPosWorkspacePx.Left + offsetX,
            normalPosWorkspacePx.Top + offsetY,
            Math.Max(1.0, normalPosWorkspacePx.Width),
            Math.Max(1.0, normalPosWorkspacePx.Height));
    }

    public static Rect InterpolateRect(Rect from, Rect to, double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return new Rect(
            from.Left + (to.Left - from.Left) * clamped,
            from.Top + (to.Top - from.Top) * clamped,
            Math.Max(1.0, from.Width + (to.Width - from.Width) * clamped),
            Math.Max(1.0, from.Height + (to.Height - from.Height) * clamped));
    }

    public static double EaseEmerge(double t) =>
        1.0 - Math.Pow(1.0 - Math.Clamp(t, 0.0, 1.0), 3.2);

    public static double EaseCollapse(double t) =>
        Math.Pow(Math.Clamp(t, 0.0, 1.0), 1.75);

    public static double ComputeEmergeOpacity(double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return clamped >= 0.30
            ? 1.0
            : 0.22 + 0.78 * (clamped / 0.30);
    }

    public static double ComputeCollapseOpacity(double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return clamped <= 0.55
            ? 1.0
            : Math.Clamp(1.0 - ((clamped - 0.55) / 0.45), 0.0, 1.0);
    }
}
