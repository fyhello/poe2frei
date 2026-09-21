using System.Text.Json;
using FreiAtlas.App.Content;
using FreiAtlas.App.Settings;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.App.Tests;

public sealed class AtlasSettingsStoreTests
{
    [Fact]
    public async Task CreateAsync_WithNoFile_UsesAllCatalogDefaults()
    {
        using var directory = new TemporaryDirectory();
        await using var store = await AtlasSettingsStore.CreateAsync(
            directory.File("settings.json"),
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasSettingsLoadStatus.Defaults, store.LoadStatus);
        Assert.Equal(66, store.Current.ContentVisibility.Count);
        Assert.All(store.Current.ContentVisibility.Values, Assert.True);
    }

    [Fact]
    public async Task Update_PublishesImmediatelyBeforeDebouncedSave()
    {
        using var directory = new TemporaryDirectory();
        await using var store = await AtlasSettingsStore.CreateAsync(
            directory.File("settings.json"),
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromSeconds(5));
        AtlasDisplaySettings? published = null;
        store.SettingsChanged += settings => published = settings;
        var updated = store.Current with { Theme = AtlasTheme.Light };

        store.Update(updated);

        Assert.Same(store.Current, published);
        Assert.Equal(AtlasTheme.Light, store.Current.Theme);
        Assert.False(File.Exists(directory.File("settings.json")));
    }

    [Fact]
    public async Task ConsecutiveUpdates_AreCoalescedIntoOneSave()
    {
        using var directory = new TemporaryDirectory();
        await using var store = await AtlasSettingsStore.CreateAsync(
            directory.File("settings.json"),
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(60));
        var savedCount = 0;
        store.PersistenceChanged += state =>
        {
            if (state == AtlasSettingsPersistenceStatus.Saved)
            {
                Interlocked.Increment(ref savedCount);
            }
        };

        store.Update(store.Current with { Theme = AtlasTheme.Light });
        store.Update(store.Current with { Theme = AtlasTheme.Dark });
        store.Update(store.Current with { Theme = AtlasTheme.Light });
        await store.FlushAsync();

        Assert.Equal(1, Volatile.Read(ref savedCount));
        Assert.True(File.Exists(directory.File("settings.json")));
        Assert.False(File.Exists(directory.File("settings.json.tmp")));
    }

