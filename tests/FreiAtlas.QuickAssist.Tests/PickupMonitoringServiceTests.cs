using System.Numerics;
using FreiAtlas.Core.Pickup;

namespace FreiAtlas.QuickAssist.Tests;

public sealed class PickupMonitoringServiceTests
{
    [Fact]
    public void Disabled_DoesNotCreateCandidateSource()
    {
        var created = 0;
        var service = new PickupMonitoringService(
            _ => { created++; return new FakeSource(); },
            _ => true);

        var result = service.ScanOnce(1234);

        Assert.Equal(0, created);
        Assert.False(result.EligibleTarget);
        Assert.Equal("未启用", result.Message);
    }

    [Fact]
    public void NotForeground_DoesNotScanGameMemory()
    {
        var created = 0;
        var service = new PickupMonitoringService(
            _ => { created++; return new FakeSource(); },
            _ => false);
        service.Configure(new PickupSettings { Enabled = true, Whitelist = ["Divine Orb"] });

        var result = service.ScanOnce(1234);

        Assert.Equal(0, created);
        Assert.Equal("等待游戏位于前台", result.Message);
    }

    [Fact]
    public void ModifierKeyRequired_NotPressed_DoesNotClick()
    {
        var clicked = false;
        var source = new FakeSource(new PickupCandidateScan(
            true,
            Vector2.Zero,
            [new PickupItemCandidate(1, "崇高石", "CurrencyAddModToRare", new(2, 0), 2)],
            null,
            Labels: [new GroundItemLabel(0x1000, "崇高石", "崇高石", new(100, 50), new(500, 400), new(550, 425))]));

        var service = new PickupMonitoringService(
            _ => source,
            _ => true,
            tryClickClientPoint: (_, _) => { clicked = true; return true; },
            isModifierKeyPressed: _ => false,
            getClientSize: _ => new Vector2(1920, 1080));

        service.Configure(new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石"],
            PickupRangeGrid = 8,
            ModifierKeyEnabled = true,
            ModifierKey = "Shift"
        });

        var result = service.ScanOnce(1234);

