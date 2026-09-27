using System.Text.Json;
using System.Text.Json.Serialization;
using FreiAtlas.Core.Settings;
using FreiAtlas.Core.Recovery;

namespace FreiAtlas.App.Settings;

public enum AtlasSettingsLoadStatus
{
    Defaults,
    Loaded,
    RecoveredInvalid
}

public enum AtlasSettingsPersistenceStatus
{
    Saving,
    Saved,
    SaveFailed
}

public sealed class AtlasSettingsStore : IAtlasSettingsSource, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly IReadOnlyList<string> _contentIds;
    private readonly TimeSpan _saveDelay;
    private readonly object _scheduleGate = new();
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _scheduledSaveCancellation;
    private Task _scheduledSave = Task.CompletedTask;
    private AtlasDisplaySettings _current;
    private AtlasHotkey _currentHotkey;
    private QuickAssistSettings _quickAssist = QuickAssistSettings.Default;
    private PriceSettings _prices = PriceSettings.Default;
    private long _currentGeneration;
    private long _persistedGeneration;
    private volatile bool _disposed;

    private AtlasSettingsStore(
        string filePath,
        IReadOnlyList<string> contentIds,
        TimeSpan saveDelay,
        AtlasDisplaySettings current,
        AtlasHotkey currentHotkey,
        AtlasSettingsLoadStatus loadStatus)
    {
        _filePath = filePath;
        _contentIds = contentIds;
        _saveDelay = saveDelay;
        _current = current;
        _currentHotkey = currentHotkey;
        LoadStatus = loadStatus;
    }

    public AtlasDisplaySettings Current => Volatile.Read(ref _current);

    public AtlasHotkey CurrentHotkey => Volatile.Read(ref _currentHotkey);

    public QuickAssistSettings QuickAssist => Volatile.Read(ref _quickAssist);

    public event Action<QuickAssistSettings>? QuickAssistChanged;

    public PriceSettings Prices => Volatile.Read(ref _prices);

    public event Action<PriceSettings>? PricesChanged;

    public AtlasSettingsLoadStatus LoadStatus { get; }

    public event Action<AtlasDisplaySettings>? SettingsChanged;

    public event Action<AtlasHotkey>? HotkeyChanged;

    public event Action<AtlasSettingsPersistenceStatus>? PersistenceChanged;

    public static async Task<AtlasSettingsStore> CreateAsync(
        string filePath,
        IEnumerable<string> contentIds,
        TimeSpan? saveDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(contentIds);

        var stableContentIds = contentIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var defaults = AtlasDisplaySettingsValidator.Validate(
            AtlasDisplaySettings.Default,
            stableContentIds);

        if (!File.Exists(filePath))
        {
            return new AtlasSettingsStore(
                filePath,
                stableContentIds,
                saveDelay ?? TimeSpan.FromMilliseconds(250),
                defaults,
                AtlasHotkey.Default,
                AtlasSettingsLoadStatus.Defaults);
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var document = await JsonSerializer.DeserializeAsync<AtlasSettingsDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                ?? throw new InvalidDataException("The settings document is empty.");
            var settings = Merge(document, defaults, stableContentIds);
            var quickAssist = document.QuickAssist ?? QuickAssistSettings.Default;
            quickAssist.Validate();
            var prices = document.Prices ?? PriceSettings.Default;
            prices.Validate();
            var hotkey = AtlasHotkey.TryParse(
                document.OverlayToggleHotkey,
                out var savedHotkey)
                ? savedHotkey!
                : AtlasHotkey.Default;
            ValidateRecoveryHotkey(quickAssist, hotkey);
            return new AtlasSettingsStore(
                filePath,
                stableContentIds,
                saveDelay ?? TimeSpan.FromMilliseconds(250),
                settings,
                hotkey,
                AtlasSettingsLoadStatus.Loaded) { _quickAssist = quickAssist, _prices = prices };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or InvalidDataException)
        {
            var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
            var invalidName = $"settings.invalid.{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
            var invalidPath = Path.Combine(directory, invalidName);
            File.Move(filePath, invalidPath);
            return new AtlasSettingsStore(
                filePath,
                stableContentIds,
                saveDelay ?? TimeSpan.FromMilliseconds(250),
                defaults,
                AtlasHotkey.Default,
                AtlasSettingsLoadStatus.RecoveredInvalid);
        }
    }

    public void Update(AtlasDisplaySettings settings)
    {
        var validated = AtlasDisplaySettingsValidator.Validate(settings, _contentIds);
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _current, validated);
            _currentGeneration++;
            ScheduleSave();
        }

        SettingsChanged?.Invoke(validated);
        PersistenceChanged?.Invoke(AtlasSettingsPersistenceStatus.Saving);
    }

    public void UpdateHotkey(AtlasHotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        var validated = AtlasHotkey.Parse(hotkey.Gesture);
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateRecoveryHotkey(_quickAssist, validated);
            Volatile.Write(ref _currentHotkey, validated);
            _currentGeneration++;
            ScheduleSave();
        }

        HotkeyChanged?.Invoke(validated);
        PersistenceChanged?.Invoke(AtlasSettingsPersistenceStatus.Saving);
    }

    public void UpdateQuickAssist(QuickAssistSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ValidateRecoveryHotkey(settings, _currentHotkey);
            Volatile.Write(ref _quickAssist, settings);
            _currentGeneration++;
            ScheduleSave();
        }
        QuickAssistChanged?.Invoke(settings);
        PersistenceChanged?.Invoke(AtlasSettingsPersistenceStatus.Saving);
    }

    public void UpdatePrices(PriceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Volatile.Write(ref _prices, settings);
            _currentGeneration++;
            ScheduleSave();
        }
        PricesChanged?.Invoke(settings);
        PersistenceChanged?.Invoke(AtlasSettingsPersistenceStatus.Saving);
    }

    private static void ValidateRecoveryHotkey(QuickAssistSettings settings, AtlasHotkey hotkey)
    {
        var recoveryKeys = new List<(string Name, RecoveryKey Key)>();
        foreach (var (name, rule) in new[]
        {
            ("生命恢复", settings.Health),
            ("魔力恢复", settings.Mana)
        })
        {
            if (RecoveryKey.TryParse(rule.Key, out var parsed) && parsed is not null)
            {
                if (AreEquivalent(parsed, hotkey))
                {
                    throw new ArgumentException(
                        $"{name}按键与覆盖层快捷键 {hotkey.Gesture} 冲突，请选择其他按键。");
                }

                recoveryKeys.Add((name, parsed));
            }
        }

        if (RecoveryKey.TryParse(settings.PortalSqueeze.Hotkey, out var portal)
            && portal is not null)
        {
            if (AreEquivalent(portal, hotkey))
            {
                throw new ArgumentException(
                    $"挤门按键与覆盖层快捷键 {hotkey.Gesture} 冲突，请选择其他按键。");
            }

            foreach (var (name, key) in recoveryKeys)
            {
                if (AreEquivalent(portal, key))
                {
                    throw new ArgumentException(
                        $"挤门按键与{name}按键冲突，请选择其他按键。");
                }
            }
        }
    }

    private static bool AreEquivalent(RecoveryKey left, RecoveryKey right)
        => left.VirtualKey == right.VirtualKey
           && left.Modifiers.Order().SequenceEqual(right.Modifiers.Order());

    private static bool AreEquivalent(RecoveryKey recovery, AtlasHotkey overlay)
    {
        var virtualKey = overlay.Key.StartsWith('F')
            && int.TryParse(overlay.Key.AsSpan(1), out var functionKey)
            ? (ushort)(0x6F + functionKey)
            : overlay.Key.Length == 1
                ? overlay.Key[0]
                : (ushort)0;
        if (virtualKey == 0 || recovery.VirtualKey != virtualKey)
        {
            return false;
        }

        var modifiers = new List<ushort>();
        if ((overlay.Modifiers & AtlasHotkeyModifiers.Control) != 0) modifiers.Add(0x11);
        if ((overlay.Modifiers & AtlasHotkeyModifiers.Alt) != 0) modifiers.Add(0x12);
        if ((overlay.Modifiers & AtlasHotkeyModifiers.Shift) != 0) modifiers.Add(0x10);
        return recovery.Modifiers.Order().SequenceEqual(modifiers.Order());
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancelScheduledSave();
        while (HasUnsavedSettings())
        {
            await SaveAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        CancelScheduledSave();
        try
        {
            if (!_scheduledSave.IsCompleted)
            {
                await _scheduledSave;
            }
        }
        catch (OperationCanceledException)
        {
        }

        while (HasUnsavedSettings())
        {
            await SaveAsync(CancellationToken.None);
        }

        _scheduledSaveCancellation?.Dispose();
        _saveGate.Dispose();
    }

    private static AtlasDisplaySettings Merge(
        AtlasSettingsDocument document,
        AtlasDisplaySettings defaults,
        IReadOnlyList<string> contentIds)
    {
        var display = document.Display;
        var merged = defaults with
        {
            SchemaVersion = document.SchemaVersion ?? defaults.SchemaVersion,
            Theme = document.Theme ?? defaults.Theme,
            AltOverlayMode = document.AltOverlayMode ?? defaults.AltOverlayMode,
            Nodes = MergeNodes(display?.Nodes, defaults.Nodes),
            ContentVisibility = display?.ContentVisibility ?? defaults.ContentVisibility,
            Edges = MergeEdges(display?.Edges, defaults.Edges),
            Highlight = MergeHighlight(display?.Highlight, defaults.Highlight),
            Labels = MergeLabels(display?.Labels, defaults.Labels),
            AreaMap = MergeAreaMap(display?.AreaMap, defaults.AreaMap),
            Navigation = MergeNavigation(
                display?.Navigation,
                defaults.Navigation)
        };
        return AtlasDisplaySettingsValidator.Validate(merged, contentIds);
    }

    private static IReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility> MergeNodes(
        IReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibilityDocument>? saved,
        IReadOnlyDictionary<AtlasNodeCategory, AtlasNodeCategoryVisibility> defaults)
        => defaults.ToDictionary(
            pair => pair.Key,
            pair => saved is not null && saved.TryGetValue(pair.Key, out var value)
                ? new AtlasNodeCategoryVisibility(
                    value.ShowMapNames ?? pair.Value.ShowMapNames,
                    value.ShowConnections ?? pair.Value.ShowConnections)
                {
                    ShowMapContents = value.ShowMapContents ?? pair.Value.ShowMapContents
                }
                : pair.Value);

    private static AtlasEdgeStyle MergeEdges(
        AtlasEdgeStyleDocument? saved,
        AtlasEdgeStyle defaults)
        => new(
            saved?.Width ?? defaults.Width,
            saved?.Opacity ?? defaults.Opacity,
            saved?.ReachableColor ?? defaults.ReachableColor,
            saved?.LockedColor ?? defaults.LockedColor);

    private static AtlasHighlightStyle MergeHighlight(
        AtlasHighlightStyleDocument? saved,
        AtlasHighlightStyle defaults)
        => new(
            saved?.Width ?? defaults.Width,
            saved?.Opacity ?? defaults.Opacity,
            saved?.Color ?? defaults.Color);

    private static AtlasLabelStyle MergeLabels(
        AtlasLabelStyleDocument? saved,
        AtlasLabelStyle defaults)
        => new(
            saved?.FontSize ?? defaults.FontSize,
            saved?.TextColor ?? defaults.TextColor,
            saved?.ShowBackground ?? defaults.ShowBackground,
            saved?.BackgroundColor ?? defaults.BackgroundColor,
            saved?.BackgroundOpacity ?? defaults.BackgroundOpacity);

    private static AreaMapDisplaySettings MergeAreaMap(
        AreaMapDisplaySettingsDocument? saved,
        AreaMapDisplaySettings defaults)
        => new(
            saved?.ShowExpedition ?? defaults.ShowExpedition,
            saved?.ShowBoss ?? defaults.ShowBoss,
            saved?.LargeMapLabelFontSize ?? defaults.LargeMapLabelFontSize)
        {
            ShowAbyss = saved?.ShowAbyss ?? defaults.ShowAbyss,
            ShowRitual = saved?.ShowRitual ?? defaults.ShowRitual,
            ShowBreach = saved?.ShowBreach ?? defaults.ShowBreach,
            ShowEssence = saved?.ShowEssence ?? defaults.ShowEssence,
            ShowIncursion = saved?.ShowIncursion ?? defaults.ShowIncursion,
            ShowStrongbox = saved?.ShowStrongbox ?? defaults.ShowStrongbox,
            ShowRareMonster = saved?.ShowRareMonster ?? defaults.ShowRareMonster,
            ShowRareChests = saved?.ShowRareChests ?? defaults.ShowRareChests,
            ShowPollen = saved?.ShowPollen ?? defaults.ShowPollen,
            ShowOmenAltar = saved?.ShowOmenAltar ?? defaults.ShowOmenAltar,
            ExpeditionTag = new AreaMapExpeditionTagStyle(
                saved?.ExpeditionTag?.BackgroundColor
                    ?? defaults.ExpeditionTag.BackgroundColor,
                saved?.ExpeditionTag?.BackgroundOpacity
                    ?? defaults.ExpeditionTag.BackgroundOpacity,
                saved?.ExpeditionTag?.BorderOpacity
                    ?? defaults.ExpeditionTag.BorderOpacity),
            ExpeditionPanel = new AreaMapExpeditionPanelSettings(
                saved?.ExpeditionPanel?.ShowNativeRecipeValues
                    ?? defaults.ExpeditionPanel.ShowNativeRecipeValues,
                saved?.ExpeditionPanel?.AutoHideStandalonePanel
                    ?? defaults.ExpeditionPanel.AutoHideStandalonePanel)
            {
                ExpandOnAreaEntry = saved?.ExpeditionPanel?.ExpandOnAreaEntry
                    ?? defaults.ExpeditionPanel.ExpandOnAreaEntry
            }
        };

    private static AtlasNavigationSettings MergeNavigation(
        AtlasNavigationSettingsDocument? saved,
        AtlasNavigationSettings defaults)
        => saved is null
            ? defaults
            : new AtlasNavigationSettings(
                saved.TargetMode ?? defaults.TargetMode,
                saved.Rules?.ToDictionary(
                    pair => pair.Key,
                    pair => new AtlasNavigationRule(
                        pair.Value?.Highlight ?? false,
                        pair.Value?.Route ?? false,
                        pair.Value?.Direction ?? false),
                    StringComparer.Ordinal)
                ?? new Dictionary<string, AtlasNavigationRule>(
                    StringComparer.OrdinalIgnoreCase),
                saved.HideCompletedMaps ?? defaults.HideCompletedMaps);

    private void ScheduleSave()
    {
        CancellationTokenSource cancellation;
        lock (_scheduleGate)
        {
            _scheduledSaveCancellation?.Cancel();
            _scheduledSaveCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _scheduledSaveCancellation = cancellation;
            _scheduledSave = SaveAfterDelayAsync(cancellation.Token);
        }

    }

    private void CancelScheduledSave()
    {
        lock (_scheduleGate)
        {
            _scheduledSaveCancellation?.Cancel();
        }
    }

    private async Task SaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_saveDelay, cancellationToken);
            await SaveAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            AtlasDisplaySettings settings;
            AtlasHotkey hotkey;
            QuickAssistSettings quickAssist;
            PriceSettings prices;
            long generation;
            lock (_stateGate)
            {
                if (_persistedGeneration >= _currentGeneration)
                {
                    return;
                }

                settings = _current;
                hotkey = _currentHotkey;
                quickAssist = _quickAssist;
                prices = _prices;
                generation = _currentGeneration;
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = _filePath + ".tmp";
            var document = new AtlasSettingsDocument
            {
                SchemaVersion = settings.SchemaVersion,
                Theme = settings.Theme,
                AltOverlayMode = settings.AltOverlayMode,
                OverlayToggleHotkey = hotkey.Gesture,
                QuickAssist = quickAssist,
                Prices = prices,
                Display = new AtlasDisplayDocument
                {
                    Nodes = settings.Nodes.ToDictionary(
                        pair => pair.Key,
                        pair => new AtlasNodeCategoryVisibilityDocument
                        {
                            ShowMapNames = pair.Value.ShowMapNames,
                            ShowConnections = pair.Value.ShowConnections,
                            ShowMapContents = pair.Value.ShowMapContents
                        }),
                    ContentVisibility = new Dictionary<string, bool>(settings.ContentVisibility, StringComparer.Ordinal),
                    Edges = new AtlasEdgeStyleDocument
                    {
                        Width = settings.Edges.Width,
                        Opacity = settings.Edges.Opacity,
                        ReachableColor = settings.Edges.ReachableColor,
                        LockedColor = settings.Edges.LockedColor
                    },
                    Highlight = new AtlasHighlightStyleDocument
                    {
                        Width = settings.Highlight.Width,
                        Opacity = settings.Highlight.Opacity,
                        Color = settings.Highlight.Color
                    },
                    Labels = new AtlasLabelStyleDocument
                    {
                        FontSize = settings.Labels.FontSize,
                        TextColor = settings.Labels.TextColor,
                        ShowBackground = settings.Labels.ShowBackground,
                        BackgroundColor = settings.Labels.BackgroundColor,
                        BackgroundOpacity = settings.Labels.BackgroundOpacity
                    },
                    AreaMap = new AreaMapDisplaySettingsDocument
                    {
                        ShowExpedition = settings.AreaMap.ShowExpedition,
                        ShowBoss = settings.AreaMap.ShowBoss,
                        ShowAbyss = settings.AreaMap.ShowAbyss,
                        ShowRitual = settings.AreaMap.ShowRitual,
                        ShowBreach = settings.AreaMap.ShowBreach,
                        ShowEssence = settings.AreaMap.ShowEssence,
                        ShowIncursion = settings.AreaMap.ShowIncursion,
                        ShowStrongbox = settings.AreaMap.ShowStrongbox,
                        ShowRareMonster = settings.AreaMap.ShowRareMonster,
                        ShowRareChests = settings.AreaMap.ShowRareChests,
                        ShowPollen = settings.AreaMap.ShowPollen,
                        ShowOmenAltar = settings.AreaMap.ShowOmenAltar,
                        LargeMapLabelFontSize = settings.AreaMap.LargeMapLabelFontSize,
                        ExpeditionTag = new AreaMapExpeditionTagStyleDocument
                        {
                            BackgroundColor = settings.AreaMap.ExpeditionTag.BackgroundColor,
                            BackgroundOpacity = settings.AreaMap.ExpeditionTag.BackgroundOpacity,
                            BorderOpacity = settings.AreaMap.ExpeditionTag.BorderOpacity
                        },
                        ExpeditionPanel = new AreaMapExpeditionPanelSettingsDocument
                        {
                            ExpandOnAreaEntry = settings.AreaMap.ExpeditionPanel.ExpandOnAreaEntry,
                            ShowNativeRecipeValues =
                                settings.AreaMap.ExpeditionPanel.ShowNativeRecipeValues,
                            AutoHideStandalonePanel =
                                settings.AreaMap.ExpeditionPanel.AutoHideStandalonePanel
                        }
                    },
                    Navigation = new AtlasNavigationSettingsDocument
                    {
                        TargetMode = settings.Navigation.TargetMode,
                        HideCompletedMaps = settings.Navigation.HideCompletedMaps,
                        Rules = settings.Navigation.Rules.ToDictionary(
                            pair => pair.Key,
                            pair => new AtlasNavigationRuleDocument
                            {
                                Highlight = pair.Value.Highlight,
                                Route = pair.Value.Route,
                                Direction = pair.Value.Direction
                            },
                            StringComparer.OrdinalIgnoreCase)
                    }
                }
            };

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
            lock (_stateGate)
            {
                _persistedGeneration = Math.Max(
                    _persistedGeneration,
                    generation);
            }

            PersistenceChanged?.Invoke(AtlasSettingsPersistenceStatus.Saved);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            PersistenceChanged?.Invoke(AtlasSettingsPersistenceStatus.SaveFailed);
            throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private bool HasUnsavedSettings()
    {
        lock (_stateGate)
        {
            return _persistedGeneration < _currentGeneration;
        }
    }
}
