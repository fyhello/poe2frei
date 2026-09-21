using System.Text.Json;
using FreiAtlas.App.Settings;
using FreiAtlas.Core.Recovery;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.App.Tests;

public sealed class QuickAssistSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "FreiAtlas.QuickAssist.Tests." + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task RoundTrip_PersistsIndependentRootWithoutChangingDisplayOrHotkey()
    {
        var settings = QuickAssistSettings.Default with
        {
            Health = new() { Enabled = true, Mode = RecoveryThresholdMode.Fixed, FixedValue = 1234, Key = "Ctrl+3" },
            Mana = new() { Enabled = true, Percentage = 42, Key = "Numpad1", IntervalMilliseconds = 1750 }
        };
        await using (var store = await AtlasSettingsStore.CreateAsync(SettingsPath, []))
        {
            store.Update(store.Current with { Theme = AtlasTheme.Light });
            store.UpdateHotkey(AtlasHotkey.Parse("F11"));
            QuickAssistSettings? received = null;
            store.QuickAssistChanged += value => received = value;
            store.UpdateQuickAssist(settings);
            Assert.Same(settings, received);
            await store.FlushAsync();
        }
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        Assert.True(json.RootElement.TryGetProperty("quickAssist", out _));
        Assert.False(json.RootElement.GetProperty("display").TryGetProperty("quickAssist", out _));
        await using var loaded = await AtlasSettingsStore.CreateAsync(SettingsPath, []);
        Assert.Equal(settings, loaded.QuickAssist);
        Assert.Equal(AtlasTheme.Light, loaded.Current.Theme);
        Assert.Equal("F11", loaded.CurrentHotkey.Gesture);
        var display = loaded.Current;
        loaded.UpdateQuickAssist(QuickAssistSettings.Default);
        Assert.Same(display, loaded.Current);
    }

    [Fact]
    public async Task OldSettings_DefaultRecoveryIsDisabled()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, """{"theme":"Light","overlayToggleHotkey":"F11"}""");
        await using var store = await AtlasSettingsStore.CreateAsync(SettingsPath, []);
        Assert.Equal(QuickAssistSettings.Default, store.QuickAssist);
        Assert.Equal(AtlasTheme.Light, store.Current.Theme);
    }

    [Fact]
    public async Task InvalidUpdate_DoesNotPublishOrReplacePreviousSettings()
    {
        await using var store = await AtlasSettingsStore.CreateAsync(SettingsPath, []);
        var original = store.QuickAssist;
        var published = false;
        store.QuickAssistChanged += _ => published = true;
        Assert.Throws<ArgumentException>(() => store.UpdateQuickAssist(original with { Health = new() { Percentage = 0 } }));
        Assert.Same(original, store.QuickAssist);
        Assert.False(published);
    }

    [Fact]
    public async Task HotkeyConflict_IsRejectedFromBothUpdateDirections()
    {
        await using var store = await AtlasSettingsStore.CreateAsync(SettingsPath, []);
        Assert.Throws<ArgumentException>(() => store.UpdateQuickAssist(new() { Health = new() { Key = "F12" } }));
        store.UpdateQuickAssist(new() { Health = new() { Key = "Alt+Ctrl+A" } });
        Assert.Throws<ArgumentException>(() => store.UpdateHotkey(AtlasHotkey.Parse("Ctrl+Alt+A")));
        Assert.Equal(AtlasHotkey.Default, store.CurrentHotkey);
    }

    [Fact]
    public async Task PortalHotkeyConflict_IsRejectedWithOverlayAndRecoveryKeys()
    {
        await using var store = await AtlasSettingsStore.CreateAsync(SettingsPath, []);
        Assert.Throws<ArgumentException>(() => store.UpdateQuickAssist(
            QuickAssistSettings.Default with
            {
                PortalSqueeze = new() { Hotkey = "F12" }
            }));
        Assert.Throws<ArgumentException>(() => store.UpdateQuickAssist(
            QuickAssistSettings.Default with
            {
                Health = new() { Key = "Ctrl+J" },
                PortalSqueeze = new() { Hotkey = "Ctrl+J" }
            }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
