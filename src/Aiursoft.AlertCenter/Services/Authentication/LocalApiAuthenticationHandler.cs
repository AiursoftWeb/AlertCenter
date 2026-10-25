using System.Text.Encodings.Web;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Server;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Services.Authentication;

public sealed class LocalApiAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    LocalApiTokenService tokenService,
    UserManager<User> userManager,
    IUserClaimsPrincipalFactory<User> principalFactory,
    IOptions<AppSettings> settings)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!settings.Value.LocalEnabled) return AuthenticateResult.NoResult();
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorization["Bearer ".Length..].Trim();
        if (!token.StartsWith(LocalApiAuthenticationDefaults.TokenPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }
        if (!tokenService.TryRead(token, out var userId))
        {
            return AuthenticateResult.Fail("The local API token is invalid or expired.");
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user == null)
        {
            return AuthenticateResult.Fail("The local API user no longer exists.");
        }

        var principal = await principalFactory.CreateAsync(user);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => WriteChallengeAsync(Context);

    internal static async Task WriteChallengeAsync(HttpContext context)
    {
        if (context.Response.HasStarted) return;
        context.Response.Headers.CacheControl = "no-store";
        var result = context.Protocol(new AiurResponse { Code = Code.WrongKey, Message = "Your session is invalid or expired. Sign in again." });
        await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
    }
}
