using System.Net;
using System.Text.RegularExpressions;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services.FileStorage;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

public partial class AlertDemoTests
{
    private async Task<Guid> CreateCommentIncident()
    {
        var key = await CreateApplication();
        using var report = await Report(key, Guid.NewGuid());
        using var scope = Server!.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().Incidents.SingleAsync()).Id;
    }

    [TestMethod]
    public async Task CommentFormsAcceptMarkdownInAllStatesAndUseInPageValidationAndDeletion()
    {
        var id = await CreateCommentIncident();
        var url = $"/Incidents/Details/{id}";
        var initial = await Http.GetStringAsync(url);
        Assert.Contains("/Incidents/AddComment", initial);
        Assert.Contains("name=\"Markdown\"", initial);
        Assert.Contains("/scripts/incident-comments.js", initial);
        Assert.Contains("/node_modules/@aiursoft/uistack-markdown-ui/dist/index.global.js", initial);
        Assert.Contains("/node_modules/monaco-editor/min/vs/loader.js", initial);
        Assert.IsTrue(initial.IndexOf("/uistack-markdown-ui/", StringComparison.Ordinal) < initial.IndexOf("/monaco-editor/", StringComparison.Ordinal));
        using var noToken = await PostForm("/Incidents/AddComment", new() { ["IncidentId"] = id.ToString(), ["Markdown"] = "CSRF" }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var get = await Http.GetAsync($"/Incidents/AddComment?IncidentId={id}");
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        foreach (var state in new[] { Incident.New, Incident.Acked, Incident.Mitigated, Incident.Resolved })
        {
            Guid version;
            using (var scope = Server!.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                var incident = await db.Incidents.SingleAsync();
                incident.Status = state;
                version = incident.Version;
                await db.SaveChangesAsync();
            }
            using var added = await DialogPost("/Incidents/AddComment", new()
            {
                ["IncidentId"] = id.ToString(), ["Markdown"] = "# " + state + "\n\n**Evidence**"
            }, url);
            Assert.AreEqual(HttpStatusCode.OK, added.StatusCode);
            Assert.AreEqual(url + "?commentsPage=1#incident-comments", JObject.Parse(await added.Content.ReadAsStringAsync())["redirectUrl"]!.Value<string>());
            using var scopeAfter = Server!.Services.CreateScope();
            var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TemplateDbContext>();
            Assert.AreEqual(version, (await dbAfter.Incidents.SingleAsync()).Version);
        }
        foreach (var invalid in new[] { "", "  \r\n", new string('x', 4001) })
        {
            using var rejected = await DialogPost("/Incidents/AddComment", new()
            {
                ["IncidentId"] = id.ToString(), ["Markdown"] = invalid
            }, url);
            Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.IsNotNull(JObject.Parse(await rejected.Content.ReadAsStringAsync())["message"]);
        }
        using var scopeFinal = Server!.Services.CreateScope();
        var finalDb = scopeFinal.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(4, await finalDb.IncidentComments.CountAsync());
        Assert.AreEqual(0, await finalDb.PushDeliveries.CountAsync());
        Assert.AreEqual(1, await finalDb.IncidentTimelineEntries.CountAsync());
    }

    [TestMethod]
    public async Task DeletedCommentsAreRedactedFromHtmlAndPreserveDeletionAuditAndPage()
    {
        var id = await CreateCommentIncident();
        var url = $"/Incidents/Details/{id}";
        const string markdown = "**private-evidence-removed**\n<script>window.commentXss=true</script>\n[unsafe](javascript:alert(1))\n</textarea><img src=x onerror=alert(1)>";
        AssertRedirect(await PostForm("/Incidents/AddComment", new() { ["IncidentId"] = id.ToString(), ["Markdown"] = markdown }, url), url + "?commentsPage=1#incident-comments");
        var before = await Http.GetStringAsync(url);
        Assert.Contains("private-evidence-removed", before);
        Assert.Contains("&lt;script&gt;", before);
        Assert.DoesNotContain("<script>window.commentXss", before);
        Assert.DoesNotContain("<img src=x onerror=", before);
        Guid commentId;
        using (var scope = Server!.Services.CreateScope())
            commentId = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().IncidentComments.SingleAsync()).Id;
        using var noToken = await PostForm("/Incidents/DeleteComment", new() { ["IncidentId"] = id.ToString(), ["CommentId"] = commentId.ToString() }, includeToken: false);
        Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
        using var deleted = await DialogPost("/Incidents/DeleteComment", new()
        {
            ["IncidentId"] = id.ToString(), ["CommentId"] = commentId.ToString(), ["CommentsPage"] = "2"
        }, url);
        Assert.AreEqual(HttpStatusCode.OK, deleted.StatusCode);
        Assert.AreEqual(url + "?commentsPage=2#incident-comments", JObject.Parse(await deleted.Content.ReadAsStringAsync())["redirectUrl"]!.Value<string>());
        var after = WebUtility.HtmlDecode(await Http.GetStringAsync(url));
        Assert.Contains("评论已删除", after);
        Assert.DoesNotContain("private-evidence-removed", after);
        Assert.DoesNotContain("window.commentXss", after);
        using var repeated = await DialogPost("/Incidents/DeleteComment", new()
        {
            ["IncidentId"] = id.ToString(), ["CommentId"] = commentId.ToString()
        }, url);
        Assert.AreEqual(HttpStatusCode.Conflict, repeated.StatusCode);
        using var scopeAfter = Server!.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var saved = await dbAfter.IncidentComments.SingleAsync();
        Assert.AreEqual(markdown, saved.Markdown);
        Assert.IsNotNull(saved.DeletedAtUtc);
        Assert.IsNotNull(saved.DeletedByName);
    }

    [TestMethod]
    public async Task CommentAvatarsFollowProfilesAndFallBackForDeletedAuthorsIncludingTombstones()
    {
        var id = await CreateCommentIncident();
        var url = $"/Incidents/Details/{id}";
        var (email, _) = await RegisterAndLoginAsync();
        Guid visibleId;
        Guid deletedId;
        string authorId;
        string authorName;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var author = await db.Users.SingleAsync(user => user.Email == email);
            authorId = author.Id;
            authorName = author.DisplayName;
            author.AvatarRelativePath = "avatar/comment-author.jpg";
            var visible = new IncidentComment { IncidentId = id, AuthorId = authorId, AuthorName = authorName, Markdown = "Visible comment" };
            var deleted = new IncidentComment
            {
                IncidentId = id, AuthorId = authorId, AuthorName = authorName, Markdown = "Deleted evidence",
                DeletedAtUtc = DateTime.UtcNow, DeletedByName = "Administrator"
            };
            db.IncidentComments.AddRange(visible, deleted);
            await db.SaveChangesAsync();
            visibleId = visible.Id;
            deletedId = deleted.Id;
        }
        await LoginAsAdmin();

        void AssertAvatar(string html, Guid commentId, string avatarPath)
        {
            var article = Regex.Match(html, $"<article[^>]*id=\"incident-comment-{commentId}\"[^>]*>(.*?)</article>", RegexOptions.Singleline);
            Assert.IsTrue(article.Success);
            var storage = GetService<StorageService>();
            Assert.Contains($"src=\"{storage.RelativePathToInternetUrl(avatarPath)}?w=64&square=true\"", WebUtility.HtmlDecode(article.Value));
            Assert.Contains($"alt=\"{authorName}\"", article.Value);
            Assert.Contains("incident-comment-avatar", article.Value);
        }

        var html = await Http.GetStringAsync(url);
        AssertAvatar(html, visibleId, "avatar/comment-author.jpg");
        AssertAvatar(html, deletedId, "avatar/comment-author.jpg");
        Assert.DoesNotContain("Deleted evidence", html);

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var author = await db.Users.SingleAsync(user => user.Id == authorId);
            author.AvatarRelativePath = "avatar/comment-author-updated.jpg";
            author.DisplayName = "Renamed author";
            await db.SaveChangesAsync();
        }
        html = await Http.GetStringAsync(url);
        AssertAvatar(html, visibleId, "avatar/comment-author-updated.jpg");
        AssertAvatar(html, deletedId, "avatar/comment-author-updated.jpg");

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            db.Users.Remove(await db.Users.SingleAsync(user => user.Id == authorId));
            await db.SaveChangesAsync();
        }
        html = await Http.GetStringAsync(url);
        AssertAvatar(html, visibleId, User.DefaultAvatarPath);
        AssertAvatar(html, deletedId, User.DefaultAvatarPath);
        Assert.DoesNotContain("Deleted evidence", html);
    }

    [TestMethod]
    public async Task CommentReadersSeeTwentyNewestAndDeleteOnlyTheirOwnWhileUnrelatedUsersAreDenied()
    {
        var id = await CreateCommentIncident();
        var url = $"/Incidents/Details/{id}";
        Guid adminCommentId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var admin = await db.Users.SingleAsync(user => user.UserName == "admin");
            var comments = Enumerable.Range(0, 25).Select(index => new IncidentComment
            {
                IncidentId = id, AuthorId = admin.Id, AuthorName = admin.DisplayName,
                Markdown = $"comment-body-{index:D2}", CreatedAtUtc = DateTime.UtcNow.AddMinutes(index - 30)
            }).ToList();
            db.IncidentComments.AddRange(comments);
            await db.SaveChangesAsync();
            adminCommentId = comments[^1].Id;
        }
        var (email, _) = await RegisterAndLoginAsync();
        using var denied = await PostForm("/Incidents/AddComment", new() { ["IncidentId"] = id.ToString(), ["Markdown"] = "denied" }, "/Manage/ChangePassword");
        Assert.AreEqual(HttpStatusCode.Found, denied.StatusCode);
        Assert.Contains("Code403", denied.Headers.Location!.OriginalString);
        string userId;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            userId = (await db.Users.SingleAsync(user => user.Email == email)).Id;
            db.ApplicationUserAccesses.Add(new ApplicationUserAccess { ApplicationId = (await db.Incidents.SingleAsync()).ApplicationId, UserId = userId });
            await db.SaveChangesAsync();
        }
        var first = await Http.GetStringAsync(url);
        Assert.Contains("comment-body-24", first);
        Assert.DoesNotContain("comment-body-04", first);
        var second = await Http.GetStringAsync(url + "?commentsPage=2");
        Assert.Contains("comment-body-04", second);
        Assert.DoesNotContain("comment-body-24", second);
        Assert.DoesNotContain($"data-comment-id=\"{adminCommentId}\"", first);
        Assert.DoesNotContain("action=\"/Incidents/DeleteComment\"", first);
        Assert.DoesNotContain("action=\"/Incidents/Resolve\"", first);
        Assert.Contains("action=\"/Incidents/AddComment\"", first);
        using var deleteOther = await PostForm("/Incidents/DeleteComment", new() { ["IncidentId"] = id.ToString(), ["CommentId"] = adminCommentId.ToString() }, url);
        Assert.AreEqual(HttpStatusCode.Found, deleteOther.StatusCode);
        Assert.Contains("Code403", deleteOther.Headers.Location!.OriginalString);
        AssertRedirect(await PostForm("/Incidents/AddComment", new() { ["IncidentId"] = id.ToString(), ["Markdown"] = "viewer own comment" }, url), url + "?commentsPage=1#incident-comments");
        Guid ownId;
        using (var scope = Server!.Services.CreateScope())
            ownId = (await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().IncidentComments.SingleAsync(comment => comment.AuthorId == userId)).Id;
        AssertRedirect(await PostForm("/Incidents/DeleteComment", new() { ["IncidentId"] = id.ToString(), ["CommentId"] = ownId.ToString() }, url), url + "?commentsPage=1#incident-comments");
        await LoginAsAdmin();
        using var deletedApplication = await PostForm("/Alerts/DeleteApplication", new()
        {
            ["id"] = (await CommentApplicationId(id)).ToString(), ["confirmationName"] = "Demo Orders"
        }, "/Alerts/Applications");
        AssertRedirect(deletedApplication, "/Alerts/Applications");
        using var afterScope = Server!.Services.CreateScope();
        Assert.AreEqual(0, await afterScope.ServiceProvider.GetRequiredService<TemplateDbContext>().IncidentComments.CountAsync());
    }

    private async Task<Guid> CommentApplicationId(Guid id)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        return (await db.Incidents.SingleAsync(incident => incident.Id == id)).ApplicationId;
    }
}
