using System.Diagnostics;
using System.Drawing;
using FreiAtlas.Atlas;
using FreiAtlas.Atlas.Memory;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Settings;
using FreiAtlas.Game;
using FreiAtlas.Expedition;
using FreiAtlas.Platform.Windows.Overlay;
using FreiAtlas.Platform.Windows.Process;
using FreiAtlas.Platform.Windows.Windows;
using FreiAtlas.Plugin.AreaMap;
using FreiAtlas.Plugin.ExpeditionPanel;

namespace FreiAtlas.Host;

public sealed class AtlasOverlayRunner
{
    private readonly PricesRuntime? _prices;

    public AtlasOverlayRunner() { }

    public AtlasOverlayRunner(PricesRuntime prices)
    {
        _prices = prices ?? throw new ArgumentNullException(nameof(prices));
    }

    private static readonly TimeSpan SampleInterval =
        TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan FrameInterval =
        TimeSpan.FromMilliseconds(1000d / 30d);

    public int Run(int processId)
        => Run(
            processId,
            CancellationToken.None,
            DefaultAtlasSettingsSource.Instance);

    public int Run(
        int processId,
        CancellationToken cancellationToken,
        IAtlasSettingsSource settingsSource,
        Action<AtlasSnapshot>? snapshotObserver = null,
        OverlayStopController? stopController = null)
    {
        ArgumentNullException.ThrowIfNull(settingsSource);
        if (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        if (!ProcessAttachment.TryAttach(processId, out var attachment)
            || attachment is null)
        {
            Console.WriteLine($"pid={processId}");
            Console.WriteLine("overlay=unavailable");
            Console.WriteLine("reason=read-only-process-attach-failed");
            return 2;
        }

        using (attachment)
        {
            var windowTracker = new GameWindowTracker();
            var profile = AtlasLayoutProfile.Default;
            var probe = new LiveAtlasMemoryProbe();
            var source = new AtlasProcessReader(
                attachment.Memory,
                profile,
                probe);
            var provider = new AtlasUiTreeProvider(
                source,
                profile,
                () => GetViewport(windowTracker, processId));
            var service = new AtlasProviderService(
                provider,
                profile.RequiredStableSamples);

            var initialWindow = windowTracker.Track(processId);
            if (initialWindow is not
                {
                    ClientBounds.Width: > 0,
                    ClientBounds.Height: > 0
                })
            {
                Console.WriteLine($"pid={processId}");
                Console.WriteLine("overlay=unavailable");
                Console.WriteLine("reason=client-viewport-unavailable");
                return 2;
            }

            var areaProvider = new AreaMapProviderService(
                attachment.Memory,
                new AreaMapProviderOptions
                {
                    ClientViewport = new AreaUiRect(
                        0f,
                        0f,
                        initialWindow.ClientBounds.Width,
                        initialWindow.ClientBounds.Height)
                });
            using var ownedPrices = _prices is null ? new PricesRuntime(PriceSettings.Default) : null;
            var prices = _prices ?? ownedPrices!;
            ownedPrices?.Start();
            var framePriceBook = prices.CurrentBook;
            var expeditionValueProvider = new ExpeditionValueProvider(
                () => framePriceBook);
            var expeditionRecipeValueSource = new ExpeditionRecipeValueAdapter(
                () => framePriceBook);
            var areaPlugin = new AreaMapOverlayPlugin(
                areaProvider,
                AreaMapProjectionProfile.Verified,
                expeditionValueProvider);
            var expeditionPanelPlugin = new ExpeditionRecipePanelPlugin(
                areaProvider,
                expeditionRecipeValueSource);
            var expeditionNativeValuePlugin = new ExpeditionNativeValuePlugin(
                areaProvider,
                expeditionRecipeValueSource);

            WaitUntilStable(
                service,
                maxSamples: 12,
                sampleInterval: SampleInterval,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return 0;
            }

            using var targetProcess =
                System.Diagnostics.Process.GetProcessById(processId);
            using var atlasSurface = AtlasNameOverlaySurface.Create();
            using var areaSurface = AreaMapOverlaySurface.Create();
            var panelPositionPath = GetExpeditionPanelPositionPath();
            Point? panelPosition = ExpeditionPanelPositionStore.TryLoad(
                panelPositionPath);
            using var expeditionPanelSurface = ExpeditionPanelSurface.Create(
                panelPosition);
            using var nativeValueSurface = TryCreateNativeValueSurface();
            Action atlasRequestHide = atlasSurface.RequestHide;
            Action areaRequestHide = areaSurface.RequestHide;
            Action panelRequestHide = expeditionPanelSurface.RequestHide;
            Action? nativeValueRequestHide = nativeValueSurface is null
                ? null
                : nativeValueSurface.RequestHide;
            Action<Point> panelPositionChanged = position => panelPosition = position;
            expeditionPanelSurface.PanelToggleRequested +=
                expeditionPanelPlugin.TogglePanel;
            expeditionPanelSurface.EncounterToggleRequested +=
                expeditionPanelPlugin.ToggleEncounter;
            expeditionPanelSurface.PositionChanged += panelPositionChanged;
            stopController?.Register(atlasRequestHide);
            stopController?.Register(areaRequestHide);
            stopController?.Register(panelRequestHide);
            if (nativeValueRequestHide is not null)
            {
                stopController?.Register(nativeValueRequestHide);
            }
            try
            {
                using var cancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                areaProvider.SampleWorld();
                areaProvider.SampleRealtime();

                Console.WriteLine($"pid={processId}");
                Console.WriteLine("overlay=started");

                var atlasSampling = Task.Run(
                    () => SampleLoopAsync(
                        service,
                        snapshotObserver,
                        cancellation.Token));
                var areaSampling = areaProvider.StartAsync(cancellation.Token);
                return RunSamplingSession(
                    cancellation,
                    atlasSampling,
                    areaSampling,
                    (sessionCancellation, atlasTask, areaTask) => RunRenderLoop(
                        processId,
                        targetProcess,
                        windowTracker,
                        service,
                        atlasSurface,
                        areaProvider,
                        areaPlugin,
                        areaSurface,
                        expeditionPanelPlugin,
                        expeditionPanelSurface,
                        expeditionNativeValuePlugin,
                        nativeValueSurface,
                        sessionCancellation,
                        atlasTask,
                        areaTask,
                        settingsSource,
                        () => framePriceBook = prices.CurrentBook),
                    atlasSurface.Hide,
                    areaSurface.Hide,
                    expeditionPanelSurface.Hide,
                    nativeValueSurface is null
                        ? static () => { }
                        : nativeValueSurface.Hide);
            }
            finally
            {
                stopController?.Unregister(atlasRequestHide);
                stopController?.Unregister(areaRequestHide);
                stopController?.Unregister(panelRequestHide);
                if (nativeValueRequestHide is not null)
                {
                    stopController?.Unregister(nativeValueRequestHide);
                }
                expeditionPanelSurface.PanelToggleRequested -=
                    expeditionPanelPlugin.TogglePanel;
                expeditionPanelSurface.EncounterToggleRequested -=
                    expeditionPanelPlugin.ToggleEncounter;
                expeditionPanelSurface.PositionChanged -= panelPositionChanged;
                if (panelPosition is { } position)
                {
                    TrySaveExpeditionPanelPosition(panelPositionPath, position);
                }
            }
        }
    }

    private static string GetExpeditionPanelPositionPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreiAtlas",
            "expedition-panel-position.json");

