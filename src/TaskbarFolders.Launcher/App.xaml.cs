using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskbarFolders.Core.Icons;
using TaskbarFolders.Core.Shortcuts;
using TaskbarFolders.Launcher.Configuration;
using TaskbarFolders.Launcher.Services;
using TaskbarFolders.Launcher.ViewModels;
using TaskbarFolders.Shared.Configuration;
using TaskbarFolders.Shared.Logging;
using TaskbarFolders.Shared.Models;

namespace TaskbarFolders.Launcher;

/// <summary>
/// Application entry point for the TaskbarFolders Launcher.
/// </summary>
/// <remarks>
/// Two modes:
/// <list type="bullet">
///   <item><b>Popup mode</b> (default) — user clicked a pinned tile; show the apps grid.</item>
///   <item><b>Pin mode</b> (<c>--pin-mode</c>) — Manager asked us to pin the group to the
///   taskbar via <see cref="Windows.UI.Shell.TaskbarManager"/>. Shows the system permission
///   dialog and exits with a status code the Manager interprets.</item>
/// </list>
/// Exit codes (popup/startup paths): <c>1</c> = no group id resolvable, <c>3</c> = startup
/// threw, <c>4</c> = unhandled dispatcher exception after startup. Pin mode reports its own
/// runner outcome codes via <see cref="TaskbarPinRunner"/> (the Manager maps those; only a
/// pin-mode process that dies before the runner reuses 1/3/4 with the meanings above).
/// Every failure path writes to the launcher file log via
/// <see cref="StartupFailureLogger"/> — Trace-only diagnostics are invisible in published
/// builds and are forbidden here.
/// </remarks>
public partial class App : Application
{
    private ServiceProvider? _services;

    /// <summary>
    /// Gets the application service provider once <see cref="OnStartup"/> has completed.
    /// </summary>
    public IServiceProvider? Services => _services;

    /// <inheritdoc/>
    protected override async void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Last-chance handlers so no failure can end the process without a log line.
        // AppDomain.UnhandledException covers non-dispatcher threads (process dies after
        // the handler); DispatcherUnhandledException covers UI-thread throws outside the
        // OnStartup try below (e.g. deferred Storyboard.Begin callbacks).
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            StartupFailureLogger.Log(
                "Unhandled exception; process is terminating.",
                args.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, args) =>
        {
            StartupFailureLogger.Log(
                "Unhandled dispatcher exception; shutting down with exit code 4.",
                args.Exception);
            args.Handled = true;
            Shutdown(4);
        };

        var (groupId, fromAumid) = ResolveGroupId(e.Args);
        if (groupId is null)
        {
            StartupFailureLogger.Log(
                $"Launcher started without {CommandLineParser.GroupIdArg} argument and no AUMID fallback available; shutting down with exit code 1.");
            Shutdown(1);
            return;
        }

