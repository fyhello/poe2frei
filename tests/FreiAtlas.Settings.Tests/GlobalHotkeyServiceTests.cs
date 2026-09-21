using FreiAtlas.App.Settings;
using FreiAtlas.Settings.Hotkeys;

namespace FreiAtlas.Settings.Tests;

public sealed class GlobalHotkeyServiceTests
{
    [Fact]
    public void TryRegister_AddsNoRepeatModifier()
    {
        var native = new FakeGlobalHotkeyNative();
        using var registration = new GlobalHotkeyRegistration((nint)123, native);

        Assert.True(registration.TryRegister(AtlasHotkey.Default, out _));

        var call = Assert.Single(native.RegisterCalls);
        Assert.Equal(0x4000u, call.Modifiers);
        Assert.Equal(0x7Bu, call.VirtualKey);
    }

    [Fact]
    public void TryRegister_WhenCandidateConflicts_KeepsOldRegistration()
    {
        var native = new FakeGlobalHotkeyNative();
        using var registration = new GlobalHotkeyRegistration((nint)123, native);
        Assert.True(registration.TryRegister(AtlasHotkey.Default, out _));
        var oldId = registration.ActiveId;
        native.FailNextRegister = true;

        var changed = registration.TryRegister(
            AtlasHotkey.Parse("Alt+R"),
            out var error);

        Assert.False(changed);
        Assert.Equal(oldId, registration.ActiveId);
        Assert.Equal(AtlasHotkey.Default, registration.Current);
        Assert.Empty(native.UnregisterCalls);
        Assert.Equal("快捷键已被其他程序占用", error);
    }

    [Fact]
    public void TryRegister_WhenCandidateSucceeds_ReplacesOldRegistration()
    {
        var native = new FakeGlobalHotkeyNative();
        using var registration = new GlobalHotkeyRegistration((nint)123, native);
        Assert.True(registration.TryRegister(AtlasHotkey.Default, out _));
        var oldId = registration.ActiveId;

        Assert.True(registration.TryRegister(
            AtlasHotkey.Parse("Alt+R"),
            out var error));

        Assert.Null(error);
        Assert.NotEqual(oldId, registration.ActiveId);
        Assert.Equal(AtlasHotkey.Parse("Alt+R"), registration.Current);
        Assert.Contains(((nint)123, oldId), native.UnregisterCalls);
        Assert.False(registration.HandlesMessage(oldId));
        Assert.True(registration.HandlesMessage(registration.ActiveId));
    }

    [Fact]
    public void Dispose_UnregistersCurrentRegistration()
    {
        var native = new FakeGlobalHotkeyNative();
        var registration = new GlobalHotkeyRegistration((nint)123, native);
        Assert.True(registration.TryRegister(AtlasHotkey.Default, out _));
        var activeId = registration.ActiveId;

        registration.Dispose();

        Assert.Contains(((nint)123, activeId), native.UnregisterCalls);
    }

    private sealed class FakeGlobalHotkeyNative : IGlobalHotkeyNative
    {
        public List<(nint Window, int Id, uint Modifiers, uint VirtualKey)>
            RegisterCalls { get; } = [];

        public List<(nint Window, int Id)> UnregisterCalls { get; } = [];

        public bool FailNextRegister { get; set; }

        public bool RegisterHotKey(
            nint window,
            int id,
            uint modifiers,
            uint virtualKey)
        {
            RegisterCalls.Add((window, id, modifiers, virtualKey));
            if (!FailNextRegister)
            {
                return true;
            }

            FailNextRegister = false;
            return false;
        }

        public bool UnregisterHotKey(nint window, int id)
        {
            UnregisterCalls.Add((window, id));
            return true;
        }
    }
}
