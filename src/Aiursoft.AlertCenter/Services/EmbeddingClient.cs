using System.Net.Http.Headers;
using Aiursoft.AlertCenter.Configuration;

namespace Aiursoft.AlertCenter.Services;

public sealed record EmbeddingConfiguration(Uri Endpoint, string Model, string Token, string ModelKey, int QueryCacheLimit);

public sealed class EmbeddingClient(GlobalSettingsService settings, IHttpClientFactory clients)
{
    public const string HttpClientName = "IncidentEmbeddings";

    public async Task<EmbeddingConfiguration?> GetConfigurationAsync()
    {
        if (!await settings.GetBoolSettingAsync(SettingsMap.EnableEmbeddingBasedSearch)) return null;
        var endpoint = (await settings.GetSettingValueAsync(SettingsMap.EmbeddingOllamaInstance)).Trim();
        var model = (await settings.GetSettingValueAsync(SettingsMap.EmbeddingModel)).Trim();
        if (string.IsNullOrEmpty(model) || !Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo) ||
            !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment)) return null;
        var api = new Uri(baseUri, "api/embed?keep_alive=-1");
        var key = IncidentEmbeddingText.Hash($"v1\n{api.AbsoluteUri}\n{model}");
        var limit = await settings.GetIntSettingAsync(SettingsMap.EmbeddingQueryCacheLimit);
        return new EmbeddingConfiguration(api, model, await settings.GetSettingValueAsync(SettingsMap.EmbeddingApiToken),
            key, limit is >= 1 and <= 10000 ? limit : 2000);
    }

    public async Task<float[][]> GenerateAsync(EmbeddingConfiguration configuration, IReadOnlyList<string> inputs,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.Endpoint)
        {
            Content = JsonContent.Create(new { model = configuration.Model, input = inputs, keep_alive = -1 })
        };
        if (!string.IsNullOrWhiteSpace(configuration.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.Token);
        using var client = clients.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<EmbedResponse>(deadline.Token);
        if (result?.Embeddings == null || result.Embeddings.Length != inputs.Count || result.Embeddings.Any(vector => vector == null))
            throw new InvalidDataException("The embedding server returned an unexpected number of vectors.");
        var vectors = result.Embeddings.Select(EmbeddingVector.Normalize).ToArray();
        if (vectors.Select(vector => vector.Length).Distinct().Count() != 1)
            throw new InvalidDataException("The embedding server returned inconsistent vector dimensions.");
        return vectors;
    }

    private sealed record EmbedResponse(float[][]? Embeddings);
}
