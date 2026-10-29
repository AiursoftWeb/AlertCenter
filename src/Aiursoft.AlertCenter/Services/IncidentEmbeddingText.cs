using System.Security.Cryptography;
using System.Text;

namespace Aiursoft.AlertCenter.Services;

internal static class IncidentEmbeddingText
{
    public static string Build(string application, string title, string? content) =>
        $"Application: {application}\nTitle: {title}\nContent:\n{content}";

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
