using System.Numerics;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Tests.Memory;

namespace FreiAtlas.Game.Tests.Views;

internal sealed class ExpeditionUiMemoryBuilder
{
    private readonly SyntheticProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private long _nextElement = 0x900000;
    private long _nextVector = 0xB00000;

    public ExpeditionUiMemoryBuilder(SyntheticProcessMemory memory, Poe2MemoryProfile? profile = null)
    {
        _memory = memory;
        _profile = profile ?? Poe2MemoryProfile.Current;
        InGameState = AllocateElementAddress();
        Root = AddElement(flags: 0, size: new Vector2(2560, 1600));
        _memory.WritePointer(InGameState + _profile.InGameState.UiRootOffset, Root);
    }

    public nint InGameState { get; }
    public nint Root { get; }
    public Poe2MemoryProfile Profile => _profile;

    public nint AddElement(uint flags = 0, Vector2? relativePosition = null, Vector2? positionModifier = null, Vector2? size = null, float scale = 1f, byte scaleIndex = 0, bool selfValid = true, bool modifyPosition = false)
    {
        var address = AllocateElementAddress();
        var emptyVector = AllocateVectorAddress();
        _memory.WritePointer(address + _profile.UiElement.SelfOffset, selfValid ? address : 0);
        _memory.WritePointer(address + _profile.UiElement.ChildrenOffset, emptyVector);
        _memory.WritePointer(address + _profile.UiElement.ChildrenEndOffset, emptyVector);
        _memory.WritePointer(address + _profile.UiElement.ParentOffset, 0);
        _memory.WriteVector2(address + _profile.UiElement.RelativePositionOffset, relativePosition ?? Vector2.Zero);
        _memory.WriteVector2(address + _profile.UiElement.PositionModifierOffset, positionModifier ?? Vector2.Zero);
        _memory.WriteFloat(address + _profile.UiElement.ScaleOffset, scale);
        _memory.WriteByte(address + _profile.UiElement.ScaleIndexOffset, scaleIndex);
        _memory.WriteUInt32(address + _profile.UiElement.FlagsOffset, modifyPosition
            ? flags | (1u << _profile.UiElement.ModifyPositionBit)
            : flags);
        _memory.WriteVector2(address + _profile.UiElement.WidthOffset, size ?? new Vector2(100, 40));
        return address;
    }

    public nint AddRow(
        string rewardText,
        int runeCount,
        Vector2 relativePosition,
        Vector2? size = null,
        bool selfValid = true)
    {
        var row = AddElement(
            flags: 1u << _profile.UiElement.VisibleBit,
            relativePosition: relativePosition,
            size: size ?? new Vector2(222, 21),
            selfValid: selfValid);
        var reward = AddElement();
        _memory.WriteStdWString(reward + _profile.RuneforgeUi.RewardTextOffset, rewardText);
        var children = new List<nint> { reward };
        for (var index = 0; index < runeCount; index++) children.Add(AddElement());
        SetChildren(row, children.ToArray());
        return row;
    }

    public (nint Gate, nint Viewport, nint Container) AddRecipeBranch(bool gateVisible, IReadOnlyList<nint> rows, bool includeWrongSibling = false, Vector2? scrollOffset = null)
    {
        var fingerprints = _profile.RuneforgeUi.PanelFlagFingerprints;
        var visibleMask = 1u << _profile.UiElement.VisibleBit;
        var gate = AddElement(flags: gateVisible ? fingerprints[0] | visibleMask : fingerprints[0] & ~visibleMask, relativePosition: new Vector2(20, 70), size: new Vector2(240, 260));
        var current = gate;
        nint viewport = 0;
        for (var step = 1; step < fingerprints.Count; step++)
        {
            var next = AddElement(flags: fingerprints[step] | visibleMask, relativePosition: step == _profile.RuneforgeUi.ViewportStep ? new Vector2(1, 1) : Vector2.Zero, size: step == fingerprints.Count - 1 ? new Vector2(222, Math.Max(21, rows.Count * 21)) : new Vector2(222, 230));
            if (step == _profile.RuneforgeUi.ViewportStep)
            {
                viewport = next;
                _memory.WriteVector2(viewport + _profile.RuneforgeUi.ScrollOffset, scrollOffset ?? Vector2.Zero);
            }
            SetChildren(current, next);
            current = next;
        }
        var container = current;
        SetChildren(container, rows.ToArray());
        if (includeWrongSibling)
        {
            var wrongGate = AddElement(flags: fingerprints[0] | visibleMask);
            var wrongCurrent = wrongGate;
            for (var step = 1; step < fingerprints.Count; step++)
            {
                var wrong = AddElement(flags: fingerprints[step] | visibleMask);
                SetChildren(wrongCurrent, wrong);
                wrongCurrent = wrong;
            }
            SetChildren(wrongCurrent, AddElement(selfValid: false));
            SetChildren(Root, wrongGate, gate);
        }
        else SetChildren(Root, gate);
        return (gate, viewport, container);
    }

    public (nint Gate, nint Viewport, nint Container) AddRecipeTree(
        IReadOnlyList<(string RewardText, int RuneCount, Vector2 Position)> rows,
        bool gateVisible = true,
        bool includeWrongSibling = false,
        Vector2? scroll = null)
        => AddRecipeBranch(
            gateVisible,
            rows.Select(row => AddRow(row.RewardText, row.RuneCount, row.Position)).ToArray(),
            includeWrongSibling,
            scroll);

    public void SetChildren(nint parent, params nint[] children)
    {
        var start = AllocateVectorAddress(children.Length * sizeof(long));
        for (var index = 0; index < children.Length; index++)
        {
            _memory.WritePointer(start + (index * sizeof(long)), children[index]);
            _memory.WritePointer(children[index] + _profile.UiElement.ParentOffset, parent);
        }
        _memory.WritePointer(parent + _profile.UiElement.ChildrenOffset, start);
        _memory.WritePointer(parent + _profile.UiElement.ChildrenEndOffset, start + (children.Length * sizeof(long)));
    }

    public void SetParent(nint child, nint parent) => _memory.WritePointer(child + _profile.UiElement.ParentOffset, parent);
    public void WriteSize(nint address, Vector2 size) => _memory.WriteVector2(address + _profile.UiElement.WidthOffset, size);

    private nint AllocateElementAddress() { var address = (nint)_nextElement; _nextElement += 0x800; return address; }
    private nint AllocateVectorAddress(int byteCount = 0)
    {
        var address = (nint)_nextVector;
        var blocks = Math.Max(1, (byteCount + 0x3FF) / 0x400);
        _nextVector += blocks * 0x400;
        return address;
    }
}
