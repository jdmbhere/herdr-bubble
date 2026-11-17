using System.Drawing;
using System.Drawing.Imaging;

namespace HerdrBubble;

public static class WindowSnapshotHelper
{
    /// <summary>
    /// Captures the on-screen pixels of the given visible rectangle using the GPU-backed
    /// desktop compositor. Much faster (~4ms) than PrintWindow(PW_RENDERFULLCONTENT),
    /// which stalls the UI thread.
    /// </summary>
    public static Bitmap? CaptureWindowSnapshot(System.Windows.Rect visibleRectPx)
    {
        if (visibleRectPx.IsEmpty
            || visibleRectPx.Width < 2
            || visibleRectPx.Height < 2)
        {
            return null;
        }

        Bitmap? bitmap = null;
        try
        {
            int width = Math.Max(2, (int)Math.Round(visibleRectPx.Width));
            int height = Math.Max(2, (int)Math.Round(visibleRectPx.Height));

            bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(
                    (int)Math.Round(visibleRectPx.Left),
                    (int)Math.Round(visibleRectPx.Top),
                    0,
                    0,
                    new Size(width, height),
                    CopyPixelOperation.SourceCopy);
            }

            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            return null;
        }
    }
}