    private static void TrySaveExpeditionPanelPosition(
        string path,
        Point position)
    {
        try
        {
            ExpeditionPanelPositionStore.Save(path, position);
        }
        catch (Exception exception) when (exception is IOException
                                         or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Expedition panel position could not be saved: {exception.Message}");
        }
    }

    internal static int RunSamplingSession(
        CancellationTokenSource cancellation,
        Task atlasSampling,
        Task areaSampling,
        Func<CancellationTokenSource, Task, Task, int> runRenderLoop,
        Action hideAtlasSurface,
        Action hideAreaSurface,
        Action hideExpeditionPanelSurface,
        Action hideNativeValueSurface)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(atlasSampling);
        ArgumentNullException.ThrowIfNull(areaSampling);
        ArgumentNullException.ThrowIfNull(runRenderLoop);
        ArgumentNullException.ThrowIfNull(hideAtlasSurface);
        ArgumentNullException.ThrowIfNull(hideAreaSurface);
        ArgumentNullException.ThrowIfNull(hideExpeditionPanelSurface);
        ArgumentNullException.ThrowIfNull(hideNativeValueSurface);

        try
        {
            return runRenderLoop(
                cancellation,
                atlasSampling,
                areaSampling);
        }
        finally
        {
            try
            {
                cancellation.Cancel();
            }
            finally
            {
                try
                {
                    HideAllSurfaces(
                        hideAtlasSurface,
                        hideAreaSurface,
                        hideExpeditionPanelSurface,
                        hideNativeValueSurface);
                }
                finally
                {
                    try
                    {
                        Task.WhenAll(atlasSampling, areaSampling)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (OperationCanceledException)
                        when (cancellation.IsCancellationRequested)
                    {
                    }
                }
            }
        }
    }

