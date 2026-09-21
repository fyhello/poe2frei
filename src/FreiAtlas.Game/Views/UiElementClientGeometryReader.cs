using System.Numerics;
using FreiAtlas.Core.Area;
using FreiAtlas.Core.Memory;
using FreiAtlas.Game.Memory;

namespace FreiAtlas.Game.Views;

internal sealed class UiElementClientGeometryReader
{
    private const int MaximumParentDepth = 64;
    private readonly IProcessMemory _memory;
    private readonly Poe2MemoryProfile _profile;
    private readonly GameMemoryReader _reader;

    public UiElementClientGeometryReader(IProcessMemory memory, Poe2MemoryProfile? profile = null)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _profile = profile ?? Poe2MemoryProfile.Current;
        _reader = new GameMemoryReader(memory, _profile);
    }

    public bool TryReadRect(
        nint address,
        nint scrollViewport,
        Vector2 scrollOffset,
        AreaUiRect clientViewport,
        out AreaUiRect bounds,
        out string? diagnosticCode)
    {
        bounds = default!;
        diagnosticCode = "runeforge-ui-geometry-invalid";
        var parentCycle = false;
        if (!IsValidViewport(clientViewport)
            || !IsFinite(scrollOffset)
            || !_reader.TryReadVector2(address + _profile.UiElement.WidthOffset, out var size)
            || size.X <= 1f
            || size.Y <= 1f
            || !TryReadPosition(
                address,
                scrollViewport,
                scrollOffset,
                clientViewport,
                new HashSet<nint>(),
                0,
                out var position,
                out var scale,
                out parentCycle))
        {
            if (parentCycle)
            {
                diagnosticCode = "runeforge-ui-parent-cycle";
            }
            return false;
        }

        var clientPosition = new Vector2(clientViewport.X, clientViewport.Y) + (position * scale.Value);
        var clientSize = size * scale.Value;
        if (!IsFinite(clientPosition)
            || !IsFinite(clientSize)
            || clientSize.X <= 0f
            || clientSize.Y <= 0f)
        {
            return false;
        }

        bounds = new AreaUiRect(clientPosition.X, clientPosition.Y, clientSize.X, clientSize.Y);
        diagnosticCode = null;
        return true;
    }

    private bool TryReadPosition(
        nint address,
        nint scrollViewport,
        Vector2 scrollOffset,
        AreaUiRect clientViewport,
        HashSet<nint> path,
        int depth,
        out Vector2 position,
        out ElementScale scale,
        out bool parentCycle)
    {
        position = default;
        scale = default;
        parentCycle = false;
        if (depth >= MaximumParentDepth)
        {
            return false;
        }
        if (!path.Add(address))
        {
            parentCycle = true;
            return false;
        }

        if (!_reader.TryReadVector2(address + _profile.UiElement.RelativePositionOffset, out var relativePosition)
            || !TryReadScale(address, clientViewport, out scale)
            || !_memory.TryReadPointer(address + _profile.UiElement.ParentOffset, out var parent)
            || !TryReadFlags(address, out var flags))
        {
            path.Remove(address);
            return false;
        }

        Vector2 parentPosition;
        if (parent == 0)
        {
            parentPosition = Vector2.Zero;
        }
        else
        {
            var isScrollParent = parent == scrollViewport;
            if (!TryReadPosition(
                    parent,
                    isScrollParent ? 0 : scrollViewport,
                    isScrollParent ? Vector2.Zero : scrollOffset,
                    clientViewport,
                    path,
                    depth + 1,
                    out parentPosition,
                    out var parentScale,
                    out parentCycle))
            {
                path.Remove(address);
                return false;
            }

            if (isScrollParent)
            {
                // The caller supplies zero for the clip rect and live scroll for rows.
                parentPosition += scrollOffset;
            }
            else if ((flags & (1u << _profile.UiElement.ModifyPositionBit)) != 0)
            {
                if (!_reader.TryReadVector2(parent + _profile.UiElement.PositionModifierOffset, out var modifier))
                {
                    path.Remove(address);
                    return false;
                }
                parentPosition += modifier;
            }
            if (parentScale.Index != scale.Index || parentScale.Value != scale.Value)
            {
                parentPosition *= parentScale.Value / scale.Value;
            }
        }

        position = parentPosition + relativePosition;
        path.Remove(address);
        return IsFinite(position);
    }

    private bool TryReadScale(nint address, AreaUiRect clientViewport, out ElementScale scale)
    {
        scale = default;
        if (!_memory.TryReadFloat(address + _profile.UiElement.ScaleOffset, out var multiplier)
            || !_reader.TryReadByte(address + _profile.UiElement.ScaleIndexOffset, out var index)
            || !float.IsFinite(multiplier))
        {
            return false;
        }
        if (multiplier == 0f)
        {
            multiplier = 1f;
        }

        var widthScale = clientViewport.Width / _profile.UiElement.BaseResolutionWidth;
        var heightScale = clientViewport.Height / _profile.UiElement.BaseResolutionHeight;
        var value = index switch
        {
            1 => new Vector2(widthScale, widthScale) * multiplier,
            2 => new Vector2(heightScale, heightScale) * multiplier,
            3 => new Vector2(widthScale, heightScale) * multiplier,
            _ => new Vector2(multiplier, multiplier)
        };
        if (!IsFinite(value) || value.X <= 0f || value.Y <= 0f)
        {
            return false;
        }

        scale = new ElementScale(index, value);
        return true;
    }

    private bool TryReadFlags(nint address, out uint flags)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (_memory.TryRead(address + _profile.UiElement.FlagsOffset, bytes))
        {
            flags = BitConverter.ToUInt32(bytes);
            return true;
        }
        flags = default;
        return false;
    }

    private static bool IsValidViewport(AreaUiRect viewport)
        => float.IsFinite(viewport.X)
           && float.IsFinite(viewport.Y)
           && float.IsFinite(viewport.Width)
           && float.IsFinite(viewport.Height)
           && viewport.Width > 0f
           && viewport.Height > 0f;

    private static bool IsFinite(Vector2 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y);

    private readonly record struct ElementScale(byte Index, Vector2 Value);
}