        Assert.False(clicked);
        Assert.False(result.EligibleTarget);
        Assert.Contains("门禁未满足", result.Message);
    }

    [Fact]
    public void InRangeWhitelistedItem_WithLabel_ClicksExactLabelCenter()
    {
        Vector2? clickedPoint = null;
        var labelCenter = new Vector2(550, 425);
        var source = new FakeSource(new PickupCandidateScan(
            true,
            Vector2.Zero,
            [new PickupItemCandidate(10, "崇高石", "CurrencyAddModToRare", new(3, 0), 3)],
            null,
            Labels: [new GroundItemLabel(0x2000, "崇高石", "崇高石", new(100, 50), new(500, 400), labelCenter)]));

        var service = new PickupMonitoringService(
            _ => source,
            _ => true,
            tryClickClientPoint: (_, pt) => { clickedPoint = pt; return true; },
            getClientSize: _ => new Vector2(1920, 1080));

        service.Configure(new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石"],
            PickupRangeGrid = 8
        });

        var result = service.ScanOnce(1234);

        Assert.True(result.EligibleTarget);
        Assert.Equal(10u, result.EntityId);
        Assert.Equal("崇高石", result.ItemName);
        Assert.Equal(labelCenter, clickedPoint);
        Assert.Contains("已顺手拾取目标", result.Message);
    }

    [Fact]
    public void AwaitingDisappearance_ItemDisappears_ResetsToIdle()
    {
        var clickCount = 0;
        var labelCenter = new Vector2(550, 425);
        var itemPresent = true;

        var source = new DynamicSource(() =>
        {
            if (itemPresent)
            {
                return new PickupCandidateScan(
                    true,
                    Vector2.Zero,
                    [new PickupItemCandidate(10, "崇高石", "CurrencyAddModToRare", new(3, 0), 3)],
                    null,
                    Labels: [new GroundItemLabel(0x2000, "崇高石", "崇高石", new(100, 50), new(500, 400), labelCenter)]);
            }
            return new PickupCandidateScan(true, Vector2.Zero, [], null, Labels: []);
        });

        var service = new PickupMonitoringService(
            _ => source,
            _ => true,
            tryClickClientPoint: (_, _) => { clickCount++; return true; },
            getClientSize: _ => new Vector2(1920, 1080));

        service.Configure(new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石"],
            PickupRangeGrid = 8,
            DisappearTimeoutMilliseconds = 800
        });

        // 第一次扫描：触发拾取
        var result1 = service.ScanOnce(1234);
        Assert.Equal(1, clickCount);
        Assert.Contains("已顺手拾取目标", result1.Message);

        // 第二次扫描：物品尚未消失，处于等待中，不应重复触发点击
        var result2 = service.ScanOnce(1234);
        Assert.Equal(1, clickCount);
        Assert.Contains("等待物品拾取消失", result2.Message);

        // 第三次扫描：物品已消失，闭环完成回到待机
        itemPresent = false;
        var result3 = service.ScanOnce(1234);
        Assert.Equal(1, clickCount);
        Assert.Contains("拾取范围内没有白名单物品", result3.Message);
    }

    [Fact]
    public void OutOfRangeWhitelistedItem_IsNotReportedAsEligible()
    {
        var source = new FakeSource(new PickupCandidateScan(
            true,
            Vector2.Zero,
            [new PickupItemCandidate(7, "崇高石", "CurrencyAddModToRare", new(15, 0), 15)],
            null,
            Labels: [new GroundItemLabel(0x2000, "崇高石", "崇高石", new(100, 50), new(500, 400), new(550, 425))]));

        var service = new PickupMonitoringService(
            _ => source,
            _ => true,
            getClientSize: _ => new Vector2(1920, 1080));

        service.Configure(new PickupSettings
        {
            Enabled = true,
            Whitelist = ["CurrencyAddModToRare"],
            PickupRangeGrid = 8
        });

        var result = service.ScanOnce(1234);

        Assert.False(result.EligibleTarget);
        Assert.Null(result.EntityId);
        Assert.Contains("没有白名单物品", result.Message);
    }

    [Fact]
    public void HoveredWhitelistedItem_TriggersClickCurrentCursor()
    {
        var cursorClicked = false;
        var clientPointClicked = false;
        var source = new FakeSource(new PickupCandidateScan(
            true,
            Vector2.Zero,
            [new PickupItemCandidate(99, "崇高石", "CurrencyAddModToRare", new(2, 0), 2)],
            null,
            HoveredGroundItemEntityId: 99,
            Labels: [new GroundItemLabel(0x2000, "崇高石", "崇高石", new(100, 50), new(500, 400), new(550, 425))]));

        var service = new PickupMonitoringService(
            _ => source,
            _ => true,
            tryClickClientPoint: (_, _) => { clientPointClicked = true; return true; },
            clickCurrentCursor: _ => { cursorClicked = true; return true; });

        service.Configure(new PickupSettings
        {
            Enabled = true,
            Whitelist = ["崇高石"],
            PickupRangeGrid = 8
        });

        var result = service.ScanOnce(1234);

        Assert.True(cursorClicked);
        Assert.False(clientPointClicked);
        Assert.Equal(99u, result.EntityId);
        Assert.Contains("悬停拾取目标", result.Message);
    }

    private sealed class FakeSource(PickupCandidateScan? scan = null) : IPickupCandidateSource
    {
        public int ScanCount { get; private set; }
        public PickupCandidateScan Scan()
        {
            ScanCount++;
            return scan ?? new PickupCandidateScan(true, Vector2.Zero, [], null);
        }
        public void Dispose() { }
    }

    private sealed class DynamicSource(Func<PickupCandidateScan> scanFactory) : IPickupCandidateSource
    {
        public PickupCandidateScan Scan() => scanFactory();
        public void Dispose() { }
    }
}
