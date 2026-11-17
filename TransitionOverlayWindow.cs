using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Media;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;

namespace HerdrBubble;

/// <summary>
/// Non-activating, click-through, topmost overlay used to animate the terminal
/// emerging from or collapsing back into the floating bubble.
///
/// This is a WinForms <see cref="System.Windows.Forms.Form"/> with WS_EX_LAYERED +
/// SetLayeredWindowAttributes(LWA_ALPHA) rather than a WPF Window with
/// AllowsTransparency = true. WPF transparency forces UpdateLayeredWindow CPU DIB
/// rasterization on every resize frame; the layered WinForms window uses DWM
/// hardware GPU compositing instead, so it stays smooth at full refresh rate.
/// </summary>
public sealed class TransitionOverlayWindow : System.Windows.Forms.Form
{
    private const double EmergeDurationMs = 135.0;
    private const double CrossfadeDurationMs = 35.0;
    private const double EmergeTotalMs = 170.0;
    private const double RevealThreshold = 0.65;
    private const double CollapseDurationMs = 135.0;

    private EventHandler? _renderHandler;
    private Action? _onRevealTarget;
    private Action? _onFinished;
    private Bitmap? _snapshot;
    private bool _cancelled;

    public TransitionOverlayWindow()
    {
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        BackColor = Color.FromArgb(15, 18, 28);
        Bounds = new Rectangle(-10000, -10000, 1, 1);
        SetStyle(
            System.Windows.Forms.ControlStyles.AllPaintingInWmPaint
            | System.Windows.Forms.ControlStyles.UserPaint
            | System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer
            | System.Windows.Forms.ControlStyles.Opaque
            | System.Windows.Forms.ControlStyles.ResizeRedraw,
            true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override System.Windows.Forms.CreateParams CreateParams
    {
        get
        {
            System.Windows.Forms.CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080 | 0x00000020 | 0x08000000 | 0x00080000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32WindowHelper.TrySetRoundedCorners(Handle);
    }

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
    {
        base.OnPaint(e);

        // Never dispose _snapshot here: the caller owns the cached bitmaps.
        if (_snapshot is not null)
        {
            e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
            e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
            e.Graphics.DrawImage(_snapshot, ClientRectangle);
            return;
        }

        Rectangle client = ClientRectangle;

        using (var background = new SolidBrush(Color.FromArgb(14, 17, 26)))
        {
            e.Graphics.FillRectangle(background, client);
        }

        int barHeight = Math.Min(22, Math.Max(0, client.Height));
        if (barHeight > 0)
        {
            using var topBar = new SolidBrush(Color.FromArgb(24, 29, 44));
            e.Graphics.FillRectangle(topBar, new Rectangle(0, 0, client.Width, barHeight));
        }

        if (client.Width > 1 && client.Height > 1)
        {
            using var borderPen = new Pen(Color.FromArgb(140, 96, 165, 250), 1f);
            e.Graphics.DrawRectangle(borderPen, 0, 0, client.Width - 1, client.Height - 1);
        }

        using var font = new Font("Consolas", 20f, FontStyle.Bold);
        using var textBrush = new SolidBrush(Color.FromArgb(147, 197, 253));
        const string prompt = ">_";
        SizeF textSize = e.Graphics.MeasureString(prompt, font);
        float x = (client.Width - textSize.Width) * 0.5f;
        float y = (client.Height - textSize.Height) * 0.5f;
        e.Graphics.DrawString(prompt, font, textBrush, x, y);
    }

    public void StartEmerge(
        System.Windows.Rect fromRectPx,
        System.Windows.Rect toRectPx,
        Bitmap? snapshot,
        IntPtr bubbleHandle,
        Action onRevealTarget,
        Action? onFinished)
    {
        _cancelled = false;
        _onRevealTarget = onRevealTarget;
        _onFinished = onFinished;
        _snapshot = snapshot;

        Show();
        IntPtr handle = Handle;
        Win32WindowHelper.EnsureTopmostNoActivate(handle);
        Win32WindowHelper.PlaceWindowDirectlyBelow(handle, bubbleHandle);
        Win32WindowHelper.SetWindowAlpha(handle, 0.22);
        MoveTo(handle, fromRectPx);

        StartAnimation(handle, fromRectPx, toRectPx, emerge: true);
    }

    public void StartCollapse(
        System.Windows.Rect fromRectPx,
        System.Windows.Rect toRectPx,
        Bitmap? snapshot,
        IntPtr bubbleHandle,
        Action? onFinished)
    {
        _cancelled = false;
        _onRevealTarget = null;
        _onFinished = onFinished;
        _snapshot = snapshot;

        Show();
        IntPtr handle = Handle;
        Win32WindowHelper.EnsureTopmostNoActivate(handle);
        Win32WindowHelper.PlaceWindowDirectlyBelow(handle, bubbleHandle);
        Win32WindowHelper.SetWindowAlpha(handle, 1.0);
        MoveTo(handle, fromRectPx);

        StartAnimation(handle, fromRectPx, toRectPx, emerge: false);
    }

    public void Cancel()
    {
        _cancelled = true;
        StopRendering();
        _onRevealTarget = null;
        _onFinished = null;
        _snapshot = null;

        try
        {
            Close();
        }
        catch
        {
            // Overlay may already be closing; ignore.
        }

        Dispose();
    }

    private void StartAnimation(
        IntPtr handle,
        System.Windows.Rect fromRectPx,
        System.Windows.Rect toRectPx,
        bool emerge)
    {
        var stopwatch = Stopwatch.StartNew();
        bool revealed = false;

        _renderHandler = (_, _) =>
        {
            if (_cancelled)
            {
                StopRendering();
                return;
            }

            double elapsed = stopwatch.Elapsed.TotalMilliseconds;

            if (emerge)
            {
                if (elapsed < EmergeDurationMs)
                {
                    double t = elapsed / EmergeDurationMs;
                    double eased = WindowTransitionGeometry.EaseEmerge(t);
                    System.Windows.Rect rect = WindowTransitionGeometry.InterpolateRect(fromRectPx, toRectPx, eased);
                    MoveTo(handle, rect);
                    Win32WindowHelper.SetWindowAlpha(handle, WindowTransitionGeometry.ComputeEmergeOpacity(t));

                    if (!revealed && t >= RevealThreshold)
                    {
                        revealed = true;
                        _onRevealTarget?.Invoke();
                    }
                }
                else if (elapsed < EmergeTotalMs)
                {
                    double t = Math.Clamp((elapsed - EmergeDurationMs) / CrossfadeDurationMs, 0.0, 1.0);
                    MoveTo(handle, toRectPx);
                    Win32WindowHelper.SetWindowAlpha(handle, 1.0 - t);

                    if (!revealed)
                    {
                        revealed = true;
                        _onRevealTarget?.Invoke();
                    }
                }
                else
                {
                    Finish();
                }
            }
            else
            {
                if (elapsed < CollapseDurationMs)
                {
                    double t = elapsed / CollapseDurationMs;
                    double eased = WindowTransitionGeometry.EaseCollapse(t);
                    System.Windows.Rect rect = WindowTransitionGeometry.InterpolateRect(fromRectPx, toRectPx, eased);
                    MoveTo(handle, rect);
                    Win32WindowHelper.SetWindowAlpha(handle, WindowTransitionGeometry.ComputeCollapseOpacity(t));
                }
                else
                {
                    Finish();
                }
            }
        };

        CompositionTarget.Rendering += _renderHandler;
    }

    private static void MoveTo(IntPtr handle, System.Windows.Rect rect)
    {
        Win32WindowHelper.MoveAndResizeWindowPixels(
            handle,
            (int)Math.Round(rect.Left),
            (int)Math.Round(rect.Top),
            (int)Math.Round(rect.Width),
            (int)Math.Round(rect.Height),
            topmost: false);
    }

    private void Finish()
    {
        StopRendering();
        _onRevealTarget = null;
        _snapshot = null;

        try
        {
            Close();
        }
        catch
        {
            // Form may already be closing; ignore.
        }

        Dispose();

        Action? finished = _onFinished;
        _onFinished = null;
        finished?.Invoke();
    }

    private void StopRendering()
    {
        if (_renderHandler is not null)
        {
            CompositionTarget.Rendering -= _renderHandler;
            _renderHandler = null;
        }
    }
}
