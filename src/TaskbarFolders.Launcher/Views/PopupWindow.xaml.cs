using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TaskbarFolders.Launcher.Services;
using TaskbarFolders.Launcher.ViewModels;
using TaskbarFolders.Shared.Configuration;
using TaskbarFolders.Shared.Models;

namespace TaskbarFolders.Launcher.Views;

/// <summary>
/// Popup window displayed when a taskbar group is clicked. Reads the user's popup-position
/// preference from the injected <see cref="AppSettings"/>, places itself anchored on the
/// cursor position at click time, plays a fade+scale open animation, and dismisses on focus
/// loss.
/// </summary>
/// <remarks>
/// v0.3+: chrome is fully transparent — no acrylic backdrop, no border, no shadow. The
/// previous TryEnableAcrylic path was removed; only the per-tile hover highlight is visible.
/// <see cref="AppSettings"/> is injected directly rather than re-loaded via
/// <see cref="IAppSettingsStore"/>; App.OnStartup loads once and registers the instance as a
/// singleton.
/// </remarks>
public partial class PopupWindow : Window
{
    /// <summary>Tile width + height in DIPs. Mirrors the Image width in the data template.</summary>
    private const int TilePx = 96;

    /// <summary>Outer padding on the popup Border in DIPs.</summary>
    private const int PaddingPx = 12;

    /// <summary>
    /// Interval of the never-activated fallback timer. Long enough that a user who is
    /// mousing towards the popup is not interrupted, short enough that a popup Windows
    /// refused to activate cannot linger as an orphaned Topmost window.
    /// </summary>
    private static readonly TimeSpan _activationFallbackInterval = TimeSpan.FromSeconds(3);

    private readonly PopupViewModel _viewModel;
    private readonly ITaskbarPositionHelper _positionHelper;
    private readonly AppSettings _settings;
    private DispatcherTimer? _safetyTimer;
    private DispatcherTimer? _activationFallbackTimer;
    private bool _wasActivated;

