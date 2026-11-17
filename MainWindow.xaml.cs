using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace HerdrBubble;

public partial class MainWindow : Window
{
    private const double BaseWindowDip = 120.0;
    private const double EdgeMargin = 14.0;
    private const double BubbleCoreOffset = 29.0; // (120 window - 62 bubble) / 2
    private const double DragEdgeClipMargin = 4.0;
    private const double DragThresholdPx = 5.0;
    private const double IdleOpacity = 0.48;

    private readonly DispatcherTimer _monitorTimer;
    private readonly DispatcherTimer _idleDimTimer;

    private IntPtr _selfHandle;
    private HwndSource? _hwndSource;
    private IntPtr _pinnedTerminalHandle;
    private IntPtr _lastExternalForeground;
    private IntPtr _lastObservedForeground;

    private readonly Dictionary<IntPtr, Rect> _lastKnownWindowRects = new();
    private readonly Dictionary<IntPtr, Rect> _lastKnownFrameInsets = new();
    private bool _pendingAnchorNewSpawn;
    private readonly Dictionary<IntPtr, System.Drawing.Bitmap> _cachedSnapshots = new();
    private TransitionOverlayWindow? _activeOverlay;
    private IntPtr _transitionTargetHandle = IntPtr.Zero;

    private TrayIconHelper? _trayIcon;
    private RegisteredWaitHandle? _wakeupRegistration;

    private bool _isMouseDown;
    private bool _isDragging;
    private bool _isMenuOpen;
    private bool _isDimmed;

    private bool _snapToEdges;
    private bool _herdrOnlyMode;
    private bool _idleDimEnabled = true;
    private string _launchCommand = "herdr";
    private string _targetKeyword = "herdr";

    private DateTime _awaitingLaunchedWindowUntilUtc = DateTime.MinValue;
    private HashSet<IntPtr>? _preLaunchWindowHandles;

    private Point _dragStartCursorPx;
    private Point _grabOffsetPx;

    // Spring physics state for edge snap animation (in screen pixels)
    private bool _springActive;
    private double _springPosX;
    private double _springPosY;
    private double _springTargetX;
    private double _springTargetY;
    private double _velocityX;
    private double _velocityY;
    private long _lastFrameTicks;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        Closed += MainWindow_Closed;

        _monitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        _monitorTimer.Tick += MonitorTimer_Tick;

