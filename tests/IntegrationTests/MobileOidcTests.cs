using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Aiursoft.DbTools;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.SDK.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using static Aiursoft.WebTools.Extends;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public sealed class MobileOidcTests
{
    [TestMethod]
    public async Task MobileOidcValidatesTokensMapsImmutableUserAndKeepsLocalLoginDisabled()
    {
        using var identityReservation = new TestPortReservation();
        using var apiReservation = new TestPortReservation();
        var authority = $"http://127.0.0.1:{identityReservation.Port}";
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("N") };
        await using var identity = BuildIdentityProvider(identityReservation, authority, key, rsa);
        await identity.StartAsync();
        await using var api = await AppAsync<Startup>([
            "--AppSettings:AuthProvider=OIDC",
            "--AppSettings:OIDC:Authority=https://web-provider.invalid",
            $"--AppSettings:OIDC:MobileAuthority={authority}",
            "--AppSettings:OIDC:RequireHttpsMetadata=false",
            "--AppSettings:OIDC:ClientId=test-web",
            "--AppSettings:OIDC:ClientSecret=do-not-disclose",
            "--AppSettings:OIDC:MobileClientId=test-android",
            "--AppSettings:OIDC:ApiAudience=test-android"
        ], port: apiReservation.Port);
        apiReservation.ConfigureServer(api);
        await api.UpdateDbAsync<TemplateDbContext>();
        await api.StartAsync();
        using var http = new HttpClient();
        http.BaseAddress = new Uri($"http://127.0.0.1:{apiReservation.Port}");
        var subject = $"mobile-{Guid.NewGuid():N}";
        string Token(string issuer, string audience, SecurityKey signingKey, bool expired = false, bool profile = true) =>
            new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer, audience,
                profile ? [new Claim("sub", subject), new Claim("preferred_username", "mobile-user"),
                    new Claim("name", "Mobile User"), new Claim("email", $"{subject}@example.com")]
                    : [new Claim("sub", subject)],
                DateTime.UtcNow.AddHours(-1), expired ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow.AddMinutes(10),
                new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));
        static async Task<HttpResponseMessage> Get(HttpClient client, string token, string path = "/api/v1/mobile/me")
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await client.SendAsync(request);
        }
        try
        {
            var configText = await http.GetStringAsync("/api/v1/config");
            Assert.DoesNotContain("do-not-disclose", configText);
            var configuration = await http.GetFromJsonAsync<MobileConfigurationResponse>("/api/v1/config");
            Assert.IsNotNull(configuration);
            Assert.AreEqual(authority, configuration.Authority);
            Assert.AreEqual("test-android", configuration.ClientId);
            Assert.AreEqual("com.aiursoft.alertcenter:/oauth2redirect", configuration.RedirectUri);
            CollectionAssert.Contains(configuration.Scopes, "offline_access");
            using var local = await http.PostAsJsonAsync("/api/v1/auth/local/login", new LocalLoginRequest
                { EmailOrUserName = "admin@default.com", Password = "Admin@123456!" });
            Assert.AreEqual(HttpStatusCode.Unauthorized, local.StatusCode);
            using var otherRsa = RSA.Create(2048);
            var otherKey = new RsaSecurityKey(otherRsa) { KeyId = key.KeyId };
            foreach (var invalid in new[] { Token(authority + "/wrong", "test-android", key),
                Token(authority, "test-web", key), Token(authority, "test-android", key, expired: true),
                Token(authority, "test-android", otherKey), Token(authority, "test-android", key, profile: false) })
            {
                using var rejected = await Get(http, invalid);
                Assert.AreEqual(HttpStatusCode.Unauthorized, rejected.StatusCode);
            }
            using var accepted = await Get(http, Token(authority, "test-android", key));
            accepted.EnsureSuccessStatusCode();
            Assert.AreEqual("Mobile User", (await accepted.Content.ReadFromJsonAsync<MobileUserResponse>())!.DisplayName);
            using (var scope = api.Services.CreateScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
                var user = await manager.FindByLoginAsync("OpenIdConnect", subject);
                Assert.IsNotNull(user);
                var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                db.SourceApplications.Add(new SourceApplication { Name = "Owned from mobile", OwnerId = user.Id });
                db.SourceApplications.Add(new SourceApplication { Name = "Not shared" });
                await db.SaveChangesAsync();
            }
            using var restored = await Get(http, Token(authority, "test-android", key, profile: false), "/api/v1/mobile/applications");
            restored.EnsureSuccessStatusCode();
            var applications = await restored.Content.ReadFromJsonAsync<MobileApplicationsResponse>();
            Assert.AreEqual("Owned from mobile", applications!.Applications.Single().Name);
        }
        finally
        {
            await api.StopAsync();
            await identity.StopAsync();
        }
    }

    private static WebApplication BuildIdentityProvider(
        TestPortReservation reservation,
        string authority,
        RsaSecurityKey signingKey,
        RSA rsa)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{reservation.Port}");
        var app = builder.Build();
        reservation.ConfigureServer(app);
        app.MapGet("/.well-known/openid-configuration", () => new
        {
            issuer = authority,
            authorization_endpoint = $"{authority}/authorize",
            token_endpoint = $"{authority}/token",
            jwks_uri = $"{authority}/jwks",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" }
        });
        app.MapGet("/jwks", () =>
        {
            var parameters = rsa.ExportParameters(false);
            return new
            {
                keys = new[]
                {
                    new
                    {
                        kty = "RSA",
                        use = "sig",
                        kid = signingKey.KeyId,
                        alg = "RS256",
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent)
                    }
                }
            };
        });
        return app;
    }
}