    internal static bool SuppressOverlays(
        bool isSuppressed,
        Action hideAtlasSurface,
        Action hideAreaSurface,
        Action hideExpeditionPanelSurface,
        Action hideNativeValueSurface)
    {
        if (!isSuppressed)
        {
            return false;
        }

        HideAllSurfaces(
            hideAtlasSurface,
            hideAreaSurface,
            hideExpeditionPanelSurface,
            hideNativeValueSurface);
        return true;
    }

    private static void HideAllSurfaces(
        Action hideAtlasSurface,
        Action hideAreaSurface,
        Action hideExpeditionPanelSurface,
        Action hideNativeValueSurface)
    {
        try
        {
            hideAtlasSurface();
        }
        finally
        {
            try
            {
                hideAreaSurface();
            }
            finally
            {
                try
                {
                    hideExpeditionPanelSurface();
                }
                finally
                {
                    hideNativeValueSurface();
                }
            }
        }
    }

    internal static void ObserveSamplingTasks(
        Task atlasSampling,
        Task areaSampling,
        CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(atlasSampling);
        ArgumentNullException.ThrowIfNull(areaSampling);
        ArgumentNullException.ThrowIfNull(cancellation);

        var atlasCompleted = atlasSampling.IsCompleted;
        var areaCompleted = areaSampling.IsCompleted;
        if (cancellation.IsCancellationRequested
            || (!atlasCompleted && !areaCompleted))
        {
            return;
        }

        cancellation.Cancel();
        if (atlasCompleted
            && (atlasSampling.IsFaulted || atlasSampling.IsCanceled))
        {
            atlasSampling.GetAwaiter().GetResult();
        }

        if (areaCompleted
            && (areaSampling.IsFaulted || areaSampling.IsCanceled))
        {
            areaSampling.GetAwaiter().GetResult();
        }

        var completedLoop = atlasCompleted
            ? "atlas"
            : "area";
        throw new InvalidOperationException(
            $"The {completedLoop} sampling loop ended unexpectedly.");
    }

    internal static bool TryUpdateAreaViewport(
        AreaMapProviderService areaProvider,
        GameWindowSnapshot? window)
    {
        ArgumentNullException.ThrowIfNull(areaProvider);
        if (window is not
            {
                Handle: not 0,
                IsMinimized: false,
                ClientBounds.Width: > 0,
                ClientBounds.Height: > 0
            })
        {
            return false;
        }

        return areaProvider.UpdateClientViewport(new AreaUiRect(
            0f,
            0f,
            window.ClientBounds.Width,
            window.ClientBounds.Height));
    }