        _idleDimTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4.2)
        };
        _idleDimTimer.Tick += IdleDimTimer_Tick;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _selfHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_selfHandle);
        _hwndSource?.AddHook(WndProc);
        Win32WindowHelper.RegisterGlobalToggleHotkey(_selfHandle);

        LoadAndApplySettings();
        Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);

        RefreshTargetAndStatus();
        _monitorTimer.Start();
        ResetIdleTimer();

        _trayIcon = new TrayIconHelper(
            onShowBubble: () => Dispatcher.BeginInvoke(() => BringBubbleIntoView(recenterToPrimary: false)),
            onResetPosition: () => Dispatcher.BeginInvoke(() => BringBubbleIntoView(recenterToPrimary: true)),
            onLaunchTerminal: () => Dispatcher.BeginInvoke(() =>
            {
                LaunchTargetTerminal();
                Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
            }),
            onExit: () => Dispatcher.BeginInvoke(Close));

        if (Application.Current is App app && app.WakeupEvent is { } wakeupEvent)
        {
            _wakeupRegistration = ThreadPool.RegisterWaitForSingleObject(
                wakeupEvent,
                (_, _) => Dispatcher.BeginInvoke(() => BringBubbleIntoView(recenterToPrimary: false)),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        // Entrance pop-in animation
        AnimateBubbleScale(0.35, 1.0, TimeSpan.FromMilliseconds(420), useBackEase: true);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32WindowHelper.WM_HOTKEY && wParam.ToInt32() == Win32WindowHelper.ToggleHotkeyId)
        {
            BringBubbleIntoView(recenterToPrimary: false);
            TriggerBlackHoleEmergenceAnimation();
            HandleBubbleClick();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void LoadAndApplySettings()
    {
        BubbleSettings settings = AppSettingsStore.Load();
        _snapToEdges = settings.MagneticEdgeSnap;
        _herdrOnlyMode = settings.StrictHerdrTitleFilter;
        _idleDimEnabled = settings.IdleDimEnabled;
        _launchCommand = string.IsNullOrWhiteSpace(settings.LaunchCommand) ? "herdr" : settings.LaunchCommand.Trim();
        _targetKeyword = string.IsNullOrWhiteSpace(settings.TargetKeyword) ? "herdr" : settings.TargetKeyword.Trim();

        if (settings.WindowX is double savedX && settings.WindowY is double savedY)
        {
            Rect winRect = GetCurrentWindowRectPixels();
            double centerX = savedX + winRect.Width * 0.5;
            double centerY = savedY + winRect.Height * 0.5;

            if (Win32WindowHelper.IsPointOnAnyMonitor(centerX, centerY))
            {
                MoveClampedToMonitor(savedX, savedY, useEdgeMargin: false);
                return;
            }
        }

        MoveToPrimaryDefaultEdge();
    }

    private void SaveSettings()
    {
        Rect winRect = GetCurrentWindowRectPixels();
        AppSettingsStore.Save(new BubbleSettings
        {
            WindowX = winRect.Left,
            WindowY = winRect.Top,
            MagneticEdgeSnap = _snapToEdges,
            StrictHerdrTitleFilter = _herdrOnlyMode,
            IdleDimEnabled = _idleDimEnabled,
            LaunchCommand = _launchCommand,
            TargetKeyword = _targetKeyword
        });
    }

    private List<TerminalWindowInfo> FindTerminals() =>
        Win32WindowHelper.FindTerminalWindows(_targetKeyword);

    private void LaunchTargetTerminal() =>
        Win32WindowHelper.LaunchHerdrTerminal(_launchCommand, _targetKeyword);

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
            Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        CancelActiveTransition();
        ClearCachedSnapshots();
        _lastKnownFrameInsets.Clear();
        SaveSettings();
        _monitorTimer.Stop();
        _idleDimTimer.Stop();
        CompositionTarget.Rendering -= OnSpringRender;

        Win32WindowHelper.UnregisterGlobalToggleHotkey(_selfHandle);
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        _wakeupRegistration?.Unregister(null);
        _wakeupRegistration = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    private void MoveToPrimaryDefaultEdge()
    {
        Rect workArea = Win32WindowHelper.GetPrimaryMonitorWorkAreaPixels();
        Rect winRect = GetCurrentWindowRectPixels();
        (double coreOffsetPx, double edgeMarginPx, _) = GetScaledMetrics(winRect.Width);

        int x = (int)Math.Round(workArea.Right - winRect.Width + coreOffsetPx - edgeMarginPx);
        int y = (int)Math.Round(workArea.Top + (workArea.Height - winRect.Height) * 0.45);
        Win32WindowHelper.MoveWindowPixels(_selfHandle, x, y);
    }

    private void MoveClampedToMonitor(double targetX, double targetY, bool useEdgeMargin)
    {
        Rect winRect = GetCurrentWindowRectPixels();
        Rect workArea = Win32WindowHelper.GetMonitorWorkAreaForPointPixels(
            targetX + winRect.Width * 0.5,
            targetY + winRect.Height * 0.5);

        (double coreOffsetPx, double edgeMarginPx, double dragClipPx) = GetScaledMetrics(winRect.Width);
        double margin = useEdgeMargin ? edgeMarginPx : dragClipPx;

        double minLeft = workArea.Left - coreOffsetPx + margin;
        double maxLeft = workArea.Right - winRect.Width + coreOffsetPx - margin;
        double minTop = workArea.Top - coreOffsetPx + margin;
        double maxTop = workArea.Bottom - winRect.Height + coreOffsetPx - margin;

        int clampedX = (int)Math.Round(Math.Clamp(targetX, minLeft, maxLeft));
        int clampedY = (int)Math.Round(Math.Clamp(targetY, minTop, maxTop));
        Win32WindowHelper.MoveWindowPixels(_selfHandle, clampedX, clampedY);
    }

    private void BringBubbleIntoView(bool recenterToPrimary)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (recenterToPrimary)
        {
            _springActive = false;
            CompositionTarget.Rendering -= OnSpringRender;
            MoveToPrimaryDefaultEdge();
            SaveSettings();
        }
        else
        {
            Rect winRect = GetCurrentWindowRectPixels();
            MoveClampedToMonitor(winRect.Left, winRect.Top, useEdgeMargin: true);
        }

        WakeFromIdle();
        Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
        AnimateBubbleScale(0.75, 1.0, TimeSpan.FromMilliseconds(280), useBackEase: true);
        TriggerRippleAnimation();
    }

    private Rect GetCurrentWindowRectPixels()
    {
        Rect winRect = Win32WindowHelper.GetWindowRectPixels(_selfHandle);
        if (!winRect.IsEmpty)
        {
            return winRect;
        }

        double width = Width > 0 ? Width : BaseWindowDip;
        double height = Height > 0 ? Height : BaseWindowDip;
        return new Rect(Left, Top, width, height);
    }

    private static (double coreOffsetPx, double edgeMarginPx, double dragClipPx) GetScaledMetrics(double winWidthPx)
    {
        double scale = winWidthPx > 0 ? winWidthPx / BaseWindowDip : 1.0;
        return (
            BubbleCoreOffset * scale,
            EdgeMargin * scale,
            DragEdgeClipMargin * scale);
    }

    private void IdleDimTimer_Tick(object? sender, EventArgs e)
    {
        _idleDimTimer.Stop();
        if (!_idleDimEnabled || _isMouseDown || _isDragging || _isMenuOpen || IsMouseOver)
        {
            return;
        }

        _isDimmed = true;
        var dimAnim = new DoubleAnimation(IdleOpacity, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BubbleContainer.BeginAnimation(OpacityProperty, dimAnim);
    }

    private void WakeFromIdle()
    {
        if (_isDimmed)
        {
            _isDimmed = false;
            var wakeAnim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            BubbleContainer.BeginAnimation(OpacityProperty, wakeAnim);
        }

        ResetIdleTimer();
    }

    private void ResetIdleTimer()
    {
        _idleDimTimer.Stop();
        if (_idleDimEnabled)
        {
            _idleDimTimer.Start();
        }
    }

    private void MonitorTimer_Tick(object? sender, EventArgs e)
    {
        IntPtr fg = Win32WindowHelper.GetForegroundHandle();
        bool foregroundChanged = fg != _lastObservedForeground;
        _lastObservedForeground = fg;

        if (fg != IntPtr.Zero && fg != _selfHandle)
        {
            _lastExternalForeground = fg;
        }

        if (!_isMenuOpen && !_isDragging)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (!IsVisible)
            {
                Show();
            }

            if (foregroundChanged || !Win32WindowHelper.IsWindowTopmost(_selfHandle))
            {
                Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
            }

            Rect winRect = GetCurrentWindowRectPixels();
            if (!Win32WindowHelper.IsPointOnAnyMonitor(
                winRect.Left + winRect.Width * 0.5,
                winRect.Top + winRect.Height * 0.5))
            {
                MoveToPrimaryDefaultEdge();
                SaveSettings();
            }
        }

        if (_preLaunchWindowHandles is not null)
        {
            if (DateTime.UtcNow > _awaitingLaunchedWindowUntilUtc)
            {
                _preLaunchWindowHandles = null;
                _pendingAnchorNewSpawn = false;
            }
            else
            {
                List<TerminalWindowInfo> current = FindTerminals();
                TerminalWindowInfo? spawned = current.FirstOrDefault(w => !_preLaunchWindowHandles.Contains(w.Handle));
                if (spawned is not null)
                {
                    _pinnedTerminalHandle = spawned.Handle;
                    _lastExternalForeground = spawned.Handle;

                    Rect spawnedRect = Win32WindowHelper.GetVisibleWindowRectPixels(spawned.Handle);
                    if (!spawnedRect.IsEmpty)
                    {
                        _lastKnownWindowRects[spawned.Handle] = spawnedRect;
                    }
                    _lastKnownFrameInsets[spawned.Handle] = Win32WindowHelper.GetWindowFrameInsetsPixels(spawned.Handle);

                    if (_pendingAnchorNewSpawn)
                    {
                        _pendingAnchorNewSpawn = false;

                        if (!Win32WindowHelper.IsRestoredAsMaximized(spawned.Handle) && !spawnedRect.IsEmpty)
                        {
                            Rect bubbleRect = GetCurrentWindowRectPixels();
                            double bubbleCenterX = bubbleRect.Left + bubbleRect.Width * 0.5;
                            double bubbleCenterY = bubbleRect.Top + bubbleRect.Height * 0.5;
                            Rect bubbleWorkArea = Win32WindowHelper.GetMonitorWorkAreaForPointPixels(bubbleCenterX, bubbleCenterY);

                            Rect anchored = WindowTransitionGeometry.ComputeAnchoredWindowRect(
                                bubbleRect,
                                spawnedRect,
                                bubbleWorkArea);

                            Rect insets = _lastKnownFrameInsets[spawned.Handle];
                            Rect rawAnchored = Win32WindowHelper.ApplyFrameInsets(anchored, insets);

                            Win32WindowHelper.MoveAndResizeWindowPixels(
                                spawned.Handle,
                                (int)Math.Round(rawAnchored.Left),
                                (int)Math.Round(rawAnchored.Top),
                                (int)Math.Round(rawAnchored.Width),
                                (int)Math.Round(rawAnchored.Height),
                                topmost: false);

                            _lastKnownWindowRects[spawned.Handle] = anchored;
                        }
                    }

                    _preLaunchWindowHandles = null;
                    _awaitingLaunchedWindowUntilUtc = DateTime.MinValue;
                }
            }
        }

        RefreshTargetAndStatus();

        IntPtr trackedTarget = ResolveTargetWindow();
        if (Win32WindowHelper.IsHandleValid(trackedTarget)
            && !Win32WindowHelper.IsMinimized(trackedTarget))
        {
            Rect visibleRect = Win32WindowHelper.GetVisibleWindowRectPixels(trackedTarget);
            if (!visibleRect.IsEmpty)
            {
                _lastKnownWindowRects[trackedTarget] = visibleRect;
                _lastKnownFrameInsets[trackedTarget] = Win32WindowHelper.GetWindowFrameInsetsPixels(trackedTarget);
            }
        }

        PruneTransitionCaches();
    }

    private void PruneTransitionCaches()
    {
        List<IntPtr> staleRects = _lastKnownWindowRects.Keys
            .Where(h => !Win32WindowHelper.IsHandleValid(h))
            .ToList();
        foreach (IntPtr handle in staleRects)
        {
            _lastKnownWindowRects.Remove(handle);
        }

        List<IntPtr> staleInsets = _lastKnownFrameInsets.Keys
            .Where(h => !Win32WindowHelper.IsHandleValid(h))
            .ToList();
        foreach (IntPtr handle in staleInsets)
        {
            _lastKnownFrameInsets.Remove(handle);
        }

        List<IntPtr> staleSnapshots = _cachedSnapshots.Keys
            .Where(h => !Win32WindowHelper.IsHandleValid(h))
            .ToList();
        foreach (IntPtr handle in staleSnapshots)
        {
            if (_cachedSnapshots.Remove(handle, out System.Drawing.Bitmap? stale))
            {
                stale.Dispose();
            }
        }
    }

    private void ClearCachedSnapshots()
    {
        foreach (System.Drawing.Bitmap snapshot in _cachedSnapshots.Values)
        {
            snapshot.Dispose();
        }

        _cachedSnapshots.Clear();
    }

    private IntPtr ResolveTargetWindow()
    {
        if (Win32WindowHelper.IsHandleValid(_pinnedTerminalHandle))
        {
            return _pinnedTerminalHandle;
        }

        List<TerminalWindowInfo> windows = FindTerminals();
        TerminalWindowInfo? match = _herdrOnlyMode
            ? windows.FirstOrDefault(w => w.IsHerdr)
            : windows.FirstOrDefault();

        _pinnedTerminalHandle = match?.Handle ?? IntPtr.Zero;
        return _pinnedTerminalHandle;
    }

    private void RefreshTargetAndStatus()
    {
        IntPtr target = ResolveTargetWindow();
        if (target == IntPtr.Zero)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber: no terminal found
            BubbleContainer.ToolTip = _herdrOnlyMode
                ? "No Herdr window found (click or Ctrl+Alt+H to launch)"
                : "No terminal found (click or Ctrl+Alt+H to launch)";
            return;
        }

        if (Win32WindowHelper.IsMinimized(target))
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(96, 165, 250)); // Sky blue: minimized & ready
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(52, 211, 153)); // Emerald: open/active
        }

        BubbleContainer.ToolTip = null;
    }

    private void Bubble_MouseEnter(object sender, MouseEventArgs e)
    {
        WakeFromIdle();
        if (!_isMouseDown)
        {
            AnimateBubbleScale(BubbleScale.ScaleX, 1.08, TimeSpan.FromMilliseconds(210), useBackEase: true);
        }
    }

    private void Bubble_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetIdleTimer();
        if (!_isMouseDown)
        {
            AnimateBubbleScale(BubbleScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(220), useBackEase: false);
        }
    }

    private void Bubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        WakeFromIdle();
        _springActive = false;
        CompositionTarget.Rendering -= OnSpringRender;

        _isMouseDown = true;
        _isDragging = false;
        _dragStartCursorPx = Win32WindowHelper.GetCursorPosPixels();

        Rect winRect = GetCurrentWindowRectPixels();
        _grabOffsetPx = new Point(
            _dragStartCursorPx.X - winRect.Left,
            _dragStartCursorPx.Y - winRect.Top);

        BubbleContainer.CaptureMouse();
        AnimateBubbleScale(BubbleScale.ScaleX, 0.90, TimeSpan.FromMilliseconds(110), useBackEase: false);
    }

    private void Bubble_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMouseDown)
        {
            return;
        }

        Point cursorPx = Win32WindowHelper.GetCursorPosPixels();
        double dx = cursorPx.X - _dragStartCursorPx.X;
        double dy = cursorPx.Y - _dragStartCursorPx.Y;

        if (!_isDragging && (Math.Abs(dx) > DragThresholdPx || Math.Abs(dy) > DragThresholdPx))
        {
            _isDragging = true;
        }

        if (_isDragging)
        {
            Rect workArea = Win32WindowHelper.GetMonitorWorkAreaForPointPixels(cursorPx.X, cursorPx.Y);
            Rect winRect = GetCurrentWindowRectPixels();
            (double coreOffsetPx, _, double dragClipPx) = GetScaledMetrics(winRect.Width);

            double newLeft = cursorPx.X - _grabOffsetPx.X;
            double newTop = cursorPx.Y - _grabOffsetPx.Y;

            double minLeft = workArea.Left - coreOffsetPx + dragClipPx;
            double maxLeft = workArea.Right - winRect.Width + coreOffsetPx - dragClipPx;
            double minTop = workArea.Top - coreOffsetPx + dragClipPx;
            double maxTop = workArea.Bottom - winRect.Height + coreOffsetPx - dragClipPx;

            int clampedX = (int)Math.Round(Math.Clamp(newLeft, minLeft, maxLeft));
            int clampedY = (int)Math.Round(Math.Clamp(newTop, minTop, maxTop));
            Win32WindowHelper.MoveWindowPixels(_selfHandle, clampedX, clampedY);
        }
    }

    private void Bubble_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMouseDown)
        {
            return;
        }

        _isMouseDown = false;
        BubbleContainer.ReleaseMouseCapture();
        ResetIdleTimer();

        if (_isDragging)
        {
            _isDragging = false;
            AnimateBubbleScale(BubbleScale.ScaleX, 1.0, TimeSpan.FromMilliseconds(260), useBackEase: true);
            if (_snapToEdges)
            {
                StartSpringSnapToNearestEdge();
            }
            else
            {
                SaveSettings();
            }
            return;
        }

        // Snappy black-hole emergence on click
        TriggerBlackHoleEmergenceAnimation();
        HandleBubbleClick();
    }

    private void HandleBubbleClick()
    {
        IntPtr target = ResolveTargetWindow();
        if (target == IntPtr.Zero)
        {
            if (DateTime.UtcNow < _awaitingLaunchedWindowUntilUtc)
            {
                return;
            }

            _preLaunchWindowHandles = FindTerminals()
                .Select(w => w.Handle)
                .ToHashSet();
            _awaitingLaunchedWindowUntilUtc = DateTime.UtcNow.AddSeconds(5);
            _pendingAnchorNewSpawn = true;

            LaunchTargetTerminal();
            Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
            RefreshTargetAndStatus();
            return;
        }

        if (Win32WindowHelper.IsMinimized(target) || _lastExternalForeground != target)
        {
            ShowTerminalFromBubble(target);
        }
        else
        {
            MinimizeTerminalToBubble(target);
        }
    }

    private void ShowTerminalFromBubble(IntPtr target)
    {
        CancelActiveTransition();

        bool isMinimized = Win32WindowHelper.IsMinimized(target);

        Rect baseRect;
        if (!isMinimized)
        {
            baseRect = Win32WindowHelper.GetVisibleWindowRectPixels(target);
        }
        else if (_lastKnownWindowRects.TryGetValue(target, out Rect cachedRect) && !cachedRect.IsEmpty)
        {
            baseRect = cachedRect;
        }
        else
        {
            baseRect = Win32WindowHelper.GetPredictedWindowRectPixels(target);
        }

        Rect bubbleRect = GetCurrentWindowRectPixels();
        double bubbleCenterX = bubbleRect.Left + bubbleRect.Width * 0.5;
        double bubbleCenterY = bubbleRect.Top + bubbleRect.Height * 0.5;
        Rect bubbleWorkArea = Win32WindowHelper.GetMonitorWorkAreaForPointPixels(bubbleCenterX, bubbleCenterY);

        bool isMaximized = Win32WindowHelper.IsRestoredAsMaximized(target);

        Rect targetRect = isMaximized
            ? bubbleWorkArea
            : WindowTransitionGeometry.ComputeAnchoredWindowRect(bubbleRect, baseRect, bubbleWorkArea);

        if (targetRect.IsEmpty)
        {
            Win32WindowHelper.BringToFront(target);
            _lastExternalForeground = target;
            Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
            RefreshTargetAndStatus();
            return;
        }

        Rect frameInsets = _lastKnownFrameInsets.TryGetValue(target, out Rect cachedInsets)
            ? cachedInsets
            : (isMinimized ? new Rect(0, 0, 0, 0) : Win32WindowHelper.GetWindowFrameInsetsPixels(target));

        Rect rawTargetRect = Win32WindowHelper.ApplyFrameInsets(targetRect, frameInsets);

        Rect snapshotRect = !baseRect.IsEmpty ? baseRect : targetRect;

        System.Drawing.Bitmap? snapshot = null;
        if (!isMinimized && Win32WindowHelper.IsForeground(target))
        {
            snapshot = WindowSnapshotHelper.CaptureWindowSnapshot(snapshotRect);

            if (snapshot is not null)
            {
                if (_cachedSnapshots.Remove(target, out System.Drawing.Bitmap? previous))
                {
                    previous.Dispose();
                }

                _cachedSnapshots[target] = snapshot;
            }
        }
        else
        {
            _cachedSnapshots.TryGetValue(target, out snapshot);
        }

        Rect bubbleOrigin = WindowTransitionGeometry.ComputeBubbleOriginRect(
            bubbleRect,
            targetRect);

        Win32WindowHelper.TrySetTransitionsDisabled(target, true);

        var overlay = new TransitionOverlayWindow();
        _activeOverlay = overlay;
        _transitionTargetHandle = target;

        Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);

        overlay.StartEmerge(
            bubbleOrigin,
            targetRect,
            snapshot,
            _selfHandle,
            onRevealTarget: () =>
            {
                if (isMaximized)
                {
                    Win32WindowHelper.BringToFront(target);
                    Win32WindowHelper.MoveMaximizedWindowToMonitor(target, bubbleWorkArea);
                }
                else
                {
                    if (Win32WindowHelper.IsMinimized(target))
                    {
                        Win32WindowHelper.SetMinimizedNormalRect(target, rawTargetRect);
                    }

                    Win32WindowHelper.BringToFront(target);
                    Win32WindowHelper.MoveAndResizeWindowPixels(
                        target,
                        (int)Math.Round(rawTargetRect.Left),
                        (int)Math.Round(rawTargetRect.Top),
                        (int)Math.Round(rawTargetRect.Width),
                        (int)Math.Round(rawTargetRect.Height),
                        topmost: false);

                    _lastKnownWindowRects[target] = targetRect;
                }

                _lastExternalForeground = target;
                Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
                RefreshTargetAndStatus();
            },
            onFinished: () =>
            {
                Win32WindowHelper.TrySetTransitionsDisabled(target, false);
                _activeOverlay = null;
                _transitionTargetHandle = IntPtr.Zero;
                Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
            });

        Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
    }

    private void MinimizeTerminalToBubble(IntPtr target)
    {
        CancelActiveTransition();

        Rect targetRect = Win32WindowHelper.GetVisibleWindowRectPixels(target);
        if (targetRect.IsEmpty)
        {
            Win32WindowHelper.MinimizeWindow(target);
            _lastExternalForeground = IntPtr.Zero;
            Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
            RefreshTargetAndStatus();
            return;
        }

        _lastKnownWindowRects[target] = targetRect;

        System.Drawing.Bitmap? snapshot = WindowSnapshotHelper.CaptureWindowSnapshot(targetRect);

        if (snapshot is not null)
        {
            if (_cachedSnapshots.Remove(target, out System.Drawing.Bitmap? previous))
            {
                previous.Dispose();
            }

            _cachedSnapshots[target] = snapshot;
        }

        Rect bubbleOrigin = WindowTransitionGeometry.ComputeBubbleOriginRect(
            GetCurrentWindowRectPixels(),
            targetRect);

        Win32WindowHelper.TrySetTransitionsDisabled(target, true);

        var overlay = new TransitionOverlayWindow();
        _activeOverlay = overlay;
        _transitionTargetHandle = target;

        Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);

        overlay.StartCollapse(
            targetRect,
            bubbleOrigin,
            snapshot,
            _selfHandle,
            onFinished: () =>
            {
                Win32WindowHelper.TrySetTransitionsDisabled(target, false);
                _activeOverlay = null;
                _transitionTargetHandle = IntPtr.Zero;
            });

        Win32WindowHelper.MinimizeWindow(target);
        _lastExternalForeground = IntPtr.Zero;
        Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
        RefreshTargetAndStatus();
    }

    private void CancelActiveTransition()
    {
        TransitionOverlayWindow? overlay = _activeOverlay;
        _activeOverlay = null;

        overlay?.Cancel();

        if (_transitionTargetHandle != IntPtr.Zero)
        {
            Win32WindowHelper.TrySetTransitionsDisabled(_transitionTargetHandle, false);
            _transitionTargetHandle = IntPtr.Zero;
        }
    }

    private void Bubble_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        WakeFromIdle();
        _isMenuOpen = true;
        ContextMenu menu = BuildContextMenu();
        menu.Closed += (_, _) =>
        {
            _isMenuOpen = false;
            ResetIdleTimer();
        };
        menu.PlacementTarget = BubbleContainer;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu")
        };

        Style itemStyle = (Style)FindResource("DarkMenuItem");
        List<TerminalWindowInfo> terminals = FindTerminals();

        if (terminals.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "No terminal windows detected",
                IsEnabled = false,
                Style = itemStyle
            });
        }
        else
        {
            foreach (TerminalWindowInfo term in terminals)
            {
                string shortTitle = term.Title.Length > 34
                    ? term.Title[..31] + "..."
                    : term.Title;

                var item = new MenuItem
                {
                    Header = $"{term.ProcessName}: {shortTitle}",
                    IsChecked = term.Handle == _pinnedTerminalHandle,
                    Style = itemStyle
                };
                item.Click += (_, _) =>
                {
                    _pinnedTerminalHandle = term.Handle;
                    ShowTerminalFromBubble(term.Handle);
                };
                menu.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator { Margin = new Thickness(6, 4, 6, 4), Opacity = 0.25 });

        var herdrOnlyItem = new MenuItem
        {
            Header = "Target Herdr windows only",
            IsChecked = _herdrOnlyMode,
            Style = itemStyle
        };
        herdrOnlyItem.Click += (_, _) =>
        {
            _herdrOnlyMode = !_herdrOnlyMode;
            _pinnedTerminalHandle = IntPtr.Zero;
            RefreshTargetAndStatus();
            SaveSettings();
        };
        menu.Items.Add(herdrOnlyItem);

        var snapItem = new MenuItem
        {
            Header = "Magnetic edge snap",
            IsChecked = _snapToEdges,
            Style = itemStyle
        };
        snapItem.Click += (_, _) =>
        {
            _snapToEdges = !_snapToEdges;
            if (_snapToEdges)
            {
                StartSpringSnapToNearestEdge();
            }
            SaveSettings();
        };
        menu.Items.Add(snapItem);

        var dimItem = new MenuItem
        {
            Header = "Auto-dim when idle",
            IsChecked = _idleDimEnabled,
            Style = itemStyle
        };
        dimItem.Click += (_, _) =>
        {
            _idleDimEnabled = !_idleDimEnabled;
            WakeFromIdle();
            SaveSettings();
        };
        menu.Items.Add(dimItem);

        var startupItem = new MenuItem
        {
            Header = "Start with Windows",
            IsChecked = StartupHelper.IsStartupEnabled(),
            Style = itemStyle
        };
        startupItem.Click += (_, _) =>
        {
            StartupHelper.SetStartupEnabled(!StartupHelper.IsStartupEnabled());
        };
        menu.Items.Add(startupItem);

        var resetItem = new MenuItem
        {
            Header = "Reset position",
            Style = itemStyle
        };
        resetItem.Click += (_, _) => BringBubbleIntoView(recenterToPrimary: true);
        menu.Items.Add(resetItem);

        var launchItem = new MenuItem
        {
            Header = "Launch new Herdr terminal",
            Style = itemStyle
        };
        launchItem.Click += (_, _) =>
        {
            LaunchTargetTerminal();
            Win32WindowHelper.EnsureTopmostNoActivate(_selfHandle);
        };
        menu.Items.Add(launchItem);

        menu.Items.Add(new MenuItem
        {
            Header = "Hotkey: Ctrl + Alt + H",
            IsEnabled = false,
            Style = itemStyle
        });

        var exitItem = new MenuItem
        {
            Header = "Exit",
            Style = itemStyle
        };
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void StartSpringSnapToNearestEdge()
    {
        Rect winRect = GetCurrentWindowRectPixels();
        double centerX = winRect.Left + winRect.Width * 0.5;
        double centerY = winRect.Top + winRect.Height * 0.5;

        Rect workArea = Win32WindowHelper.GetMonitorWorkAreaForPointPixels(centerX, centerY);
        (double coreOffsetPx, double edgeMarginPx, _) = GetScaledMetrics(winRect.Width);

        double workCenterX = workArea.Left + workArea.Width * 0.5;
        double leftEdgeX = workArea.Left - coreOffsetPx + edgeMarginPx;
        double rightEdgeX = workArea.Right - winRect.Width + coreOffsetPx - edgeMarginPx;

        _springPosX = winRect.Left;
        _springPosY = winRect.Top;
        _springTargetX = centerX < workCenterX ? leftEdgeX : rightEdgeX;

        double minTop = workArea.Top - coreOffsetPx + edgeMarginPx;
        double maxTop = workArea.Bottom - winRect.Height + coreOffsetPx - edgeMarginPx;
        _springTargetY = Math.Clamp(winRect.Top, minTop, maxTop);

        _velocityX = 0;
        _velocityY = 0;
        _lastFrameTicks = Stopwatch.GetTimestamp();

        if (!_springActive)
        {
            _springActive = true;
            CompositionTarget.Rendering += OnSpringRender;
        }
    }

    private void OnSpringRender(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastFrameTicks) / (double)Stopwatch.Frequency;
        _lastFrameTicks = now;

        // Clamp delta time to keep spring stable across frame hiccups
        dt = Math.Clamp(dt, 0.001, 0.033);

        const double stiffness = 320.0;
        const double damping = 26.0;

        double forceX = (_springTargetX - _springPosX) * stiffness - _velocityX * damping;
        double forceY = (_springTargetY - _springPosY) * stiffness - _velocityY * damping;

        _velocityX += forceX * dt;
        _velocityY += forceY * dt;

        _springPosX += _velocityX * dt;
        _springPosY += _velocityY * dt;

        bool settledX = Math.Abs(_springTargetX - _springPosX) < 0.35 && Math.Abs(_velocityX) < 1.5;
        bool settledY = Math.Abs(_springTargetY - _springPosY) < 0.35 && Math.Abs(_velocityY) < 1.5;

        if (settledX && settledY)
        {
            Win32WindowHelper.MoveWindowPixels(
                _selfHandle,
                (int)Math.Round(_springTargetX),
                (int)Math.Round(_springTargetY));
            _springActive = false;
            CompositionTarget.Rendering -= OnSpringRender;
            SaveSettings();
            return;
        }

        Win32WindowHelper.MoveWindowPixels(
            _selfHandle,
            (int)Math.Round(_springPosX),
            (int)Math.Round(_springPosY));
    }

    private void AnimateBubbleScale(double from, double to, TimeSpan duration, bool useBackEase)
    {
        IEasingFunction easing = useBackEase
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var animX = new DoubleAnimation(from, to, duration) { EasingFunction = easing };
        var animY = new DoubleAnimation(from, to, duration) { EasingFunction = easing };

        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    private void TriggerBlackHoleEmergenceAnimation()
    {
        // 1. Bubble briefly compresses into the singularity and bursts out fast (~290ms total)
        var bubbleScaleAnim = new DoubleAnimationUsingKeyFrames();
        bubbleScaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.58,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        bubbleScaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.14,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(185)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        bubbleScaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(290)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));

        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, bubbleScaleAnim);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, bubbleScaleAnim);

        var bubbleTwistAnim = new DoubleAnimationUsingKeyFrames();
        bubbleTwistAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            -16.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        bubbleTwistAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280)),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }));
        BubbleRotate.BeginAnimation(RotateTransform.AngleProperty, bubbleTwistAnim);

        // 2. Singularity void opens behind the bubble and snaps shut
        var voidScaleAnim = new DoubleAnimationUsingKeyFrames();
        voidScaleAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        voidScaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.28,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)),
            new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.0 }));
        voidScaleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.15,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(290)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        var voidOpacityAnim = new DoubleAnimationUsingKeyFrames();
        voidOpacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        voidOpacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.92, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
        voidOpacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(290)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));

        VoidScale.BeginAnimation(ScaleTransform.ScaleXProperty, voidScaleAnim);
        VoidScale.BeginAnimation(ScaleTransform.ScaleYProperty, voidScaleAnim);
        SingularityVoid.BeginAnimation(OpacityProperty, voidOpacityAnim);

        // 3. Swirling accretion disk spins and dissipates
        var accretionScaleAnim = new DoubleAnimation(0.55, 1.48, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.0 }
        };
        var accretionSpinAnim = new DoubleAnimation(0.0, 155.0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var accretionOpacityAnim = new DoubleAnimationUsingKeyFrames();
        accretionOpacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        accretionOpacityAnim.KeyFrames.Add(new EasingDoubleKeyFrame(
            0.0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));

        AccretionScale.BeginAnimation(ScaleTransform.ScaleXProperty, accretionScaleAnim);
        AccretionScale.BeginAnimation(ScaleTransform.ScaleYProperty, accretionScaleAnim);
        AccretionRotate.BeginAnimation(RotateTransform.AngleProperty, accretionSpinAnim);
        AccretionRing.BeginAnimation(OpacityProperty, accretionOpacityAnim);

        // 4. Fast gravitational shockwave ripple
        TriggerRippleAnimation();
    }

    private void TriggerRippleAnimation()
    {
        var scaleAnim = new DoubleAnimation(0.75, 1.72, TimeSpan.FromMilliseconds(270))
        {
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4.5 }
        };
        var opacityAnim = new DoubleAnimation(0.85, 0.0, TimeSpan.FromMilliseconds(270))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        RippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        RippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
        RippleRing.BeginAnimation(OpacityProperty, opacityAnim);
    }
}
