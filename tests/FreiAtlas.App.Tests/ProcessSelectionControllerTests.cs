using FreiAtlas.App.Processes;

namespace FreiAtlas.App.Tests;

public sealed class ProcessSelectionControllerTests
{
    [Fact]
    public void FirstScan_WithNoProcesses_RemainsUnbound()
    {
        var controller = new ProcessSelectionController();

        controller.ApplyScan([]);

        Assert.Null(controller.SelectedProcessId);
        Assert.Empty(controller.Processes);
    }

    [Fact]
    public void FirstScan_WithOneProcess_AutoSelectsButDoesNotStart()
    {
        var controller = new ProcessSelectionController();

        controller.ApplyScan([Process(123, "逐风者")]);

        Assert.Equal(123, controller.SelectedProcessId);
        Assert.False(controller.IsOverlayRunning);
    }

    [Fact]
    public void FirstScan_WithMultipleProcesses_WaitsForManualSelection()
    {
        var controller = new ProcessSelectionController();

        controller.ApplyScan([Process(123, "逐风者"), Process(456, "锻铁者")]);

        Assert.Null(controller.SelectedProcessId);
    }

    [Fact]
    public void LaterScan_AfterEmptyFirstScan_DoesNotAutoSelect()
    {
        var controller = new ProcessSelectionController();
        controller.ApplyScan([]);

        controller.ApplyScan([Process(123, "逐风者")]);

        Assert.Null(controller.SelectedProcessId);
    }

    [Fact]
    public void LaterScan_DoesNotAutoSwitchAfterSelectedProcessExits()
    {
        var controller = new ProcessSelectionController();
        controller.ApplyScan([Process(123, "逐风者")]);
        controller.MarkOverlayStarted();

        controller.ApplyScan([Process(456, "锻铁者")]);

        Assert.Equal(123, controller.SelectedProcessId);
        Assert.False(controller.IsOverlayRunning);
        var exited = Assert.Single(
            controller.Processes,
            process => process.ProcessId == 123);
        Assert.Equal(GameProcessState.Exited, exited.State);
    }

    [Fact]
    public void TrySelect_RequiresCurrentNonExitedCandidate()
    {
        var controller = new ProcessSelectionController();
        controller.ApplyScan([Process(123, "逐风者"), Process(456, "锻铁者")]);

        Assert.False(controller.TrySelect(999));
        Assert.True(controller.TrySelect(456));
        Assert.Equal(456, controller.SelectedProcessId);
    }

    [Fact]
    public void LaterScan_PreservesSelectionAndUpdatesCharacterName()
    {
        var controller = new ProcessSelectionController();
        controller.ApplyScan([Process(123, "逐风者")]);

        controller.ApplyScan([Process(123, "新角色")]);

        Assert.Equal(123, controller.SelectedProcessId);
        Assert.Equal("新角色", Assert.Single(controller.Processes).CharacterName);
    }

    [Fact]
    public void TrySelect_WhileOverlayIsRunning_RejectsDifferentProcess()
    {
        var controller = new ProcessSelectionController();
        controller.ApplyScan([Process(123, "逐风者"), Process(456, "锻铁者")]);
        Assert.True(controller.TrySelect(123));
        controller.MarkOverlayStarted();

        Assert.False(controller.TrySelect(456));
        Assert.Equal(123, controller.SelectedProcessId);
        Assert.True(controller.IsOverlayRunning);
    }

    [Fact]
    public void MarkOverlayStarted_WhenSelectedProcessExited_Throws()
    {
        var controller = new ProcessSelectionController();
        controller.ApplyScan([Process(123, "逐风者")]);
        controller.ApplyScan([]);

        Assert.Throws<InvalidOperationException>(
            controller.MarkOverlayStarted);
        Assert.False(controller.IsOverlayRunning);
    }

    private static GameProcessSnapshot Process(int processId, string characterName)
        => new(processId, characterName, GameProcessState.InGame, null);
}