    private static async Task SampleLoopAsync(
        AtlasProviderService service,
        Action<AtlasSnapshot>? snapshotObserver,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SampleAndObserve(service.Sample, snapshotObserver);

                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static int RunRenderLoop(
        int processId,
        System.Diagnostics.Process targetProcess,
        GameWindowTracker windowTracker,
        AtlasProviderService service,
        IAtlasOverlaySurface atlasSurface,
        AreaMapProviderService areaProvider,
        AreaMapOverlayPlugin areaPlugin,
        IAreaMapOverlaySurface areaSurface,
        ExpeditionRecipePanelPlugin expeditionPanelPlugin,
        IExpeditionPanelSurface expeditionPanelSurface,
        ExpeditionNativeValuePlugin expeditionNativeValuePlugin,
        IExpeditionNativeValueSurface? nativeValueSurface,
        CancellationTokenSource sessionCancellation,
        Task atlasSampling,
        Task areaSampling,
        IAtlasSettingsSource settingsSource,
        Action preparePriceFrame)
    {
        var cancellationToken = sessionCancellation.Token;
        AtlasLiveRenderGeometry? lastGeometry = null;
        var failedGeometryFrames = 0;
        var navigationState = new AtlasNavigationRenderState();
        var nativeValueSurfaceEnabled = nativeValueSurface is not null;

        void HideNativeSurface()
            => HideNativeValueSurface(
                nativeValueSurface,
                ref nativeValueSurfaceEnabled);

        Action hideAtlasSurface = atlasSurface.Hide;
        Action hideAreaSurface = areaSurface.Hide;
        Action hideExpeditionPanelSurface = expeditionPanelSurface.Hide;
        Action hideNativeValueSurface = HideNativeSurface;
        var altOverlaySuppressionController =
            new AltOverlaySuppressionController();

        while (!cancellationToken.IsCancellationRequested
               && !targetProcess.HasExited
               && atlasSurface.PumpMessages()
               && areaSurface.PumpMessages()
               && expeditionPanelSurface.PumpMessages()
               && PumpNativeValueSurface(
                   nativeValueSurface,
                   ref nativeValueSurfaceEnabled))
        {
            var startedAt = Stopwatch.GetTimestamp();
            preparePriceFrame();
            ObserveSamplingTasks(
                atlasSampling,
                areaSampling,
                sessionCancellation);
            var settings = settingsSource.Current;
            ArgumentNullException.ThrowIfNull(settings);
            var window = windowTracker.Track(processId);
            var isGameForeground = window is
            {
                IsForeground: true,
                IsMinimized: false
            };
            var isAltDown = AltKeyStateReader.IsDown();
            var shouldSuppress =
                altOverlaySuppressionController.ShouldSuppress(
                    settings.AltOverlayMode,
                    isAltDown,
                    isGameForeground);
            var recipePanel = areaProvider.Current.ExpeditionRecipePanel;
            var expeditionVisibility = ExpeditionOverlayVisibilityPolicy.Evaluate(
                settings.AreaMap.ExpeditionPanel,
                recipePanel);
            var expeditionPanelScene = expeditionPanelPlugin.Build(
                settings.AreaMap.ExpeditionPanel.ExpandOnAreaEntry,
                showPanel: !shouldSuppress && expeditionVisibility.ShowStandaloneSurface);
            if (SuppressOverlays(
                    shouldSuppress,
                    hideAtlasSurface,
                    hideAreaSurface,
                    hideExpeditionPanelSurface,
                    hideNativeValueSurface))
            {
                SleepRemaining(startedAt, FrameInterval, cancellationToken);
                continue;
            }

            TryUpdateAreaViewport(areaProvider, window);
            var snapshot = service.Current;
            AtlasLiveRenderGeometry? sampledGeometry = null;
            if (snapshot is
                {
                    Status: AtlasSnapshotStatus.Stable,
                    IsAtlasOpen: true
                })
            {
                var grids = snapshot.Nodes
                    .Select(node => node.Grid)
                    .ToHashSet();
                if (service.TryReadLiveGeometry(grids, out var geometry))
                {
                    lastGeometry = geometry;
                    sampledGeometry = geometry;
                    failedGeometryFrames = 0;
                }
                else
                {
                    failedGeometryFrames++;
                }
            }
            else
            {
                lastGeometry = null;
                failedGeometryFrames = 0;
            }

            var atlasActive = window is
                {
                    IsForeground: true,
                    IsMinimized: false,
                    ClientBounds.Width: > 0,
                    ClientBounds.Height: > 0
                }
                && snapshot is
                {
                    Status: AtlasSnapshotStatus.Stable,
                    IsAtlasOpen: true
                };
            var navigationGeometry = navigationState.ObserveGeometry(
                atlasActive,
                sampledGeometry);

            Point? screenMousePosition =
                CursorPositionReader.TryGetScreenPosition(out var cursorPosition)
                    ? cursorPosition
                    : null;
            AtlasOverlayFrame? frame = null;
            if (atlasActive
                && snapshot is not null
                && failedGeometryFrames <= 15
                && lastGeometry is { } live)
            {
                var navigationPlan = navigationState.ResolvePlan(
                    snapshot,
                    settings.Navigation);
                frame = AtlasOverlayCoordinator.Build(
                    window,
                    snapshot,
                    live,
                    settings,
                    navigationPlan,
                    navigationGeometry,
                    screenMousePosition);
            }
            if (frame is null)
            {
                atlasSurface.Hide();
            }
            else
            {
                atlasSurface.Render(
                    frame.ClientBounds,
                    frame.Edges,
                    frame.NavigationRouteEdges,
                    frame.Labels,
                    frame.ContentIcons,
                    frame.Directions,
                    settings);
            }

            var areaFrame = AreaMapOverlayCoordinator.Build(
                window,
                areaPlugin.Build(settings.AreaMap));
            if (areaFrame is null)
            {
                areaSurface.Hide();
            }
            else
            {
                areaSurface.Render(
                    areaFrame.ClientBounds,
                    areaFrame.Scene,
                    settings.AreaMap);
            }

            var validGameWindow = window is
            {
                IsForeground: true,
                IsMinimized: false,
                ClientBounds.Width: > 0,
                ClientBounds.Height: > 0
            };

            var nativeScene = expeditionVisibility.ShowNativeSurface
                ? expeditionNativeValuePlugin.Build()
                : null;
            if (!nativeValueSurfaceEnabled
                || nativeValueSurface is null
                || !validGameWindow
                || nativeScene is null)
            {
                HideNativeValueSurface(
                    nativeValueSurface,
                    ref nativeValueSurfaceEnabled);
            }
            else
            {
                RenderNativeValueSurface(
                    nativeValueSurface,
                    window!.ClientBounds,
                    nativeScene,
                    ref nativeValueSurfaceEnabled);
            }

            if (!validGameWindow || expeditionPanelScene is null)
            {
                expeditionPanelSurface.Hide();
            }
            else
            {
                expeditionPanelSurface.Render(
                    window!.ClientBounds,
                    expeditionPanelScene,
                    panelScale: 1f);
            }

            SleepRemaining(startedAt, FrameInterval, cancellationToken);
        }

        return 0;
    }

    internal static AtlasSnapshot SampleAndObserve(
        Func<AtlasSnapshot> sample,
        Action<AtlasSnapshot>? observer)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var snapshot = sample();
        observer?.Invoke(snapshot);
        return snapshot;
    }

