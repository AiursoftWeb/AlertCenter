using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Services.FileStorage;
using Microsoft.AspNetCore.Mvc;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class FileOriginPolicyTests
{
    [TestMethod]
    public async Task ApplicationDefaultsAllowVerifiedSvgOnlyInsideProjectLogoFolder()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../src/Aiursoft.AlertCenter/appsettings.json"))).Build();
        var policy = new FileDeliveryPolicy(configuration);
        var path = Path.Combine(Path.GetTempPath(), $"logo-policy-{Guid.NewGuid():N}.svg");
        try
        {
            await File.WriteAllTextAsync(path, "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>");
            Assert.IsTrue(policy.TryGetVerifiedInlineMediaType("project-logo/logo.svg", path, out var type));
            Assert.AreEqual("image/svg+xml", type);
            Assert.IsFalse(policy.TryGetVerifiedInlineMediaType("other/logo.svg", path, out _));
            Assert.IsFalse(policy.TryGetVerifiedInlineMediaType("project-logo-other/logo.svg", path, out _));
            await File.WriteAllTextAsync(path, "<html>Not an SVG image</html>");
            Assert.IsFalse(policy.TryGetVerifiedInlineMediaType("project-logo/logo.svg", path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ArbitraryContentRenderingIsDisabledByDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:PublicOrigin"] = "https://files.example.com",
                ["Storage:RequireDedicatedInlineOrigin"] = "true"
            })
            .Build();
        var policy = new FileDeliveryPolicy(configuration);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("files.example.com");

        Assert.IsTrue(policy.CanRenderInline(context.Request));
        Assert.IsFalse(policy.CanRenderArbitraryContentInline(context.Request));
    }

    [TestMethod]
    public void InlineRenderingRequiresTheConfiguredFileOrigin()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:PublicOrigin"] = "https://files.example.com",
                ["Storage:RequireDedicatedInlineOrigin"] = "true",
                ["Storage:AllowArbitraryInlineOnDedicatedOrigin"] = "true"
            })
            .Build();
        var policy = new FileDeliveryPolicy(configuration);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.com");

        Assert.IsFalse(policy.CanRenderInline(context.Request));
        Assert.IsFalse(policy.CanRenderArbitraryContentInline(context.Request));

        context.Request.Host = new HostString("files.example.com");
        Assert.IsTrue(policy.CanRenderInline(context.Request));
        Assert.IsTrue(policy.CanRenderArbitraryContentInline(context.Request));
    }

    [TestMethod]
    public void IsolatedOriginInlineFileUsesTheMappedContentTypeWithoutRestrictiveCsp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.html");
        File.WriteAllText(path, "<script>alert(document.domain)</script>");

        try
        {
            var context = new DefaultHttpContext();
            var controller = new TestController
            {
                ControllerContext = new ControllerContext { HttpContext = context }
            };

            var result = controller.IsolatedOriginInlineFile(path);

            var file = result as PhysicalFileResult;
            Assert.IsNotNull(file);
            Assert.AreEqual("text/html", file.ContentType);
            Assert.StartsWith("inline", context.Response.Headers.ContentDisposition.Single());
            Assert.AreEqual("nosniff", context.Response.Headers.XContentTypeOptions.Single());
            Assert.IsFalse(context.Response.Headers.ContainsKey("Content-Security-Policy"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class TestController : ControllerBase;
}
