using Aiursoft.AlertCenter.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class IncidentSearchProtectionTests
{
    [TestMethod]
    public async Task ExcessConcurrentSearchIsRejectedAndSlotsAreReleasedAfterFailures()
    {
        using var filter = new IncidentSearchRequestFilter();
        var entered = new CountdownEvent(4);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Enumerable.Range(0, 4).Select(_ =>
        {
            var context = Context();
            return filter.OnActionExecutionAsync(context, async () =>
            {
                entered.Signal();
                await release.Task;
                return Executed(context);
            });
        }).ToArray();
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
        var excess = Context();
        var called = false;
        try
        {
            await filter.OnActionExecutionAsync(excess, () =>
            {
                called = true;
                return Task.FromResult(Executed(excess));
            });
            Assert.IsFalse(called);
            Assert.AreEqual(StatusCodes.Status429TooManyRequests, ((StatusCodeResult)excess.Result!).StatusCode);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(running);
            entered.Dispose();
        }
        var failure = Context();
        await Assert.ThrowsAsync<InvalidOperationException>(() => filter.OnActionExecutionAsync(failure,
            () => throw new InvalidOperationException("Test failure")));
        var next = Context();
        await filter.OnActionExecutionAsync(next, () => Task.FromResult(Executed(next)));
        Assert.IsNull(next.Result);
    }

    private static ActionExecutingContext Context() => new(
        new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
        new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());

    private static ActionExecutedContext Executed(ActionExecutingContext context) => new(context, context.Filters, context.Controller);
}