        // Any unhandled exception inside the async branches would otherwise crash the
        // process via async-void with an unobservable exit code; the Manager would map
        // the resulting random exit code to PinResult.Error. Wrap so we always shutdown
        // with a documented exit code.
        try
        {
            if (CommandLineParser.HasPinMode(e.Args))
            {
                await RunPinModeAsync(groupId, fromAumid).ConfigureAwait(true);
                return;
            }

            await RunPopupModeAsync(e, groupId, fromAumid).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StartupFailureLogger.Log("Launcher OnStartup threw; shutting down with exit code 3.", ex);
            Shutdown(3);
        }
    }

    /// <summary>
    /// Resolves the target group id from either the explicit <c>--group-id</c> argument
    /// (Manager-spawned + .lnk-pinned paths) or the AUMID Windows already assigned to the
    /// process (TaskbarManager-pinned tile paths where the original command line is not
    /// preserved). Returns a tuple of the id and whether it was recovered from the AUMID;
    /// callers use the latter to decide whether to re-stamp the AUMID (re-stamping an
    /// inherited AUMID can cause identity drift if Windows normalised the string).
    /// </summary>
    private static (string? GroupId, bool FromAumid) ResolveGroupId(string[] args)
    {
        var fromArgs = CommandLineParser.TryParseGroupId(args);
        if (fromArgs is not null)
        {
            return (fromArgs, false);
        }

        var assignedAumid = Interop.NativeMethods.TryGetCurrentProcessAumid();
        if (assignedAumid is not null && GroupAumid.TryExtractGroupId(assignedAumid, out var fromAumid))
        {
            return (fromAumid, true);
        }

        return (null, false);
    }

    /// <summary>
    /// Pin-mode entry point. Stamps AUMID, builds a minimal DI scope, shows the off-screen
    /// host window so the WinRT pin dialog has a foreground parent, awaits the pin runner,
    /// shuts down with the runner's exit code so the Manager can react to the outcome.
    /// </summary>
    private async Task RunPinModeAsync(string groupId, bool aumidAlreadyInherited)
    {
        // AUMID must match GroupAumid.For(groupId) before the WinRT call so
        // RequestPinCurrentAppAsync pins the right identity. Skip the stamp when Windows
        // already gave us the AUMID via process activation — re-stamping the inherited
        // string could drift the identity if Windows normalised the case or trimmed
        // whitespace differently from our For() formatter.
        var aumid = GroupAumid.For(groupId);
        if (!aumidAlreadyInherited)
        {
            var stampHr = Interop.NativeMethods.SetCurrentProcessExplicitAppUserModelID(aumid);
            if (stampHr != 0)
            {
                StartupFailureLogger.Log(
                    $"SetCurrentProcessExplicitAppUserModelID('{aumid}') failed with HRESULT 0x{stampHr:X8}; " +
                    "the pin would target the wrong identity.");
            }
        }

        var paths = new AppDataPathProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddTaskbarFoldersFileLogging(paths.LogsDirectory, "launcher"));
        services.AddTaskbarFoldersLauncher(new LauncherOptions(groupId), paths);
        // PopupWindow is registered in the same graph but never resolved in pin-mode;
        // AppSettings is required by the DI graph regardless, so register a default.
        services.AddSingleton(new AppSettings());

        _services = services.BuildServiceProvider();

        var logger = _services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Launcher pin-mode starting for group {GroupId}.", groupId);

        var host = _services.GetRequiredService<Views.PinHostWindow>();
        MainWindow = host;
        host.Show();
        host.Activate();

        var runner = _services.GetRequiredService<TaskbarPinRunner>();
        var exitCode = await runner.RunAsync(host, aumid).ConfigureAwait(true);

        logger.LogInformation("Pin runner exit code {ExitCode} for group {GroupId}.", exitCode, groupId);

        host.Close();
        Shutdown(exitCode);
    }

    /// <summary>
    /// Popup-mode entry point. Existing v0.3 startup path, factored out to keep
    /// <see cref="OnStartup"/> a thin dispatcher.
    /// </summary>
    private async Task RunPopupModeAsync(StartupEventArgs e, string groupId, bool aumidAlreadyInherited)
    {
        // Per-checkpoint Stopwatch timestamps so the launcher-*.log shows where the user's
        // perceived popup-open time goes. Two lines come out of it: "Startup timing" from
        // Loaded and "First render" from ContentRendered. GetTimestamp() costs ~10 ns per
        // call; negligible against the 100s of ms we are profiling.
        var tStart = Stopwatch.GetTimestamp();

        // Per-monitor DPI awareness must be set BEFORE the cursor capture below, not just
        // before HWND creation: the launcher ships without an app manifest, so the process
        // starts DPI-unaware and GetCursorPos would return virtualized 96-DPI coordinates
        // instead of the device pixels the ICursorAnchor contract (and the px→DIP
        // conversion in TaskbarPositionHelper) requires. The call costs microseconds, so
        // capturing the cursor immediately afterwards keeps the click location fresh.
        try
        {
            Interop.NativeMethods.SetProcessDpiAwarenessContext(
                Interop.NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
        }
        catch (System.EntryPointNotFoundException)
        {
            // Pre-1703 Windows — leave DPI awareness at the manifest default.
        }

        // Capture the cursor position before anything that can take 100+ ms. This is
        // the click location — by the time WPF has bootstrapped (~300–500 ms) the cursor has
        // typically drifted, which is why the v0.2 helper's late GetCursorPos call produced
        // visually-random popup placement.
        //
        // GetCursorPos can fail on restricted desktops / session 0. Out param is default-
        // initialised to (0, 0) on failure, which would silently anchor the popup at the
        // top-left of the primary monitor. Detect the failure and fall back to the screen
        // centre so the user sees the popup near the middle instead of jammed in a corner.
        // The anchor contract is device pixels (ICursorAnchor); SystemParameters reports
        // DIPs, so the fallback multiplies by the system DPI scale to stay in-contract.
        System.Windows.Point anchor;
        if (Interop.NativeMethods.GetCursorPos(out var clickPoint))
        {
            anchor = new System.Windows.Point(clickPoint.X, clickPoint.Y);
        }
        else
        {
            var systemScale = Interop.NativeMethods.GetDpiForSystem() / 96.0;
            anchor = new System.Windows.Point(
                System.Windows.SystemParameters.PrimaryScreenWidth / 2 * systemScale,
                System.Windows.SystemParameters.PrimaryScreenHeight / 2 * systemScale);
        }

        // Stamp the process AUMID BEFORE any window is created so Windows matches the
        // popup to the pinned tile (which carries the same AUMID via PKEY_AppUserModel_ID).
        // Skip when Windows already gave us the AUMID via process activation — see
        // RunPinModeAsync for the identity-drift rationale.
        if (!aumidAlreadyInherited)
        {
            _ = Interop.NativeMethods.SetCurrentProcessExplicitAppUserModelID(GroupAumid.For(groupId));
        }
        var tAumid = Stopwatch.GetTimestamp();

        var paths = new AppDataPathProvider();

        // Load settings BEFORE the DI container is built so the resolved AppSettings instance
        // can be registered as a singleton (v0.3 single-load pattern).
        var settings = await new JsonAppSettingsStore(paths).LoadAsync().ConfigureAwait(true);
        var tSettings = Stopwatch.GetTimestamp();

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddTaskbarFoldersFileLogging(paths.LogsDirectory, "launcher"));
        services.AddTaskbarFoldersLauncher(new LauncherOptions(groupId), paths);
        services.AddSingleton(settings);
#if DEBUG
        _services = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
#else
        _services = services.BuildServiceProvider();
#endif
        var tDi = Stopwatch.GetTimestamp();

        // Seed the cursor anchor BEFORE PopupViewModel/PopupWindow are resolved so any
        // placement lookup during the first paint sees a populated value.
        _services.GetRequiredService<ICursorAnchor>().Seed(anchor);

        var logger = _services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("Launcher starting for group {GroupId}.", groupId);

        // Apply the persisted theme before the window is built so DynamicResource bindings
        // paint correctly on the first frame.
        LauncherThemeApplier.Apply(this, settings.Theme);
        var tTheme = Stopwatch.GetTimestamp();

        // Two-phase load: metadata first (group name, columns, app names — ~5 ms) so the
        // window can paint immediately, then per-app icon extraction in the background.
        var viewModel = _services.GetRequiredService<PopupViewModel>();
        await viewModel.LoadAsync().ConfigureAwait(true);
        var tVm = Stopwatch.GetTimestamp();

        var popup = _services.GetRequiredService<Views.PopupWindow>();
        var tWindow = Stopwatch.GetTimestamp();
        MainWindow = popup;

        // Timing summary, emitted from the Loaded handler. `processAge` is the wall-clock
        // from Process.StartTime to the moment the handler runs — not to tStart. What
        // elapsed before RunPopupModeAsync was entered (host start-up, runtime init, WPF
        // assembly load) is therefore processAge minus the last checkpoint; the
        // ContentRendered line below reports it explicitly as `runtime`. Loaded is not a
        // paint: layout is done, nothing has been rendered yet.
        //
        // Subscribe BEFORE Show(): a Window raises Loaded synchronously inside Show(), so
        // a handler attached afterwards never runs (v0.4.1/v0.4.2 emitted no timing line
        // at all because of this). tShown is still captured after Show() returns; if
        // Loaded ran inside Show() the checkpoint falls back to tLoaded to stay monotonic.
        var processStart = Process.GetCurrentProcess().StartTime;
        long tPlaced = 0;
        long tShown = 0;
        long tActivated = 0;
        long tIcons = 0;

        // Attached after the constructor, so it runs after the window's own
        // SourceInitialized handler: the HWND exists and size + placement are computed.
        popup.SourceInitialized += (_, _) => tPlaced = Stopwatch.GetTimestamp();

        popup.Loaded += (_, _) =>
        {
            var tLoaded = Stopwatch.GetTimestamp();
            var processAge = (DateTime.Now - processStart).TotalMilliseconds;
            logger.LogInformation(
                "Startup timing (ms from tStart, processAge={ProcAge:F0}): aumid={Aumid:F0} settings={Settings:F0} di={Di:F0} theme={Theme:F0} vm={Vm:F0} window={Window:F0} placed={Placed:F0} show={Show:F0} loaded={Loaded:F0}",
                processAge,
                ToMs(tStart, tAumid),
                ToMs(tStart, tSettings),
                ToMs(tStart, tDi),
                ToMs(tStart, tTheme),
                ToMs(tStart, tVm),
                ToMs(tStart, tWindow),
                ToMs(tStart, tPlaced != 0 ? tPlaced : tLoaded),
                ToMs(tStart, tShown != 0 ? tShown : tLoaded),
                ToMs(tStart, tLoaded));
        };

        // Second line, kept separate so the Loaded line survives if this one never comes.
        // ContentRendered is queued at Input priority and so runs once the UI thread's
        // render queue has drained: it marks the first frame being handed to the render
        // thread, not its arrival on screen. The checkpoints between Show() and here are
        // the synchronous work that stands between Loaded and that first frame.
        popup.ContentRendered += (_, _) =>
        {
            var rendered = ToMs(tStart, Stopwatch.GetTimestamp());
            var processAge = (DateTime.Now - processStart).TotalMilliseconds;
            logger.LogInformation(
                "First render (ms from tStart, processAge={ProcAge:F0}, runtime={Runtime:F0}): shown={Shown:F0} activated={Activated:F0} icons={Icons:F0} rendered={Rendered:F0}",
                processAge,
                processAge - rendered,
                ToMs(tStart, tShown),
                ToMs(tStart, tActivated),
                ToMs(tStart, tIcons),
                rendered);
        };

        popup.Show();
        tShown = Stopwatch.GetTimestamp();

        // Explicitly request activation (parity with pin mode). Show() alone does not
        // reliably take foreground when Explorer still owns it from the tile click, and
        // dismiss-on-focus-loss depends on the popup having been activated once.
        popup.Activate();
        tActivated = Stopwatch.GetTimestamp();

        viewModel.StartIconLoad();
        tIcons = Stopwatch.GetTimestamp();

        // Deferred startup IO: prune stale icon-cache PNGs and old log files in the background
        // so the first paint of the popup is not blocked by ~10-70 ms of enumerate-and-delete.
        _services.GetRequiredService<IIconCache>().StartBackgroundPrune();
        _services.GetServices<ILoggerProvider>()
            .OfType<FileLoggerProvider>()
            .FirstOrDefault()?.StartBackgroundPrune();

        base.OnStartup(e);
    }

    private static double ToMs(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _services?.Dispose();
        _services = null;

        base.OnExit(e);
    }
}
