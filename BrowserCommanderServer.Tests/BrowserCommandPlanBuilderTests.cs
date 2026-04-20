using BrowserCommander.Contracts;
using BrowserCommanderServer;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class BrowserCommandPlanBuilderTests
{
    [Fact]
    public void LocatorPress_CreatesFocusThenDebuggerPressKeyPlan()
    {
        var plan = BrowserCommandPlanBuilder.LocatorPress("#press-target", "Enter");

        Assert.Collection(
            plan.Steps,
            focusStep =>
            {
                Assert.Equal(BrowserExecutionStepKinds.ContentScript, focusStep.Kind);
                Assert.Equal(BrowserExecutionOperations.FocusLocator, focusStep.Operation);
                Assert.Equal("#press-target", focusStep.Selector);
            },
            keyStep =>
            {
                Assert.Equal(BrowserExecutionStepKinds.Debugger, keyStep.Kind);
                Assert.Equal(BrowserExecutionOperations.PressKey, keyStep.Operation);
                Assert.Equal("Enter", keyStep.Key);
            });
    }

    [Fact]
    public void LocatorDragTo_CreatesDebuggerPlanWithExpectedArguments()
    {
        var plan = BrowserCommandPlanBuilder.LocatorDragTo("#source", "#target", "right", 8);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(BrowserExecutionStepKinds.Debugger, step.Kind);
        Assert.Equal(BrowserExecutionOperations.DragLocatorTo, step.Operation);
        Assert.Equal("#source", step.SourceSelector);
        Assert.Equal("#target", step.TargetSelector);
        Assert.Equal("right", step.Button);
        Assert.Equal(8, step.MoveSteps);
    }

    [Fact]
    public void PageWaitForUrl_CreatesTabPlanWithUrlAndMatchMode()
    {
        var plan = BrowserCommandPlanBuilder.PageWaitForUrl("https://example.test/path", "contains");

        var step = Assert.Single(plan.Steps);
        Assert.Equal(BrowserExecutionStepKinds.Tab, step.Kind);
        Assert.Equal(BrowserExecutionOperations.WaitForUrl, step.Operation);
        Assert.Equal("https://example.test/path", step.Url);
        Assert.Equal("contains", step.MatchMode);
    }
}
