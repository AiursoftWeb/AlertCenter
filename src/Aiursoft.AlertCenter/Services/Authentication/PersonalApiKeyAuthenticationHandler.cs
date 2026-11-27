using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Server;
using Aiursoft.AlertCenter.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Services.Authentication;

public sealed class PersonalApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TemplateDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PersonalApiKey";
    public const string HandlePolicy = "PersonalApiKeyHandleIncidents";
    internal const string HandleClaim = "alertcenter:personal:handle-incidents";
    private const string Prefix = "acpat_";

    internal static string Issue(Guid id) => $"{Prefix}{id:N}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}";
    internal static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!AuthenticationHeaderValue.TryParse(Request.Headers.Authorization, out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { } token || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        if (token.Length != Prefix.Length + 32 + 1 + 64 || token[Prefix.Length + 32] != '.' ||
            !Guid.TryParseExact(token.AsSpan(Prefix.Length, 32), "N", out var id) ||
            token.AsSpan(Prefix.Length + 33).IndexOfAnyExcept("0123456789ABCDEF") >= 0)
            return AuthenticateResult.Fail("Invalid personal API key.");

        var now = DateTime.UtcNow;
        var key = await db.PersonalApiKeys.AsNoTracking().NotCacheable()
            .Where(item => item.Id == id && item.ExpiresAtUtc > now)
            .Select(item => new
            {
                item.SecretHash, item.UserId, item.User.UserName, item.CanHandleIncidents,
                item.User.LockoutEnabled, item.User.LockoutEnd
            })
            .SingleOrDefaultAsync(Context.RequestAborted);
        if (key == null || (key.LockoutEnabled && key.LockoutEnd > now) ||
            !CryptographicOperations.FixedTimeEquals(key.SecretHash, Hash(token)))
            return AuthenticateResult.Fail("Invalid or expired personal API key.");

        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, key.UserId)], SchemeName);
        if (key.UserName != null) identity.AddClaim(new Claim(ClaimTypes.Name, key.UserName));
        if (key.CanHandleIncidents) identity.AddClaim(new Claim(HandleClaim, "true"));
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProtocolAsync(Code.WrongKey, "The personal API key is invalid, expired, or revoked.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProtocolAsync(Code.Unauthorized, "This personal API key does not permit incident updates.");

    private Task WriteProtocolAsync(Code code, string message)
    {
        Response.Headers.CacheControl = "no-store";
        return Context.Protocol(new AiurResponse { Code = code, Message = message })
            .ExecuteResultAsync(new ActionContext(Context, new RouteData(), new ActionDescriptor()));
    }
}