    /// <summary>Initializes a new instance of the <see cref="PopupWindow"/> class.</summary>
    public PopupWindow(
        PopupViewModel viewModel,
        ITaskbarPositionHelper positionHelper,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(positionHelper);
        ArgumentNullException.ThrowIfNull(settings);

        InitializeComponent();
        _viewModel = viewModel;
        _positionHelper = positionHelper;
        _settings = settings;
        DataContext = viewModel;

        _viewModel.LaunchSucceeded += OnLaunchSucceeded;
        // LastError toggles the docked strip after open; height must grow/shrink with it
        // or the strip clips the fixed 96 px tiles (issue #24).
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Activated += OnActivated;
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplySizeAndPlacement();

        if (_settings.EnableAnimations && TryFindResource("OpenAnimation") is Storyboard storyboard)
        {
            ScheduleAnimationOnFirstRender(storyboard);
        }
        else
        {
            // Either animations are disabled OR the storyboard resource was not found.
            // The XAML defaults Opacity=0.5 + ScaleX/Y=0.9 mean a missing storyboard would
            // leave the popup permanently faded and undersized — snap to the end state so
            // the user always sees the finished popup, regardless of resource lookup outcome.
            SnapToEndState();
        }

        // Never-activated fallback: dismiss-on-focus-loss relies on Deactivated, which can
        // only fire after the window has been activated once. When Windows denies the
        // activation (foreground-lock, background spawn) the popup would otherwise linger
        // as an orphaned Topmost window forever. Re-arms while the pointer is over the
        // popup so a user interacting with a never-activated popup is not interrupted;
        // OnActivated disarms it on the normal path.
        _activationFallbackTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = _activationFallbackInterval,
        };
        _activationFallbackTimer.Tick += (_, _) =>
        {
            if (_wasActivated || IsMouseOver)
            {
                return;
            }

            _activationFallbackTimer?.Stop();
            Close();
        };
        _activationFallbackTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PopupViewModel.LastError))
        {
            return;
        }

        // Resize only — do not re-run the open animation after the window is already shown.
        ApplySizeAndPlacement();
    }

    /// <summary>
    /// Sets Width from the column count and Height from what the chrome actually wants,
    /// then recomputes taskbar-aware placement and the scale-transform pivot.
    /// </summary>
    private void ApplySizeAndPlacement()
    {
        // Explicit Width + Height skip the SizeToContent measure pass that pre-v0.4 added
        // ~5-10 ms before placement could be computed. Empty / unavailable groups fall
        // back to MinHeight, keeping the banner-only layout centred.
        var cols = Math.Max(_viewModel.Columns, 1);
        Width = Math.Clamp(cols * TilePx + 2 * PaddingPx, MinWidth, MaxWidth);

        // Height comes from measuring ChromeRoot rather than adding up tile rows, because
        // the error strip docks Bottom in a fill-last DockPanel: at a grid-only height it
        // takes its space out of the fixed 96 px tiles and clips the last row (issue #24).
        // ChromeRoot's DesiredSize already carries the grid, its padding and whatever the
        // LastError binding has currently made of the strip, so nothing here needs to know
        // the strip exists.
        //
        // Measuring the strip on its own instead would mean writing Visibility to force it
        // visible for the measure pass — and a local value on a OneWay-bound property
        // discards the binding for good. ClearValue then removes only the local value, not
        // the expression, and Visibility falls back to its default, which is Visible. The
        // strip would survive as an empty red bar and clip the tiles again on the next
        // click, when LaunchApp clears LastError.
        //
        // UpdateTarget rather than an assignment: this runs from LastError's PropertyChanged,
        // and the strip's own binding is not guaranteed to have been notified first, so the
        // measure below could otherwise see the previous visibility. Pushing the source value
        // through the existing expression is deterministic and leaves the binding in place.
        ErrorStrip.GetBindingExpression(VisibilityProperty)?.UpdateTarget();

        // Measure short-circuits when the element is measure-valid and the constraint is
        // unchanged, which is exactly the case on a repeat pass — invalidate so DesiredSize
        // reflects the strip's current state rather than the previous one.
        UpdateLayout();
        ChromeRoot.InvalidateMeasure();
        ChromeRoot.Measure(new Size(Width, double.PositiveInfinity));
        Height = Math.Clamp(ChromeRoot.DesiredSize.Height, MinHeight, MaxHeight);

        // Second pass so hit-test / DesiredSize match the new explicit size before placement.
        UpdateLayout();

        var placement = _positionHelper.ComputePlacement(new Size(Width, Height), _settings.PopupPosition);
        Left = placement.Left;
        Top = placement.Top;

        UpdateScalePivot();
    }

    /// <summary>
    /// Sets the ScaleTransform pivot to bottom-centre so the open animation grows the
    /// popup up out of the clicked tile. Must also run after a strip-driven resize so
    /// CenterY tracks the new Height. Transform lives on ChromeRoot, not the Window —
    /// Window.CoerceRenderTransform rejects non-identity transforms outright.
    /// </summary>
    private void UpdateScalePivot()
    {
        if (ChromeRoot.RenderTransform is ScaleTransform scale)
        {
            scale.CenterX = Width / 2.0;
            scale.CenterY = Height;
        }
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _wasActivated = true;
        _activationFallbackTimer?.Stop();
        _activationFallbackTimer = null;
    }

    private void SnapToEndState()
    {
        if (FindName("ChromeRoot") is Border chrome)
        {
            chrome.Opacity = 1;
            if (chrome.RenderTransform is ScaleTransform scale)
            {
                scale.ScaleX = 1;
                scale.ScaleY = 1;
            }
        }
    }

    /// <summary>
    /// Schedules <see cref="Storyboard.Begin(System.Windows.FrameworkElement)"/> on the next
    /// dispatcher Render cycle and arms a 500 ms safety-net that force-snaps to the end state
    /// if the popup has not reached it. v0.4.1 used <see cref="CompositionTarget.Rendering"/>,
    /// but Win11 24H2 can skip the composition pass entirely for fully-transparent windows —
    /// <c>Rendering</c> never fires and the popup stays invisible forever. A
    /// <see cref="Dispatcher.BeginInvoke(DispatcherPriority, Delegate)"/> at Render priority
    /// always runs regardless of paint state. <c>Storyboard.SetTarget</c> on the
    /// <c>ChromeRoot</c> opacity child resolves the visual-tree element directly instead of
    /// going through the resource-scope <c>TargetName</c> lookup, which can silently no-op
    /// when the storyboard lives in <c>Window.Resources</c>. The 500 ms timer then guarantees
    /// the popup is visible no matter which corner of the WPF animation pipeline fails.
    /// </summary>
    private void ScheduleAnimationOnFirstRender(Storyboard storyboard)
    {
        if (FindName("ChromeRoot") is not Border chrome)
        {
            // No chrome to animate — collapsing to the end state still gives the user a
            // popup; missing chrome is a far worse bug than a missed animation.
            SnapToEndState();
            return;
        }

        // Clone before mutating: the resource instance can be frozen (BAML-loaded
        // Freezables), and SetTarget on a frozen timeline throws.
        storyboard = storyboard.Clone();
        foreach (var anim in storyboard.Children)
        {
            if (Storyboard.GetTargetName(anim) == "ChromeRoot")
            {
                Storyboard.SetTarget(anim, chrome);
            }
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => storyboard.Begin(this)));

        // Stored in a field so OnClosed can stop the timer if the popup is dismissed before
        // 500 ms (Deactivated / LaunchSucceeded close the window). Without that, the captured
        // chrome reference keeps the window alive until the tick fires and writes opacity on
        // a detached visual tree.
        _safetyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _safetyTimer.Tick += (_, _) =>
        {
            _safetyTimer?.Stop();

            // The start state is half-opaque, so "still below 0.5" can no longer tell a
            // stuck animation from a finished one. Anything short of the end state after
            // 500 ms means the storyboard never ran to completion.
            if (chrome.Opacity < 1)
            {
                SnapToEndState();
            }
        };
        _safetyTimer.Start();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Only dismiss after the window has genuinely held activation once. A spurious
        // deactivation before first activation would otherwise close the popup before
        // the user ever saw it.
        if (_wasActivated)
        {
            Close();
        }
    }

    private void OnLaunchSucceeded(object? sender, EventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        // Cancel any in-flight icon-load tasks so post-close task completions cannot
        // mutate the now-detached view model.
        _viewModel.CancelIconLoad();
        // Disarm the visibility safety-net if the popup closes before it fires (e.g.,
        // Deactivated dismissal within 500 ms). Letting it tick after Close would write
        // opacity on a detached visual tree.
        _safetyTimer?.Stop();
        _safetyTimer = null;
        _activationFallbackTimer?.Stop();
        _activationFallbackTimer = null;
        _viewModel.LaunchSucceeded -= OnLaunchSucceeded;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Activated -= OnActivated;
        Closed -= OnClosed;
        SourceInitialized -= OnSourceInitialized;
    }
}