    private static IExpeditionNativeValueSurface? TryCreateNativeValueSurface()
    {
        try
        {
            return ExpeditionNativeValueSurface.Create();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Native expedition value surface unavailable: {exception.Message}");
            return null;
        }
    }

    internal static bool PumpNativeValueSurface(
        IExpeditionNativeValueSurface? nativeValueSurface,
        ref bool enabled)
    {
        if (!enabled || nativeValueSurface is null)
        {
            return true;
        }

        try
        {
            return nativeValueSurface.PumpMessages();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Native expedition value surface disabled: {exception.Message}");
            enabled = false;
            TryHideNativeValueSurface(nativeValueSurface);
            return true;
        }
    }

    private static void RenderNativeValueSurface(
        IExpeditionNativeValueSurface nativeValueSurface,
        Rectangle gameClientBounds,
        ExpeditionNativeValueScene scene,
        ref bool enabled)
    {
        try
        {
            nativeValueSurface.Render(gameClientBounds, scene);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Native expedition value surface disabled: {exception.Message}");
            enabled = false;
            TryHideNativeValueSurface(nativeValueSurface);
        }
    }

    private static void HideNativeValueSurface(
        IExpeditionNativeValueSurface? nativeValueSurface,
        ref bool enabled)
    {
        if (!enabled || nativeValueSurface is null)
        {
            return;
        }

        try
        {
            nativeValueSurface.Hide();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Native expedition value surface disabled: {exception.Message}");
            enabled = false;
        }
    }

    private static void TryHideNativeValueSurface(
        IExpeditionNativeValueSurface nativeValueSurface)
    {
        try
        {
            nativeValueSurface.Hide();
        }
        catch (Exception hideException)
        {
            Console.Error.WriteLine(
                $"Native expedition value surface hide failed: {hideException.Message}");
        }
    }

    private static void SleepRemaining(
        long startedAt,
        TimeSpan frameInterval,
        CancellationToken cancellationToken)
    {
        var remaining = frameInterval - Stopwatch.GetElapsedTime(startedAt);
        if (remaining > TimeSpan.FromMilliseconds(1))
        {
            cancellationToken.WaitHandle.WaitOne(remaining);
        }
    }

    private static void WaitUntilStable(
        AtlasProviderService service,
        int maxSamples,
        TimeSpan sampleInterval,
        CancellationToken cancellationToken)
    {
        for (var sample = 0; sample < maxSamples; sample++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            service.Sample();
            if (service.Current is not null)
            {
                return;
            }

            if (sample + 1 < maxSamples
                && sampleInterval > TimeSpan.Zero
                && cancellationToken.WaitHandle.WaitOne(sampleInterval))
            {
                return;
            }
        }
    }

    private static AtlasViewport? GetViewport(
        GameWindowTracker tracker,
        int processId)
    {
        var clientBounds = tracker.Track(processId)?.ClientBounds ?? default;
        return clientBounds.Width > 0 && clientBounds.Height > 0
            ? new AtlasViewport(clientBounds.Width, clientBounds.Height)
            : null;
    }

    private sealed class DefaultAtlasSettingsSource : IAtlasSettingsSource
    {
        public static DefaultAtlasSettingsSource Instance { get; } = new();

        public AtlasDisplaySettings Current => AtlasDisplaySettings.Default;

        public event Action<AtlasDisplaySettings>? SettingsChanged
        {
            add { }
            remove { }
        }
    }
}