    [Fact]
    public async Task CreateAsync_WithInvalidJson_QuarantinesFileAndUsesDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(path, "{not-json");

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasSettingsLoadStatus.RecoveredInvalid, store.LoadStatus);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory.Path, "settings.invalid.*.json"));
        Assert.Equal(AtlasTheme.Dark, store.Current.Theme);
    }

    [Fact]
    public async Task CreateAsync_WithPartialOldFile_MergesMissingContentIds()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "theme": "Light",
              "display": {
                "contentVisibility": {
                  "AtlasIconContentMapBoss": false
                }
              }
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasSettingsLoadStatus.Loaded, store.LoadStatus);
        Assert.Equal(AtlasTheme.Light, store.Current.Theme);
        Assert.False(store.Current.ContentVisibility["AtlasIconContentMapBoss"]);
        Assert.True(store.Current.ContentVisibility["AtlasIconContentExpedition"]);
        Assert.Equal(66, store.Current.ContentVisibility.Count);
    }

    [Fact]
    public async Task CreateAsync_WithOldFile_DefaultsAltOverlayMode()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "theme": "Light"
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AltOverlayMode.HoldToHide, store.Current.AltOverlayMode);
    }

    [Theory]
    [InlineData(AltOverlayMode.HoldToHide)]
    [InlineData(AltOverlayMode.ToggleOnPress)]
    public async Task AltOverlayMode_PersistsAndReloads(AltOverlayMode mode)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await using (var store = await AtlasSettingsStore.CreateAsync(
                         path,
                         AtlasContentCatalog.Embedded.ContentIds,
                         TimeSpan.FromSeconds(5)))
        {
            store.Update(store.Current with { AltOverlayMode = mode });
            await store.FlushAsync();
        }

        var json = await File.ReadAllTextAsync(path);
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(
                mode.ToString(),
                document.RootElement.GetProperty("altOverlayMode").GetString());
        }

        await using var reloaded = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(mode, reloaded.Current.AltOverlayMode);
    }

    [Fact]
    public async Task CreateAsync_WithOldFile_DefaultsAreaMapSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "display": {
                "labels": { "fontSize": 20 }
              }
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasDisplaySettings.Default.AreaMap, store.Current.AreaMap);
    }

    [Fact]
    public async Task CreateAsync_WithOldFile_DefaultsNavigationSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "display": {
                "labels": { "fontSize": 20 }
              }
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            AtlasNavigationSettings.Default.TargetMode,
            store.Current.Navigation.TargetMode);
        Assert.True(store.Current.Navigation.HideCompletedMaps);
        Assert.Empty(store.Current.Navigation.Rules);
    }

    [Fact]
    public async Task NavigationSettings_PersistAndReload()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await using (var store = await AtlasSettingsStore.CreateAsync(
                         path,
                         AtlasContentCatalog.Embedded.ContentIds,
                         TimeSpan.FromSeconds(5)))
        {
            store.Update(store.Current with
            {
                Navigation = new AtlasNavigationSettings(
                    AtlasNavigationTargetMode.All,
                    new Dictionary<string, AtlasNavigationRule>
                    {
                        [" Dunes "] = new(true, false, true),
                        ["Mesa"] = new(false, true, false),
                        ["Inactive"] = new(false, false, false)
                    },
                    HideCompletedMaps: false)
            });
            await store.FlushAsync();
        }

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            var navigation = document.RootElement
                .GetProperty("display")
                .GetProperty("navigation");
            Assert.Equal("All", navigation.GetProperty("targetMode").GetString());
            Assert.False(navigation.GetProperty("hideCompletedMaps").GetBoolean());
            var rules = navigation.GetProperty("rules");
            Assert.Equal(2, rules.EnumerateObject().Count());
            Assert.True(rules.GetProperty("Dunes").GetProperty("highlight").GetBoolean());
            Assert.True(rules.GetProperty("Dunes").GetProperty("direction").GetBoolean());
            Assert.True(rules.GetProperty("Mesa").GetProperty("route").GetBoolean());
            Assert.False(rules.TryGetProperty("Inactive", out _));
        }

        await using var reloaded = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasNavigationTargetMode.All, reloaded.Current.Navigation.TargetMode);
        Assert.False(reloaded.Current.Navigation.HideCompletedMaps);
        Assert.Equal(2, reloaded.Current.Navigation.Rules.Count);
        Assert.Equal(
            new AtlasNavigationRule(true, false, true),
            reloaded.Current.Navigation.Rules["dunes"]);
        Assert.Equal(
            new AtlasNavigationRule(false, true, false),
            reloaded.Current.Navigation.Rules["MESA"]);
    }

    [Theory]
    [InlineData("showExpedition", false, false, true, 13f)]
    [InlineData("showBoss", false, true, false, 13f)]
    [InlineData("largeMapLabelFontSize", 21f, true, true, 21f)]
    public async Task CreateAsync_WithOneAreaMapField_MergesMissingDefaults(
        string fieldName,
        object savedValue,
        bool expectedShowExpedition,
        bool expectedShowBoss,
        float expectedLargeMapLabelFontSize)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var document = new
        {
            schemaVersion = 1,
            display = new
            {
                areaMap = new Dictionary<string, object>
                {
                    [fieldName] = savedValue
                }
            }
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(document));

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            new AreaMapDisplaySettings(
                expectedShowExpedition,
                expectedShowBoss,
                expectedLargeMapLabelFontSize),
            store.Current.AreaMap);
    }

    [Fact]
    public async Task AreaMapSettings_PersistAndReload()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var expectedAreaMap = new AreaMapDisplaySettings(
            ShowExpedition: false,
            ShowBoss: false,
            LargeMapLabelFontSize: 21f)
        {
            ShowAbyss = false,
            ShowRitual = false,
            ShowBreach = false,
            ShowEssence = false,
            ShowIncursion = false,
            ShowStrongbox = false,
            ShowRareMonster = false,
            ShowRareChests = false,
            ExpeditionTag = new AreaMapExpeditionTagStyle(
                "#123456",
                0.42f,
                0.73f),
            ExpeditionPanel = new AreaMapExpeditionPanelSettings(
                ShowNativeRecipeValues: false,
                AutoHideStandalonePanel: true) { ExpandOnAreaEntry = false }
        };
        await using (var store = await AtlasSettingsStore.CreateAsync(
                         path,
                         AtlasContentCatalog.Embedded.ContentIds,
                         TimeSpan.FromSeconds(5)))
        {
            store.Update(store.Current with
            {
                AreaMap = expectedAreaMap
            });
            await store.FlushAsync();
        }

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
            var areaMap = document.RootElement
                .GetProperty("display")
                .GetProperty("areaMap");
            Assert.False(areaMap.GetProperty("showExpedition").GetBoolean());
            Assert.False(areaMap.GetProperty("showBoss").GetBoolean());
            Assert.False(areaMap.GetProperty("showAbyss").GetBoolean());
            Assert.False(areaMap.GetProperty("showRitual").GetBoolean());
            Assert.False(areaMap.GetProperty("showBreach").GetBoolean());
            Assert.False(areaMap.GetProperty("showEssence").GetBoolean());
            Assert.False(areaMap.GetProperty("showIncursion").GetBoolean());
            Assert.False(areaMap.GetProperty("showStrongbox").GetBoolean());
            Assert.False(areaMap.GetProperty("showRareMonster").GetBoolean());
            Assert.False(areaMap.GetProperty("showRareChests").GetBoolean());
            Assert.Equal(21f, areaMap.GetProperty("largeMapLabelFontSize").GetSingle());
            var expeditionTag = areaMap.GetProperty("expeditionTag");
            Assert.Equal(
                "#123456",
                expeditionTag.GetProperty("backgroundColor").GetString());
            Assert.Equal(
                0.42f,
                expeditionTag.GetProperty("backgroundOpacity").GetSingle());
            Assert.Equal(
                0.73f,
                expeditionTag.GetProperty("borderOpacity").GetSingle());
            var expeditionPanel = areaMap.GetProperty("expeditionPanel");
            Assert.False(expeditionPanel.GetProperty("expandOnAreaEntry").GetBoolean());
            Assert.False(
                expeditionPanel.GetProperty("showNativeRecipeValues").GetBoolean());
            Assert.True(
                expeditionPanel.GetProperty("autoHideStandalonePanel").GetBoolean());
        }

        await using var reloaded = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(expectedAreaMap, reloaded.Current.AreaMap);
    }

    [Fact]
    public async Task CreateAsync_WithLegacyAreaMapSettings_UsesExpeditionDefaults()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "display": {
                "areaMap": {
                  "showExpedition": false,
                  "showBoss": true,
                  "largeMapLabelFontSize": 21
                }
              }
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            AreaMapExpeditionTagStyle.Default,
            store.Current.AreaMap.ExpeditionTag);
        Assert.Equal(
            AreaMapExpeditionPanelSettings.Default,
            store.Current.AreaMap.ExpeditionPanel);
        Assert.False(store.Current.AreaMap.ShowExpedition);
        Assert.True(store.Current.AreaMap.ShowAbyss);
        Assert.True(store.Current.AreaMap.ShowRitual);
        Assert.True(store.Current.AreaMap.ShowBreach);
        Assert.True(store.Current.AreaMap.ShowEssence);
        Assert.True(store.Current.AreaMap.ShowIncursion);
        Assert.True(store.Current.AreaMap.ShowStrongbox);
        Assert.True(store.Current.AreaMap.ShowRareMonster);
        Assert.True(store.Current.AreaMap.ShowRareChests);
        Assert.True(store.Current.AreaMap.ExpeditionPanel.ShowNativeRecipeValues);
        Assert.True(store.Current.AreaMap.ExpeditionPanel.AutoHideStandalonePanel);
        Assert.True(store.Current.AreaMap.ExpeditionPanel.ExpandOnAreaEntry);
    }

    [Theory]
    [InlineData("showNativeRecipeValues", false, false, true)]
    [InlineData("autoHideStandalonePanel", false, true, false)]
    [InlineData("expandOnAreaEntry", false, true, true, false)]
    public async Task CreateAsync_WithOneExpeditionPanelField_MergesOtherDefault(
        string fieldName,
        bool savedValue,
        bool expectedShowNativeRecipeValues,
        bool expectedAutoHideStandalonePanel,
        bool expectedExpandOnAreaEntry = true)
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var document = new
        {
            schemaVersion = 1,
            display = new
            {
                areaMap = new
                {
                    expeditionPanel = new Dictionary<string, bool>
                    {
                        [fieldName] = savedValue
                    }
                }
            }
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document));

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            new AreaMapExpeditionPanelSettings(
                expectedShowNativeRecipeValues,
                expectedAutoHideStandalonePanel) { ExpandOnAreaEntry = expectedExpandOnAreaEntry },
            store.Current.AreaMap.ExpeditionPanel);
    }

    [Fact]
    public async Task FlushAsync_SerializesOnlySettingsData()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromSeconds(5));

        store.Update(store.Current with { Theme = AtlasTheme.Light });
        await store.FlushAsync();
        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Light", document.RootElement.GetProperty("theme").GetString());
        Assert.DoesNotContain("ProcessId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CharacterName", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposeAsync_WithPendingUpdate_FlushesLatestSettings()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromSeconds(5));
        store.Update(store.Current with { Theme = AtlasTheme.Light });

        await store.DisposeAsync();

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"theme\": \"Light\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_WhenUpdateArrivesDuringSave_FlushesNewestGeneration()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        var contentIds = Enumerable.Range(0, 100_000)
            .Select(index => $"content-{index:D5}")
            .ToArray();
        var updateAppliedDuringSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        AtlasSettingsStore? store = null;
        Task? firstFlush = null;
        var observedCreate = 0;
        using var watcher = new FileSystemWatcher(directory.Path)
        {
            Filter = "settings.json.tmp",
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) =>
        {
            if (Interlocked.Exchange(ref observedCreate, 1) != 0)
            {
                return;
            }

            store!.Update(store.Current with { Theme = AtlasTheme.Dark });
            updateAppliedDuringSave.TrySetResult();
        };
        store = await AtlasSettingsStore.CreateAsync(
            path,
            contentIds,
            TimeSpan.FromSeconds(5));
        store.Update(store.Current with { Theme = AtlasTheme.Light });
        firstFlush = store.FlushAsync();

        await updateAppliedDuringSave.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(firstFlush);
        Assert.False(firstFlush.IsCompleted);
        await firstFlush;
        await store.DisposeAsync();

        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"theme\": \"Dark\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_AfterDisposeStarts_ThrowsBeforeFinalSaveCompletes()
    {
        using var directory = new TemporaryDirectory();
        var contentIds = Enumerable.Range(0, 100_000)
            .Select(index => $"content-{index:D5}")
            .ToArray();
        var temporaryFileCreated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory.Path)
        {
            Filter = "settings.json.tmp",
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => temporaryFileCreated.TrySetResult();
        var store = await AtlasSettingsStore.CreateAsync(
            directory.File("settings.json"),
            contentIds,
            TimeSpan.FromSeconds(5));
        store.Update(store.Current with { Theme = AtlasTheme.Light });

        var dispose = store.DisposeAsync().AsTask();
        await temporaryFileCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var exception = Record.Exception(() =>
            store.Update(store.Current with { Theme = AtlasTheme.Dark }));
        await dispose;

        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public async Task CreateAsync_WithPartiallyMissingNestedFields_MergesEachDefault()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "theme": "Light",
              "display": {
                "nodes": {
                  "Completed": { "showMapNames": false }
                },
                "edges": { "width": 3.25 },
                "highlight": { "color": "#123456" },
                "labels": { "fontSize": 20 }
              }
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasSettingsLoadStatus.Loaded, store.LoadStatus);
        Assert.False(store.Current.Nodes[AtlasNodeCategory.Completed].ShowMapNames);
        Assert.True(store.Current.Nodes[AtlasNodeCategory.Completed].ShowConnections);
        Assert.True(store.Current.Nodes[AtlasNodeCategory.Completed].ShowMapContents);
        Assert.True(store.Current.Nodes[AtlasNodeCategory.Unlocked].ShowMapContents);
        Assert.True(store.Current.Nodes[AtlasNodeCategory.Locked].ShowMapContents);
        Assert.Equal(
            AtlasDisplaySettings.Default.Nodes[AtlasNodeCategory.Unlocked],
            store.Current.Nodes[AtlasNodeCategory.Unlocked]);
        Assert.Equal(3.25f, store.Current.Edges.Width);
        Assert.Equal(
            AtlasDisplaySettings.Default.Edges.Opacity,
            store.Current.Edges.Opacity);
        Assert.Equal("#123456", store.Current.Highlight.Color);
        Assert.Equal(
            AtlasDisplaySettings.Default.Highlight.Width,
            store.Current.Highlight.Width);
        Assert.Equal(20f, store.Current.Labels.FontSize);
        Assert.Equal(
            AtlasDisplaySettings.Default.Labels.BackgroundColor,
            store.Current.Labels.BackgroundColor);
    }

    [Fact]
    public async Task MapContentVisibility_PersistsDifferentValuesAndReloads()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await using (var store = await AtlasSettingsStore.CreateAsync(
                         path,
                         AtlasContentCatalog.Embedded.ContentIds,
                         TimeSpan.FromSeconds(5)))
        {
            var nodes = store.Current.Nodes.ToDictionary();
            nodes[AtlasNodeCategory.Completed] = nodes[AtlasNodeCategory.Completed] with { ShowMapContents = false };
            nodes[AtlasNodeCategory.Unlocked] = nodes[AtlasNodeCategory.Unlocked] with { ShowMapContents = true };
            nodes[AtlasNodeCategory.Locked] = nodes[AtlasNodeCategory.Locked] with { ShowMapContents = false };
            store.Update(store.Current with { Nodes = nodes });
            await store.FlushAsync();
        }

        using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
        {
            var nodes = document.RootElement
                .GetProperty("display")
                .GetProperty("nodes");
            Assert.False(nodes.GetProperty("Completed").GetProperty("showMapContents").GetBoolean());
            Assert.True(nodes.GetProperty("Unlocked").GetProperty("showMapContents").GetBoolean());
            Assert.False(nodes.GetProperty("Locked").GetProperty("showMapContents").GetBoolean());
        }

        await using var reloaded = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.False(reloaded.Current.Nodes[AtlasNodeCategory.Completed].ShowMapContents);
        Assert.True(reloaded.Current.Nodes[AtlasNodeCategory.Unlocked].ShowMapContents);
        Assert.False(reloaded.Current.Nodes[AtlasNodeCategory.Locked].ShowMapContents);
    }

    [Fact]
    public async Task CreateAsync_WithUndefinedTheme_QuarantinesFile()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "theme": 999
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasSettingsLoadStatus.RecoveredInvalid, store.LoadStatus);
        Assert.Equal(AtlasTheme.Dark, store.Current.Theme);
    }

    [Fact]
    public async Task CreateAsync_WithUndefinedAltOverlayMode_QuarantinesFile()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "altOverlayMode": 999
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));

        Assert.Equal(AtlasSettingsLoadStatus.RecoveredInvalid, store.LoadStatus);
        Assert.Equal(AltOverlayMode.HoldToHide, store.Current.AltOverlayMode);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(directory.Path, "settings.invalid.*.json"));
    }

    [Fact]
    public async Task Hotkey_DefaultsToF12AndPublishesUpdateImmediately()
    {
        using var directory = new TemporaryDirectory();
        await using var store = await AtlasSettingsStore.CreateAsync(
            directory.File("settings.json"),
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(20));
        AtlasHotkey? published = null;
        store.HotkeyChanged += hotkey => published = hotkey;

        Assert.Equal(AtlasHotkey.Default, store.CurrentHotkey);
        var updated = AtlasHotkey.Parse("Alt+R");
        store.UpdateHotkey(updated);

        Assert.Equal(updated, store.CurrentHotkey);
        Assert.Equal(updated, published);
    }

    [Fact]
    public async Task Hotkey_PersistsAndReloadsWithDisplaySettings()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await using (var store = await AtlasSettingsStore.CreateAsync(
                         path,
                         AtlasContentCatalog.Embedded.ContentIds,
                         TimeSpan.FromMilliseconds(10)))
        {
            store.Update(store.Current with { Theme = AtlasTheme.Light });
            store.UpdateHotkey(AtlasHotkey.Parse("Alt+R"));
            await store.FlushAsync();
        }

        await using var reloaded = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(AtlasTheme.Light, reloaded.Current.Theme);
        Assert.Equal("Alt+R", reloaded.CurrentHotkey.Gesture);
    }

    [Fact]
    public async Task InvalidSavedHotkey_FallsBackWithoutDiscardingDisplaySettings()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("settings.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "theme": "Light",
              "overlayToggleHotkey": "R"
            }
            """);

        await using var store = await AtlasSettingsStore.CreateAsync(
            path,
            AtlasContentCatalog.Embedded.ContentIds,
            TimeSpan.FromMilliseconds(10));

        Assert.Equal(AtlasSettingsLoadStatus.Loaded, store.LoadStatus);
        Assert.Equal(AtlasTheme.Light, store.Current.Theme);
        Assert.Equal(AtlasHotkey.Default, store.CurrentHotkey);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"FreiAtlas.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
