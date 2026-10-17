using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Server;
using Aiursoft.AiurProtocol.Server.Attributes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Aiursoft.AlertCenter.Filters;

public sealed class MessageReportExceptionHandlerAttribute : ApiExceptionHandler
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            context.Result = PayloadTooLarge(context.HttpContext);
            context.ExceptionHandled = true;
            return;
        }
        base.OnException(context);
    }

    public static JsonResult PayloadTooLarge(HttpContext context)
    {
        var result = (JsonResult)context.Protocol(new AiurCollection<string>(["The request body exceeds 64 KiB."])
        {
            Code = Code.InvalidInput,
            Message = "The request body exceeds 64 KiB."
        });
        result.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return result;
    }
}
