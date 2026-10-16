using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Localization;

namespace Aiursoft.AlertCenter.Services;

public class ActionFeedbackFilter(IUrlHelperFactory urlHelperFactory, IStringLocalizer<ActionFeedbackFilter> localizer) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!HttpMethods.IsPost(request.Method) || !request.HasFormContentType ||
            request.Headers["X-AlertCenter-Dialog"] != "true" || request.Path.StartsWithSegments("/api"))
        {
            await next();
            return;
        }
        var urls = urlHelperFactory.GetUrlHelper(context);
        if (context.Result is RedirectToActionResult redirect)
        {
            var url = urls.Action(new UrlActionContext
            {
                Action = redirect.ActionName, Controller = redirect.ControllerName,
                Values = redirect.RouteValues, Fragment = redirect.Fragment
            });
            if (url != null && urls.IsLocalUrl(url)) context.Result = new JsonResult(new { redirectUrl = url });
        }
        else if (context.Result is ObjectResult { Value: string message, StatusCode: 400 or 409 } error)
        {
            context.Result = new JsonResult(new { message, refresh = error.StatusCode == 409 }) { StatusCode = error.StatusCode };
        }
        else if (context.Result is ViewResult && !context.ModelState.IsValid)
        {
            var messages = context.ModelState.Values.SelectMany(value => value.Errors)
                .Select(modelError => string.IsNullOrWhiteSpace(modelError.ErrorMessage) ? localizer["Invalid input. Please check and try again."].Value : modelError.ErrorMessage).Distinct();
            context.Result = new JsonResult(new { message = string.Join('\n', messages), refresh = false }) { StatusCode = 400 };
        }
        await next();
    }
}
