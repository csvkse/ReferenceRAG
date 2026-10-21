using ReferenceRAG.DesktopHost;
using Xunit;

namespace ReferenceRAG.Host.Tests;

public sealed class StartupWindowCoordinatorTests
{
    [Fact]
    public void SilentStart_DoesNotHideUntilFrontendIsReady()
    {
        var actions = new List<string>();
        var coordinator = Create(true, actions);
        coordinator.SetWindowHandle(42);
        Assert.Equal(new[] { "prepare:42" }, actions);
        coordinator.SignalReady();
        Assert.Equal(new[] { "prepare:42", "hide:42" }, actions);
        coordinator.SignalReady();
        Assert.Equal(2, actions.Count);
    }

    [Fact]
    public void NormalStart_RefreshesVisibleSurfaceAfterFrontendIsReady()
    {
        var actions = new List<string>();
        var coordinator = Create(false, actions);
        coordinator.SetWindowHandle(42);
        coordinator.SignalReady();
        Assert.Equal(new[] { "reveal:42" }, actions);
    }

    [Fact]
    public void UserShowBeforeReady_IsNotOverriddenByLateSilentHide()
    {
        var actions = new List<string>();
        var coordinator = Create(true, actions);
        coordinator.SetWindowHandle(42);
        coordinator.RequestShow();
        coordinator.SignalReady();
        Assert.DoesNotContain("hide:42", actions);
        Assert.Equal("reveal:42", actions.Last());
    }

    [Fact]
    public void ReadyBeforeHandle_DefersSurfaceOperationUntilValidHandle()
    {
        var actions = new List<string>();
        var coordinator = Create(false, actions);
        coordinator.SignalReady();
        Assert.Empty(actions);
        coordinator.SetWindowHandle(42);
        Assert.Equal(new[] { "reveal:42" }, actions);
    }

    [Fact]
    public void ZeroHandle_IsRejectedWithoutSurfaceOperation()
    {
        var actions = new List<string>();
        var coordinator = Create(true, actions);
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.SetWindowHandle(0));
        Assert.Empty(actions);
    }

    private static StartupWindowCoordinator Create(bool silent, List<string> actions) =>
        new(silent, h => actions.Add($"hide:{h}"), h => actions.Add($"reveal:{h}"), h => actions.Add($"prepare:{h}"));

    [Fact]
    public void Timeout_RevealsTransparentWindowAndLateReadyDoesNotHideIt()
    {
        var actions = new List<string>();
        var coordinator = Create(true, actions);
        coordinator.SetWindowHandle(42);
        Assert.True(coordinator.SignalTimeout());
        coordinator.SignalReady();
        Assert.False(coordinator.SignalTimeout());
        Assert.Equal(new[] { "prepare:42", "reveal:42" }, actions);
    }

    [Fact]
    public void NormalStart_NeverPreparesOrHidesWindow()
    {
        var actions = new List<string>();
        var coordinator = Create(false, actions);
        coordinator.SetWindowHandle(42);
        coordinator.SignalReady();
        Assert.DoesNotContain(actions, action => action.StartsWith("prepare:") || action.StartsWith("hide:"));
    }
}
