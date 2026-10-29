using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Aiursoft.AlertCenter.Services;

public sealed class IncidentSearchRequestFilter : IAsyncActionFilter, IDisposable
{
    private readonly SemaphoreSlim _slots = new(4, 4);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await _slots.WaitAsync(0, context.HttpContext.RequestAborted))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return;
        }
        try { await next(); }
        finally { _slots.Release(); }
    }

    public void Dispose() => _slots.Dispose();
}
