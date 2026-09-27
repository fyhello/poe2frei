using System.Text.Json;
using FreiAtlas.Core.Settings;

namespace FreiAtlas.Settings.Tests;

public sealed class WebAssetsTests
{
    [Fact]
    public void InitialMarkup_UsesOnlyNeutralLoadingState()
    {
        var html = File.ReadAllText(Path.Combine(WebRoot(), "index.html"));

        Assert.Contains(
            "id=\"selectedProcess\">正在加载进程",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"nodeCount\" class=\"page-meta\">正在加载",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"contentCount\" class=\"page-meta\">正在加载",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"saveStatus\">正在加载设置",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "id=\"overlayStatus\">正在加载运行状态",
            html,
            StringComparison.Ordinal);
        AssertTagContains(html, "button", "processSelect", "disabled");
        AssertTagContains(html, "button", "refreshButton", "disabled");
        AssertTagContains(html, "button", "overlayButton", "disabled");
        AssertTagContains(html, "button", "hotkeyButton", "disabled");
        Assert.DoesNotContain("0 个节点", html, StringComparison.Ordinal);
        Assert.DoesNotContain("0 / 0 已显示", html, StringComparison.Ordinal);
        Assert.DoesNotContain("设置已保存", html, StringComparison.Ordinal);
        Assert.DoesNotContain("覆盖层未启动", html, StringComparison.Ordinal);
        Assert.DoesNotContain("未检测到游戏进程", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SubmitSettings_MergesPendingEditsWithoutChangingRuntimeSnapshot()
    {
        var script = NormalizeLineEndings(File.ReadAllText(
            Path.Combine(WebRoot(), "app.js")));

        Assert.Contains("let draftSettings = null;", script, StringComparison.Ordinal);
        Assert.Contains(
            "return clone(draftSettings ?? runtime.settings);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "function submitSettings(settings) {\n"
            + "  draftSettings = settings;\n"
            + "  renderSettings();\n"
            + "  post('updateSettings', { settings });\n"
            + "}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "settingsEqual(snapshot.settings, draftSettings)",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "runtime.settings = settings",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "draftSettings = null;\n    post('resetPage'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "draftSettings = null;\n    post('resetAll'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$('refreshButton').disabled = false;",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SubmitSettings_RendersDraftImmediatelyUntilHostAcknowledgesIt()
    {
        var script = NormalizeLineEndings(File.ReadAllText(
            Path.Combine(WebRoot(), "app.js")));

        Assert.Contains(
            "function displayedSettings() {\n"
            + "  return draftSettings ?? runtime?.settings ?? null;\n"
            + "}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "function submitSettings(settings) {\n"
            + "  draftSettings = settings;\n"
            + "  renderSettings();\n"
            + "  post('updateSettings', { settings });\n"
            + "}",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const settings = displayedSettings();",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "function renderSettings()",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Layout_ReachesSingleColumnAtMinimumWindowWidth()
    {
        var css = NormalizeLineEndings(File.ReadAllText(
            Path.Combine(WebRoot(), "app.css")));
        var xaml = File.ReadAllText(Path.Combine(
            TestPaths.WorkspaceRoot,
            "src", "FreiAtlas.Settings", "MainWindow.xaml"));

        Assert.Contains("MinWidth=\"760\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            ".control-grid.two-col { grid-template-columns: 1fr; }",
            css,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".control-grid.two-col { grid-template-columns: repeat(2",
            css,
            StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 800px)", css, StringComparison.Ordinal);
        Assert.Contains(
            "  .content-list { grid-template-columns: 1fr; }",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "  .workspace { grid-template-columns: 132px minmax(0, 1fr); }",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            ".slider-field { grid-template-columns: 105px minmax(80px, 1fr) 64px; gap: 9px; }",
            css,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("edgeWidth", "0.5", "6", "0.25")]
    [InlineData("edgeOpacity", "0", "1", "0.05")]
    [InlineData("highlightWidth", "0.5", "8", "0.25")]
    [InlineData("highlightOpacity", "0", "1", "0.05")]
    [InlineData("fontSize", "9", "30", "1")]
    [InlineData("backgroundOpacity", "0", "1", "0.05")]
    public void NumericSettingsControls_MatchValidatorRanges(
        string id,
        string minimum,
        string maximum,
        string step)
    {
        var html = File.ReadAllText(Path.Combine(WebRoot(), "index.html"));

        AssertNumericInput(html, id, "range", minimum, maximum, step);
        AssertNumericInput(html, id + "Number", "number", minimum, maximum, step);
    }

    [Fact]
    public void AreaMapPage_ExposesAllIconFiltersWithoutLabelControls()
    {
        var html = File.ReadAllText(Path.Combine(WebRoot(), "index.html"));
        var script = File.ReadAllText(Path.Combine(WebRoot(), "app.js"));

        Assert.Contains("data-page=\"areaMap\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapExpeditionToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapBossToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapAbyssToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapRitualToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapBreachToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapEssenceToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapIncursionToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapStrongboxToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapRareMonsterToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapRareChestsToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapPollenToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"areaMapOmenAltarToggle\"", html, StringComparison.Ordinal);
        AssertTagContains(
            html,
            "button",
            "areaMapExpeditionToggle",
            "aria-label=\"显示先祖秘藏\"");
        AssertTagContains(
            html,
            "button",
            "areaMapBossToggle",
            "aria-label=\"显示 Boss\"");
        AssertTagContains(
            html,
            "button",
            "areaMapPollenToggle",
            "aria-label=\"显示灵火\"");
        AssertTagContains(
            html,
            "button",
            "areaMapOmenAltarToggle",
            "aria-label=\"显示预兆祭坛\"");
        Assert.Contains("settings.areaMap.showExpedition", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showBoss", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showAbyss", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showRitual", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showBreach", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showEssence", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showIncursion", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showStrongbox", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showRareMonster", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showRareChests", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showPollen", script, StringComparison.Ordinal);
        Assert.Contains("settings.areaMap.showOmenAltar", script, StringComparison.Ordinal);
        Assert.DoesNotContain("areaMapLabelFontSize", html, StringComparison.Ordinal);
        Assert.DoesNotContain("areaMapExpeditionBackgroundColor", html, StringComparison.Ordinal);
        Assert.DoesNotContain("areaMapExpeditionBackgroundOpacity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("areaMapExpeditionBorderOpacity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("先祖秘藏标签", html, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.areaMap.largeMapLabelFontSize", script, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.areaMap.expeditionTag", script, StringComparison.Ordinal);
        Assert.Contains("data-reset=\"areaMap\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AreaMapPage_ResetRestoresOnlyAreaMapDefaults()
    {
        var source = NormalizeLineEndings(File.ReadAllText(Path.Combine(
            TestPaths.WorkspaceRoot,
            "src",
            "FreiAtlas.Settings",
            "MainWindow.xaml.cs")));

        Assert.Contains(
            "\"areaMap\" => current with { AreaMap = defaults.AreaMap },",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AreaMapPage_ExposesIndependentNativeRecipePanelOptions()
    {
        var html = File.ReadAllText(Path.Combine(WebRoot(), "index.html"));
        var script = File.ReadAllText(Path.Combine(WebRoot(), "app.js"));

        Assert.Contains("先祖秘藏配方面板", html, StringComparison.Ordinal);
        AssertTagContains(html, "select", "areaMapPanelEntryState", "aria-describedby=\"panelEntryHint\"");
        Assert.Contains("settings.areaMap.expeditionPanel.expandOnAreaEntry", script, StringComparison.Ordinal);
        AssertTagContains(
            html,
            "button",
            "areaMapNativeRecipeValuesToggle",
            "role=\"switch\"");
        AssertTagContains(
            html,
            "button",
            "areaMapNativeRecipeValuesToggle",
            "aria-label=\"原生配方面板显示价值\"");
        AssertTagContains(
            html,
            "button",
            "areaMapAutoHideStandalonePanelToggle",
            "role=\"switch\"");
        AssertTagContains(
            html,
            "button",
            "areaMapAutoHideStandalonePanelToggle",
            "aria-label=\"打开原生配方面板时自动隐藏独立面板\"");
        Assert.Contains(
            "settings.areaMap.expeditionPanel.showNativeRecipeValues",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "settings.areaMap.expeditionPanel.autoHideStandalonePanel",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MasterNodeToggles_RenderPartialSelectionAsMixed()
    {
        var webRoot = WebRoot();
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var css = File.ReadAllText(Path.Combine(webRoot, "app.css"));

        Assert.Contains("function aggregateToggle(values)", script, StringComparison.Ordinal);
        Assert.Contains("state === 'mixed'", script, StringComparison.Ordinal);
        Assert.Contains("'aria-checked', state === 'mixed' ? 'mixed'", script, StringComparison.Ordinal);
        Assert.Contains(".toggle.mixed", css, StringComparison.Ordinal);
    }

    [Fact]
    public void NodePage_ExposesMapContentStateColumnAndMasterToggle()
    {
        var webRoot = WebRoot();
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var css = File.ReadAllText(Path.Combine(webRoot, "app.css"));
        var nodesSection = SliceBetween(
            html,
            "<section id=\"nodes\"",
            "<section id=\"contents\"");

        Assert.Contains("<span>地图内容</span>", nodesSection, StringComparison.Ordinal);
        Assert.Contains(
            "const allContents = aggregateToggle(nodeCategories.map(category => settings.nodes[category.id].showMapContents));",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "appendNodeRow(container, { id: 'All', label: '所有', className: '' }, allNames, allConnections, allContents, true);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "settings.nodes[category.id].showMapContents",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const toggleRole = master ? 'checkbox' : 'switch';",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameToggle.setAttribute('role', toggleRole);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "nameToggle.setAttribute('aria-label', `${category.label}地图名称`);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "connectionToggle.setAttribute('role', toggleRole);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "connectionToggle.setAttribute('aria-label', `${category.label}节点连线`);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "contentToggle.setAttribute('role', toggleRole);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "contentToggle.setAttribute('aria-label', `${category.label}地图内容`);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "updateNodeSetting(category.id, 'showMapContents', showContents !== true)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "const targets = categoryId === 'All' ? nodeCategories.map(item => item.id) : [categoryId];",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "targets.forEach(id => settings.nodes[id][key] = enabled);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: minmax(130px, 1fr) repeat(3, 110px)",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            "grid-template-columns: minmax(100px, 1fr) repeat(3, 84px)",
            css,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBar_ReportsRecoveredSettingsAndStaleRuntimeSnapshot()
    {
        var webRoot = WebRoot();
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var css = File.ReadAllText(Path.Combine(webRoot, "app.css"));

        Assert.Contains("runtime?.loadStatus === 'RecoveredInvalid'", script, StringComparison.Ordinal);
        Assert.Contains("设置文件已损坏，已恢复默认值", script, StringComparison.Ordinal);
        Assert.Contains("const snapshotStaleAfterMs = 5000;", script, StringComparison.Ordinal);
        Assert.Contains("状态可能已过期", script, StringComparison.Ordinal);
        Assert.Contains("setInterval(updateStaleStatus, 1000);", script, StringComparison.Ordinal);
        Assert.Contains(".save-bar .stale", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPage_ExposesResetAllCommand()
    {
        var webRoot = WebRoot();
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("id=\"resetAllButton\"", html, StringComparison.Ordinal);
        Assert.Contains("post('resetAll', {})", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetCommands_PreserveMapContentVisibilityPageBoundaries()
    {
        var source = NormalizeLineEndings(File.ReadAllText(Path.Combine(
            TestPaths.WorkspaceRoot,
            "src",
            "FreiAtlas.Settings",
            "MainWindow.xaml.cs")));
        var resetPage = SliceBetween(
            source,
            "private async Task ResetPageAsync(string page)",
            "private async Task UpdateHotkeyAsync");
        var contentsBranch = SliceBetween(
            resetPage,
            "\"contents\" => current with",
            "\"atlasNavigation\" => current with");
        var resetAllBranch = SliceBetween(
            source,
            "case ResetAllWebCommand:",
            "catch (Exception exception");
        var defaultSettings = SliceBetween(
            source,
            "private static AtlasDisplaySettings DefaultSettings()",
            "private void OnSnapshotChanged");

        Assert.Contains(
            "\"nodes\" => current with { Nodes = defaults.Nodes },",
            resetPage,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContentVisibility = defaults.ContentVisibility",
            contentsBranch,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Nodes =", contentsBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowMapContents", contentsBranch, StringComparison.Ordinal);
        Assert.Contains(
            "await _runtime.UpdateSettingsAsync(DefaultSettings());",
            resetAllBranch,
            StringComparison.Ordinal);
        Assert.Contains(
            "AtlasDisplaySettings.Default,",
            defaultSettings,
            StringComparison.Ordinal);
        Assert.All(
            AtlasDisplaySettings.Default.Nodes.Values,
            visibility => Assert.True(visibility.ShowMapContents));
    }

    [Fact]
    public void SettingsPage_ExposesAccessibleHotkeyRecorder()
    {
        var webRoot = WebRoot();
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var css = File.ReadAllText(Path.Combine(webRoot, "app.css"));

        Assert.Contains("id=\"hotkeyButton\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"hotkeyValue\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"hotkeyError\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", html, StringComparison.Ordinal);
        Assert.Contains("post('updateHotkey', { gesture })", script, StringComparison.Ordinal);
        Assert.Contains("event.key === 'Escape'", script, StringComparison.Ordinal);
        Assert.Contains("event.code.startsWith('Digit')", script, StringComparison.Ordinal);
        Assert.Contains(".hotkey-command:focus-visible", css, StringComparison.Ordinal);
        Assert.Contains(".hotkey-error:empty", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Sidebar_ExposesAccessibleAltOverlayModeSetting()
    {
        var webRoot = WebRoot();
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var css = File.ReadAllText(Path.Combine(webRoot, "app.css"));

        var renderAltOverlayMode = SliceBetween(
            script,
            "function renderAltOverlayMode()",
            "function updateAltOverlayMode");
        var updateAltOverlayMode = SliceBetween(
            script,
            "function updateAltOverlayMode(mode)",
            "function post(");
        var renderSettings = SliceBetween(
            script,
            "function renderSettings()",
            "function render()");
        var initialize = SliceBetween(
            script,
            "async function initialize()",
            "initialize().catch");
        var altModeControlCss = SliceBetween(
            css,
            ".segmented-control.alt-mode-control {",
            ".alt-mode-control button {");
        var altModeButtonCss = SliceBetween(
            css,
            ".alt-mode-control button {",
            ".navigation-table {");

        Assert.Contains("Alt 隐藏方式", html, StringComparison.Ordinal);
        AssertTagContains(html, "button", "altModeHold", "type=\"button\"");
        AssertTagContains(html, "button", "altModeHold", "aria-pressed=\"false\"");
        AssertTagContains(html, "button", "altModeToggle", "type=\"button\"");
        AssertTagContains(html, "button", "altModeToggle", "aria-pressed=\"false\"");
        Assert.Contains(
            "<div class=\"segmented-control alt-mode-control\" role=\"group\" aria-label=\"Alt 隐藏方式\">",
            html,
            StringComparison.Ordinal);

        Assert.Contains("displayedSettings()?.altOverlayMode", renderAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("|| 'HoldToHide'", renderAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("['altModeHold', 'HoldToHide']", renderAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("['altModeToggle', 'ToggleOnPress']", renderAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("classList.toggle('selected', selected)", renderAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("setAttribute('aria-pressed', String(selected))", renderAltOverlayMode, StringComparison.Ordinal);

        Assert.Contains("const settings = editableSettings();", updateAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("settings.altOverlayMode === mode) return;", updateAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("settings.altOverlayMode = mode;", updateAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("submitSettings(settings);", updateAltOverlayMode, StringComparison.Ordinal);
        Assert.Contains("renderAltOverlayMode();", renderSettings, StringComparison.Ordinal);
        Assert.Contains(
            "$('altModeHold').addEventListener('click', () => updateAltOverlayMode('HoldToHide'))",
            initialize,
            StringComparison.Ordinal);
        Assert.Contains(
            "$('altModeToggle').addEventListener('click', () => updateAltOverlayMode('ToggleOnPress'))",
            initialize,
            StringComparison.Ordinal);

        Assert.Contains(".alt-mode-setting", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr));", altModeControlCss, StringComparison.Ordinal);
        Assert.Contains("width: 100%;", altModeControlCss, StringComparison.Ordinal);
        Assert.Contains("height: 32px;", altModeControlCss, StringComparison.Ordinal);
        Assert.Contains("min-width: 0;", altModeButtonCss, StringComparison.Ordinal);
        Assert.Contains("font-size: 10px;", altModeButtonCss, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap;", altModeButtonCss, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasNavigationPage_ExposesIndependentTabControlsAndScrollableTable()
    {
        var webRoot = WebRoot();
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var css = File.ReadAllText(Path.Combine(webRoot, "app.css"));

        Assert.Contains("data-page=\"atlasNavigation\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"atlasNavigation\"", html, StringComparison.Ordinal);
        Assert.Contains("data-reset=\"atlasNavigation\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"navigationStatus\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"navigationSearch\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"navigationModeNearest\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"navigationModeAll\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"navigationHideCompletedToggle\"", html, StringComparison.Ordinal);
        Assert.Contains("隐藏已完成地图", html, StringComparison.Ordinal);
        Assert.Contains("id=\"clearNavigationRules\"", html, StringComparison.Ordinal);
        Assert.Contains("地图名称", html, StringComparison.Ordinal);
        Assert.Contains("数量", html, StringComparison.Ordinal);
        Assert.Contains("高亮", html, StringComparison.Ordinal);
        Assert.Contains("路线", html, StringComparison.Ordinal);
        Assert.Contains("方向", html, StringComparison.Ordinal);
        Assert.Contains("清空选择", html, StringComparison.Ordinal);
        Assert.Contains(".navigation-table-body", css, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", css, StringComparison.Ordinal);
        Assert.Contains("text-overflow: ellipsis", css, StringComparison.Ordinal);
        Assert.Contains(
            ".navigation-toolbar .search-wrap { border-radius: 3px; }",
            css,
            StringComparison.Ordinal);
        Assert.Contains(
            ".navigation-toolbar .secondary-button { border-radius: 3px; }",
            css,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasNavigationSearch_RemainsLocalAcrossSnapshotsAndSubmissions()
    {
        var script = NormalizeLineEndings(File.ReadAllText(
            Path.Combine(WebRoot(), "app.js")));

        Assert.Contains("let navigationSearch = '';", script, StringComparison.Ordinal);
        Assert.Contains(
            "$('navigationSearch').addEventListener('input', event => {\n"
            + "    navigationSearch = event.target.value;\n"
            + "    renderNavigation();\n"
            + "  });",
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            script.Split("navigationSearch =", StringSplitOptions.None).Length - 1);

        var handlerStart = script.IndexOf(
            "$('navigationSearch').addEventListener('input'",
            StringComparison.Ordinal);
        Assert.True(handlerStart >= 0);
        var handlerEnd = script.IndexOf("  });", handlerStart, StringComparison.Ordinal);
        Assert.True(handlerEnd > handlerStart);
        var handler = script[handlerStart..handlerEnd];
        Assert.DoesNotContain("submitSettings", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("post(", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasNavigationRows_MergeCatalogAndSavedRulesWithoutHtmlMapNames()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot(), "app.js"));

        Assert.Contains("function mergeNavigationEntries", script, StringComparison.Ordinal);
        Assert.Contains("runtime?.navigationCatalog?.entries", script, StringComparison.Ordinal);
        Assert.Contains("settings.navigation.rules", script, StringComparison.Ordinal);
        Assert.Contains("当前未出现", script, StringComparison.Ordinal);
        Assert.Contains("'上次读取'", script, StringComparison.Ordinal);
        Assert.Contains("active !== right.active", script, StringComparison.Ordinal);
        Assert.Contains("name.textContent = entry.displayName", script, StringComparison.Ordinal);
        Assert.DoesNotContain("innerHTML = entry.displayName", script, StringComparison.Ordinal);
        Assert.Contains("input.type = 'checkbox'", script, StringComparison.Ordinal);
        Assert.Contains("delete settings.navigation.rules[entry.ruleName]", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasNavigationSettings_UseStableRuleOrderAndPageOnlyReset()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot(), "app.js"));
        var source = File.ReadAllText(Path.Combine(
            TestPaths.WorkspaceRoot,
            "src",
            "FreiAtlas.Settings",
            "MainWindow.xaml.cs"));

        Assert.Contains("function stableSettings", script, StringComparison.Ordinal);
        Assert.Contains("Object.entries(stable.navigation.rules)", script, StringComparison.Ordinal);
        Assert.Contains(".sort(([left], [right])", script, StringComparison.Ordinal);
        Assert.Contains(
            "\"atlasNavigation\" => current with { Navigation = defaults.Navigation },",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AtlasNavigationHideCompletedToggleFiltersRowsAndPersistsSetting()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot(), "app.js"));

        Assert.Contains("settings.navigation.hideCompletedMaps", script, StringComparison.Ordinal);
        Assert.Contains("entry.incompleteNodeCount", script, StringComparison.Ordinal);
        Assert.Contains("setToggle($('navigationHideCompletedToggle')", script, StringComparison.Ordinal);
        Assert.Contains("$('navigationHideCompletedToggle').addEventListener('click'", script, StringComparison.Ordinal);
        Assert.Contains("submitSettings(settings)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayButton_ShowsDisabledStoppingStateAndLocksProcessSelection()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot(), "app.js"));

        Assert.Contains(
            "const stopping = Boolean(runtime?.isOverlayStopping);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "overlayButton.disabled = stopping || (!running && !runtime?.selectedProcessId);",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "? '<i data-lucide=\"loader-circle\"></i><span>正在停止</span>'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "$('processSelect').disabled = running || stopping;",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Boolean(runtime.isOverlayRunning) || Boolean(runtime.isOverlayStopping)",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayButton_UsesSingleMapOverlayToggleAndExistingProtocol()
    {
        var script = File.ReadAllText(Path.Combine(WebRoot(), "app.js"));

        Assert.Contains("<span>启动地图覆盖层</span>", script, StringComparison.Ordinal);
        Assert.Contains("<span>停止地图覆盖层</span>", script, StringComparison.Ordinal);
        Assert.Contains(
            "post(runtime?.isOverlayRunning ? 'stopOverlay' : 'startOverlay')",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("startAreaOverlay", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stopAreaOverlay", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentCatalog_ReferencesOnlyExistingLocalIcons()
    {
        var webRoot = WebRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(webRoot, "assets", "content-catalog.json")));
        var entries = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal(66, entries.Length);
        foreach (var entry in entries)
        {
            var iconPath = entry.GetProperty("iconPath").GetString();
            Assert.False(string.IsNullOrWhiteSpace(iconPath));
            Assert.True(
                File.Exists(Path.Combine(
                    webRoot,
                    iconPath!.Replace('/', Path.DirectorySeparatorChar))),
                $"Missing local icon: {iconPath}");
        }
    }

    private static string WebRoot()
        => Path.Combine(
            TestPaths.WorkspaceRoot,
            "src", "FreiAtlas.Settings", "Web");

    private static void AssertNumericInput(
        string html,
        string id,
        string type,
        string minimum,
        string maximum,
        string step)
    {
        var start = html.IndexOf($"<input id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing input '{id}'.");
        var end = html.IndexOf('>', start);
        Assert.True(end >= 0, $"Input '{id}' has no closing bracket.");
        var input = html[start..(end + 1)];
        Assert.Contains($"type=\"{type}\"", input, StringComparison.Ordinal);
        Assert.Contains($"min=\"{minimum}\"", input, StringComparison.Ordinal);
        Assert.Contains($"max=\"{maximum}\"", input, StringComparison.Ordinal);
        Assert.Contains($"step=\"{step}\"", input, StringComparison.Ordinal);
    }

    private static void AssertTagContains(
        string html,
        string tagName,
        string id,
        string expected)
    {
        var start = html.IndexOf($"<{tagName} id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing {tagName} '{id}'.");
        var end = html.IndexOf('>', start);
        Assert.True(end >= 0, $"Tag '{id}' has no closing bracket.");
        Assert.Contains(expected, html[start..(end + 1)], StringComparison.Ordinal);
    }

    private static string SliceBetween(
        string source,
        string start,
        string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start boundary '{start}'.");
        var endIndex = source.IndexOf(
            end,
            startIndex + start.Length,
            StringComparison.Ordinal);
        Assert.True(endIndex >= 0, $"Missing end boundary '{end}'.");
        return source[startIndex..endIndex];
    }

    private static string NormalizeLineEndings(string value)
        => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
