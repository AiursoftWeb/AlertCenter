using System.Net;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Services.FileStorage;

namespace Aiursoft.AlertCenter.Tests.IntegrationTests;

[TestClass]
public class ManageControllerTests : TestBase
{
    [TestMethod]
    public async Task TestManageWorkflow()
    {
        await LoginAsAdmin();

        // Ensure AllowUserAdjustNickname is true
        using (var scope = Server!.Services.CreateScope())
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<GlobalSettingsService>();
            await settingsService.UpdateSettingAsync(Configuration.SettingsMap.AllowUserAdjustNickname, "True");
        }

        // 1. Index
        var indexResponse = await Http.GetAsync("/Manage/Index");
        indexResponse.EnsureSuccessStatusCode();

        // 2. ChangePassword (GET)
        var changePasswordPage = await Http.GetAsync("/Manage/ChangePassword");
        changePasswordPage.EnsureSuccessStatusCode();

        // 3. ChangeProfile (GET)
        var changeProfilePage = await Http.GetAsync("/Manage/ChangeProfile");
        changeProfilePage.EnsureSuccessStatusCode();

        // 4. ChangeAvatar (GET)
        var changeAvatarPage = await Http.GetAsync("/Manage/ChangeAvatar");
        changeAvatarPage.EnsureSuccessStatusCode();
    }

    [TestMethod]
    public async Task TestChangePasswordFailure()
    {
        await RegisterAndLoginAsync();

        // Test with wrong old password
        var response = await PostForm("/Manage/ChangePassword", new Dictionary<string, string>
        {
            { "OldPassword", "WrongPassword" },
            { "NewPassword", "NewPassword123!" },
            { "ConfirmPassword", "NewPassword123!" }
        });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Incorrect password.", html);
    }

    [TestMethod]
    public async Task TestChangeProfileFailure()
    {
        await RegisterAndLoginAsync();

        // Test with invalid model (empty name)
        var response = await PostForm("/Manage/ChangeProfile", new Dictionary<string, string>
        {
            { "Name", "" }
        });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        // Should stay on the same page with validation error
    }

    [TestMethod]
    public async Task TestChangeAvatarInvalidImage()
    {
        await RegisterAndLoginAsync();

        // Upload a non-image file
        var content = new StringContent("Not an image");
        var multipartContent = new MultipartFormDataContent();
        multipartContent.Add(content, "file", "test.txt");

        var storage = GetService<StorageService>();
        var uploadUrl = storage.GetUploadUrl("avatar", isVault: false);
        var uploadResponse = await Http.PostAsync(uploadUrl, multipartContent);
        uploadResponse.EnsureSuccessStatusCode();
        var uploadResult = await uploadResponse.Content.ReadFromJsonAsync<UploadResult>();
        string path = uploadResult!.Path;

        var response = await PostForm("/Manage/ChangeAvatar", new Dictionary<string, string>
        {
            { "AvatarUrl", path }
        });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("The file is not a valid image.", html);
    }

    [TestMethod]
    public async Task TestChangeAvatarAllowsJpegExtension()
    {
        await RegisterAndLoginAsync();

        var response = await Http.GetAsync("/Manage/ChangeAvatar");
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-allowed-file-extensions=\"png bmp jpg jpeg\"", html);
        Assert.Contains("data-valid-extensions=\"png bmp jpg jpeg\"", html);
        Assert.Contains("/scripts/file-upload.js", html);
    }

    [TestMethod]
    public async Task TestBindUpdateAndUnbindPhoneNumber()
    {
        var (email, _) = await RegisterAndLoginAsync();

        var indexHtml = await Http.GetStringAsync("/Manage/Index");
        Assert.Contains("/Manage/ChangePhoneNumber", indexHtml);
        Assert.Contains("Not bound", indexHtml);

        var getPage = await Http.GetAsync("/Manage/ChangePhoneNumber");
        getPage.EnsureSuccessStatusCode();

        // Invalid phone number (missing country code '+') should fail validation.
        var invalidResponse = await PostForm("/Manage/ChangePhoneNumber", new Dictionary<string, string>
        {
            { "PhoneNumber", "13800138000" }
        });
        Assert.AreEqual(HttpStatusCode.OK, invalidResponse.StatusCode);
        var invalidHtml = await invalidResponse.Content.ReadAsStringAsync();
        Assert.Contains("E.164", invalidHtml);

        // Valid phone number with spaces/hyphens should normalize and save.
        var bindResponse = await PostForm("/Manage/ChangePhoneNumber", new Dictionary<string, string>
        {
            { "PhoneNumber", "+86 138-0013-8000" }
        });
        Assert.AreEqual(HttpStatusCode.Redirect, bindResponse.StatusCode);

        var boundIndexHtml = WebUtility.HtmlDecode(await Http.GetStringAsync("/Manage/Index"));
        Assert.Contains("+861****8000", boundIndexHtml);

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Entities.TemplateDbContext>();
            var user = db.Users.Single(u => u.Email == email);
            Assert.AreEqual("+8613800138000", user.PhoneNumber);
        }

        // Unbind phone number.
        var unbindResponse = await PostForm("/Manage/UnbindPhoneNumber", new Dictionary<string, string>(), "/Manage/ChangePhoneNumber");
        Assert.AreEqual(HttpStatusCode.Redirect, unbindResponse.StatusCode);

        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Entities.TemplateDbContext>();
            var user = db.Users.Single(u => u.Email == email);
            Assert.IsNull(user.PhoneNumber);
        }
    }

    private class UploadResult
    {
        public string Path { get; init; } = string.Empty;
    }
}
