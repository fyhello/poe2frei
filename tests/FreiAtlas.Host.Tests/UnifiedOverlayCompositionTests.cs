using System.Xml.Linq;

namespace FreiAtlas.Host.Tests;

public sealed class UnifiedOverlayCompositionTests
{
    private static readonly string WorkspaceRoot = FindWorkspaceRoot();

    [Fact]
    public void HostRuntimeReferencesFormalAreaMapPlugin()
    {
        var project = XDocument.Load(Path.Combine(
            WorkspaceRoot,
            "src",
            "FreiAtlas.Host.Runtime",
            "FreiAtlas.Host.Runtime.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => value is not null)
            .Select(value => Path.GetFileNameWithoutExtension(value!));

        Assert.Contains("FreiAtlas.Plugin.AreaMap", references);
        Assert.Contains("FreiAtlas.Expedition", references);
    }

    [Fact]
    public void RunnerUsesOneAttachmentForBothReadingProviders()
    {
        var source = RunnerSource();

        Assert.Equal(1, Count(source, "ProcessAttachment.TryAttach("));
        Assert.Contains("new AtlasProcessReader(\n                attachment.Memory", source);
        Assert.Contains("new AreaMapProviderService(\n                attachment.Memory", source);
    }

    [Fact]
    public void RunnerComposesFormalAreaPluginAndAllFourDrawingSurfaces()
    {
        var source = RunnerSource();

        Assert.Contains("new AreaMapOverlayPlugin(", source);
        Assert.Contains("AtlasNameOverlaySurface.Create()", source);
        Assert.Contains("AreaMapOverlaySurface.Create()", source);
        Assert.Contains("ExpeditionPanelSurface.Create(", source);
        Assert.Contains("new ExpeditionRecipePanelPlugin(", source);
        Assert.Contains("new ExpeditionRecipeValueAdapter(", source);
        Assert.Contains("new ExpeditionNativeValuePlugin(", source);
        Assert.Contains("ExpeditionNativeValueSurface.Create()", source);
        Assert.Contains("nativeValueSurface.Render(", source);
        Assert.Contains("ExpeditionOverlayVisibilityPolicy.Evaluate(", source);
        Assert.Contains("areaProvider.StartAsync(", source);
        Assert.Contains("AreaMapOverlayCoordinator.Build(", source);
    }

    [Fact]
    public void RunnerRegistersAllFourSurfacesForImmediateStop()
    {
        var source = RunnerSource();

        Assert.Contains("stopController?.Register(atlasRequestHide)", source);
        Assert.Contains("stopController?.Register(areaRequestHide)", source);
        Assert.Contains("stopController?.Register(panelRequestHide)", source);
        Assert.Contains("stopController?.Register(nativeValueRequestHide)", source);
        Assert.Contains("stopController?.Unregister(atlasRequestHide)", source);
        Assert.Contains("stopController?.Unregister(areaRequestHide)", source);
        Assert.Contains("stopController?.Unregister(panelRequestHide)", source);
        Assert.Contains("stopController?.Unregister(nativeValueRequestHide)", source);
    }

    [Fact]
    public void RunnerPassesOneSettingsSnapshotToAreaPluginAndSurface()
    {
        var source = RunnerSource();

        Assert.Equal(1, Count(source, "var settings = settingsSource.Current;"));
        Assert.Contains("areaPlugin.Build(settings.AreaMap)", source, StringComparison.Ordinal);
        Assert.Contains(
            "areaFrame.Scene,\n                    settings.AreaMap",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerCachesNavigationPlanAndPassesIndependentDrawingLayers()
    {
        var source = RunnerSource();

        Assert.Contains(
            "var navigationState = new AtlasNavigationRenderState();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "navigationState.ObserveGeometry(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "navigationState.ResolvePlan(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "frame.NavigationRouteEdges,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "frame.Directions,",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerSharesOnePriceBookForAllConsumersInEachFrame()
    {
        var source = RunnerSource();

        Assert.Equal(2, Count(source, "() => framePriceBook)"));
        Assert.Contains("preparePriceFrame();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new Poe2DbPriceSource(", source, StringComparison.Ordinal);
        Assert.Contains("new ExpeditionValueProvider(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerBuildsPanelWithoutDependingOnAtlasOrMapViewVisibility()
    {
        var source = RunnerSource();

        Assert.Contains("expeditionPanelPlugin.Build(", source, StringComparison.Ordinal);
        Assert.Contains("settings.AreaMap.ExpeditionPanel.ExpandOnAreaEntry", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("expeditionPanelPlugin.Build(", StringComparison.Ordinal)
            < source.IndexOf("if (SuppressOverlays(", StringComparison.Ordinal));
        Assert.Contains("expeditionPanelSurface.Render(", source, StringComparison.Ordinal);
        Assert.Contains("panelScale: 1f", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "expeditionPanelPlugin.Build(settings.AreaMap)",
            source,
            StringComparison.Ordinal);
    }

    private static string RunnerSource()
        => File.ReadAllText(Path.Combine(
            WorkspaceRoot,
            "src",
            "FreiAtlas.Host",
            "AtlasOverlayRunner.cs"));

    private static int Count(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FreiAtlas.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate FreiAtlas.slnx.");
    }
}
