using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aiursoft.AlertCenter.Client;

internal sealed record GitLabPipeline(string Source, long Id, string Url, string Title, string Commit,
    DateTimeOffset CreatedAt, string Result)
{
    public static GitLabPipeline Read(Func<string, string?> environment, string result)
    {
        if (result is not ("success" or "failed"))
            throw new ArgumentException("--result must be success or failed.");
        string Required(string name) => !string.IsNullOrWhiteSpace(environment(name))
            ? environment(name)! : throw new ArgumentException($"GitLab CI variable {name} is required.");
        string Url(string name)
        {
            var value = Required(name);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException($"GitLab CI variable {name} must be an absolute HTTP(S) URL without credentials.");
            return value;
        }
        long PositiveId(string name) => long.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id : throw new ArgumentException($"GitLab CI variable {name} must be a positive integer.");
        var server = Url("CI_SERVER_URL").TrimEnd('/');
        var project = PositiveId("CI_PROJECT_ID");
        var branch = Required("CI_COMMIT_BRANCH");
        var id = PositiveId("CI_PIPELINE_ID");
        var url = Url("CI_PIPELINE_URL");
        var title = $"GitLab CI: {Required("CI_PROJECT_PATH")} ({branch})";
        var commit = Required("CI_COMMIT_SHA");
        if (!DateTimeOffset.TryParseExact(Required("CI_PIPELINE_CREATED_AT"),
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var createdAt))
            throw new ArgumentException("GitLab CI variable CI_PIPELINE_CREATED_AT must be an ISO 8601 timestamp.");
        return new GitLabPipeline($"gitlab:{server}:{project}:{branch}", id, url, title[..Math.Min(200, title.Length)], commit, createdAt, result);
    }

    // Preserve Python uuid.uuid5(NAMESPACE_URL, source + ':' + pipelineId) used by existing pipelines.
    public Guid ReportId()
    {
        var name = Encoding.UTF8.GetBytes(Source + ":" + Id.ToString(CultureInfo.InvariantCulture));
        var ns = new Guid("6ba7b811-9dad-11d1-80b4-00c04fd430c8").ToByteArray(bigEndian: true);
        var hash = SHA1.HashData([.. ns, .. name]);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
