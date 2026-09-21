using System.Drawing;
using FreiAtlas.Core.Atlas;
using FreiAtlas.Core.Contracts;
using FreiAtlas.Core.Settings;
using FreiAtlas.Platform.Windows.Windows;

namespace FreiAtlas.Host;

public sealed record AtlasOverlayFrame(
    Rectangle ClientBounds,
    IReadOnlyList<AtlasLabelPlacement> Labels,
    IReadOnlyList<AtlasEdgePlacement> Edges,
    IReadOnlyList<AtlasEdgePlacement> NavigationRouteEdges,
    IReadOnlyList<AtlasContentIconPlacement> ContentIcons,
    IReadOnlyList<AtlasDirectionPlacement> Directions);

public static class AtlasOverlayCoordinator
{
    public static AtlasOverlayFrame? Build(
        GameWindowSnapshot? window,
        AtlasSnapshot? snapshot,
        AtlasLiveRenderGeometry geometry,
        Point? screenMousePosition = null)
        => Build(
            window,
            snapshot,
            geometry,
            AtlasDisplaySettings.Default,
            screenMousePosition);

    public static AtlasOverlayFrame? Build(
        GameWindowSnapshot? window,
        AtlasSnapshot? snapshot,
        AtlasLiveRenderGeometry geometry,
        AtlasDisplaySettings settings,
        Point? screenMousePosition = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var navigationPlan = snapshot is not null
            && settings.Navigation.Rules.Count > 0
            ? AtlasNavigationPlanner.Build(snapshot, settings.Navigation)
            : new AtlasNavigationPlan(
                new HashSet<AtlasGridPos>(),
                Array.Empty<AtlasNavigationTarget>(),
                new HashSet<AtlasNavigationEdgeKey>(),
                string.Empty);
        return Build(
            window,
            snapshot,
            geometry,
            settings,
            navigationPlan,
            geometry,
            screenMousePosition);
    }

    public static AtlasOverlayFrame? Build(
        GameWindowSnapshot? window,
        AtlasSnapshot? snapshot,
        AtlasLiveRenderGeometry geometry,
        AtlasDisplaySettings settings,
        AtlasNavigationPlan navigationPlan,
        AtlasLiveRenderGeometry? navigationGeometry,
        Point? screenMousePosition = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(navigationPlan);

        if (window is null
            || !window.IsForeground
            || window.IsMinimized
            || window.ClientBounds.Width <= 0
            || window.ClientBounds.Height <= 0
            || snapshot is null
            || !snapshot.IsAtlasOpen
            || snapshot.Status != AtlasSnapshotStatus.Stable)
        {
            return null;
        }

        var viewport = new AtlasViewport(
            window.ClientBounds.Width,
            window.ClientBounds.Height);
        var labels = AtlasLabelLayout.Build(
            snapshot,
            geometry,
            viewport,
            margin: 100f);
        var edges = AtlasEdgeLayout.Build(
            snapshot,
            geometry,
            viewport,
            margin: 100f);
        if (screenMousePosition is { } screenMouse)
        {
            var clientMouse = new System.Numerics.Vector2(
                screenMouse.X - window.ClientBounds.Left,
                screenMouse.Y - window.ClientBounds.Top);
            var selection = AtlasRegionReveal.Select(
                snapshot,
                labels,
                clientMouse,
                settings.Labels.FontSize);
            edges = edges
                .Select(edge => edge with
                {
                    IsHighlighted = AtlasRegionReveal.IsInternalEdge(
                        snapshot,
                        edge.From,
                        edge.To,
                        selection)
                })
                .ToArray();
        }
        var contentIcons = AtlasContentIconLayout.Build(
            snapshot,
            geometry,
            viewport,
            margin: 100f);
        var nodesByGrid = snapshot.Nodes
            .GroupBy(node => node.Grid)
            .ToDictionary(group => group.Key, group => group.First());
        var visibleLabels = labels
            .Select(label => label with
            {
                IsNavigationHighlighted =
                    navigationPlan.HighlightTargets.Contains(label.Grid)
                    && label.Anchor.X >= 0f
                    && label.Anchor.X < viewport.Width
                    && label.Anchor.Y >= 0f
                    && label.Anchor.Y < viewport.Height
            })
            .Where(label =>
                label.IsNavigationHighlighted
                || (nodesByGrid.TryGetValue(label.Grid, out var node)
                    && VisibilityFor(settings, node).ShowMapNames))
            .ToArray();
        var visibleEdges = edges
            .Where(edge =>
                edge.IsHighlighted
                || (nodesByGrid.TryGetValue(edge.From, out var from)
                    && nodesByGrid.TryGetValue(edge.To, out var to)
                    && VisibilityFor(settings, from).ShowConnections
                    && VisibilityFor(settings, to).ShowConnections))
            .ToArray();
        var visibleContentIcons = contentIcons
            .Where(icon =>
                (!settings.ContentVisibility.TryGetValue(
                     icon.ReferenceIconId,
                     out var isVisible)
                 || isVisible)
                && nodesByGrid.TryGetValue(icon.Grid, out var node)
                && VisibilityFor(settings, node).ShowMapContents)
            .ToArray();
        var navigationRouteEdges = navigationGeometry is { } routeGeometry
            ? AtlasNavigationLayout.BuildRouteEdges(
                snapshot,
                navigationPlan,
                routeGeometry,
                viewport,
                margin: 100f)
            : Array.Empty<AtlasEdgePlacement>();
        var directions = navigationGeometry is { } directionGeometry
            ? AtlasNavigationLayout.BuildDirections(
                navigationPlan,
                directionGeometry,
                viewport)
            : Array.Empty<AtlasDirectionPlacement>();
        return new AtlasOverlayFrame(
            window.ClientBounds,
            visibleLabels,
            visibleEdges,
            navigationRouteEdges,
            visibleContentIcons,
            directions);
    }

    private static AtlasNodeCategoryVisibility VisibilityFor(
        AtlasDisplaySettings settings,
        AtlasNodeSnapshot node)
        => settings.Nodes[AtlasNodeCategoryClassifier.Classify(
            node.IsCompleted,
            node.IsAccessible)];
}
