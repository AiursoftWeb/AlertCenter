using System.Net;
using System.Text.Json;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class WebhookChannelAdapterTests
{
    private static readonly DateTime CreatedAt = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("relative/path", false)]
    [DataRow("file:///tmp/webhook", false)]
    [DataRow("ftp://receiver.example/hook", false)]
    [DataRow("https://user:password@receiver.example/hook", false)]
    [DataRow("https://receiver.example/hook#fragment", false)]
    [DataRow("https://receiver.example/hook", true)]
    [DataRow("http://localhost:9000/hook?source=alert", true)]
    [DataRow("https://[::1]:9000/hook", true)]
    public void EndpointRequiresAnAbsoluteHttpUrlWithoutCredentialsOrFragment(string? endpoint, bool valid)
    {
        Assert.AreEqual(valid, WebhookChannelAdapter.IsValidEndpoint(endpoint));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t\r\n")]
    [DataRow("{}")]
    [DataRow("{\"Authorization\":\"Bearer unit-test\",\"X-Source\":\"alert-center\"}")]
    [DataRow("{\"AZaz09!#$%&'*+-.^_`|~\":\"valid-token-name\"}")]
    public void AcceptsEmptyHeadersAndValidHttpTokenNames(string? json)
    {
        Assert.IsTrue(WebhookChannelAdapter.TryValidateCustomHeaders(json, out var error));
        Assert.IsNull(error);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("42")]
    [DataRow("true")]
    [DataRow("\"headers\"")]
    [DataRow("{\"X-Value\":42}")]
    [DataRow("{\"X-Value\":false}")]
    [DataRow("{\"X-Value\":null}")]
    [DataRow("{\"X-Value\":{}}")]
    [DataRow("{\"X-Value\":[]}")]
    [DataRow("{\"X-Value\":\"first\",\"x-value\":\"second\"}")]
    public void RejectsMalformedHeadersNonStringValuesAndCaseInsensitiveDuplicates(string json)
    {
        Assert.IsFalse(WebhookChannelAdapter.TryValidateCustomHeaders(json, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow("Content-Type")]
    [DataRow("content-length")]
    [DataRow("CONTENT-Encoding")]
    [DataRow("Transfer-Encoding")]
    [DataRow("Host")]
    [DataRow("Connection")]
    [DataRow("Expect")]
    [DataRow("TE")]
    [DataRow("Trailer")]
    [DataRow("Upgrade")]
    [DataRow("idempotency-key")]
    [DataRow("")]
    [DataRow("X Value")]
    [DataRow("X:Value")]
    [DataRow("X\tValue")]
    [DataRow("X\r\nInjected")]
    [DataRow("值班")]
    public void RejectsReservedAndInvalidHeaderNames(string name)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { [name] = "test" });

        Assert.IsFalse(WebhookChannelAdapter.TryValidateCustomHeaders(json, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow(128, 2048, true)]
    [DataRow(129, 2048, false)]
    [DataRow(128, 2049, false)]
    public void HeaderNameAndValueLengthLimitsAreInclusive(int nameLength, int valueLength, bool valid)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [new string('X', nameLength)] = new('v', valueLength)
        });

        Assert.AreEqual(valid, WebhookChannelAdapter.TryValidateCustomHeaders(json, out var error));
        Assert.AreEqual(valid, error == null);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("\t")]
    [DataRow("value\r\nInjected: header")]
    [DataRow("value\0")]
    [DataRow("value\u007f")]
    public void RejectsBlankOrControlCharactersInHeaderValues(string value)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["X-Value"] = value });

        Assert.IsFalse(WebhookChannelAdapter.TryValidateCustomHeaders(json, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t\r\n")]
    [DataRow("{}")]
    [DataRow("{\"items\":[1,true,null,{\"title\":\"{{title}}\"}],\"context\":\"{{context}}\"}")]
    public void AcceptsEmptyTemplatesAndNestedJsonObjects(string? template)
    {
        Assert.IsTrue(WebhookChannelAdapter.TryValidatePayloadTemplate(template, out var error));
        Assert.IsNull(error);
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("42")]
    [DataRow("true")]
    [DataRow("\"{{title}}\"")]
    [DataRow("{\"value\":\"{{unknown}}\"}")]
    [DataRow("{\"value\":\"{{Title}}\"}")]
    [DataRow("{\"value\":\"prefix {{title}}\"}")]
    [DataRow("{\"value\":\"{{title}} suffix\"}")]
    [DataRow("{\"value\":\"{{title}}{{content}}\"}")]
    [DataRow("{\"value\":\"{{title\"}")]
    [DataRow("{\"nested\":[{\"value\":\"{{unknown}}\"}]}")]
    public void RejectsInvalidTemplateRootsAndUnknownOrPartialPlaceholders(string template)
    {
        Assert.IsFalse(WebhookChannelAdapter.TryValidatePayloadTemplate(template, out var error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("带引号 \" 和换行\n的内容 🔔")]
    public async Task DefaultPayloadPreservesReportedFieldsAndUsesDeliveryIdForIdempotency(string? content)
    {
        var delivery = Delivery();
        var message = new ReportedMessage
        {
            Title = "数据库 \"primary\"\n故障",
            Content = content,
            Level = "sev0",
            OccurredAtUtc = CreatedAt.AddMinutes(-5),
            ContextJson = "{\"count\":3,\"active\":true,\"items\":[null,{\"id\":7}]}"
        };
        delivery.Message = message;
        delivery.MessageId = message.Id;
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(delivery.Channel!.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(delivery.Id.ToString(), request.Headers.GetValues("Idempotency-Key").Single());
            Assert.AreEqual("application/json", request.Content!.Headers.ContentType!.MediaType);
            using var json = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var payload = json.RootElement;
            Assert.AreEqual(7, payload.EnumerateObject().Count());
            Assert.AreEqual(delivery.Id, payload.GetProperty("deliveryId").GetGuid());
            Assert.AreEqual(message.Id, payload.GetProperty("messageId").GetGuid());
            Assert.AreEqual(message.Title, payload.GetProperty("title").GetString());
            Assert.AreEqual(content, payload.GetProperty("content").GetString());
            Assert.AreEqual(message.Level, payload.GetProperty("level").GetString());
            Assert.AreEqual(message.OccurredAtUtc, payload.GetProperty("occurredAtUtc").GetDateTime());
            var context = payload.GetProperty("context");
            Assert.AreEqual(3, context.GetProperty("count").GetInt32());
            Assert.IsTrue(context.GetProperty("active").GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, context.GetProperty("items")[0].ValueKind);
            Assert.AreEqual(7, context.GetProperty("items")[1].GetProperty("id").GetInt32());
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        var factory = new StubClientFactory(handler);

        var result = await new WebhookChannelAdapter(factory, Protector(), localizer: TestLocalization.For<WebhookChannelAdapter>()).DeliverAsync(delivery);

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.ErrorMessage);
        Assert.AreEqual("PushWebhook", factory.LastName);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("自定义测试消息")]
    public async Task TestDeliveryUsesFallbackFieldsAndPreservesExplicitTestContent(string? content)
    {
        var delivery = Delivery();
        delivery.TestContent = content;
        using var handler = new StubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var payload = json.RootElement;
            Assert.AreEqual(delivery.Id, payload.GetProperty("deliveryId").GetGuid());
            Assert.AreEqual(JsonValueKind.Null, payload.GetProperty("messageId").ValueKind);
            Assert.AreEqual("Channel test notification", payload.GetProperty("title").GetString());
            Assert.AreEqual(content ?? PushDelivery.DefaultTestContent, payload.GetProperty("content").GetString());
            Assert.AreEqual("sev3", payload.GetProperty("level").GetString());
            Assert.AreEqual(CreatedAt, payload.GetProperty("occurredAtUtc").GetDateTime());
            Assert.AreEqual(JsonValueKind.Null, payload.GetProperty("context").ValueKind);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await Adapter(handler).DeliverAsync(delivery);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CustomPayloadResolvesEveryPlaceholderRecursivelyWithoutReinterpretingMessageText()
    {
        var delivery = Delivery();
        var protector = Protector();
        const string headers = "{\"X-Source\":\"alert-center\",\"X-Trace\":\"unit-test\"}";
        Assert.IsTrue(protector.TryProtect(headers, out var encrypted));
        delivery.Channel!.WebhookHeadersProtected = encrypted;
        delivery.Channel.WebhookPayloadTemplate = """
            {"event":"alert","nested":{"id":"{{deliveryId}}","message":"{{messageId}}","title":"{{title}}",
            "content":"{{content}}","level":"{{level}}","time":"{{occurredAtUtc}}"},
            "items":["{{context}}",{"copy":"{{context}}"},"{{title}}",42,true,null]}
            """;
        var message = new ReportedMessage
        {
            Title = "literal {{unknown}}",
            Content = "prefix {{title}}",
            Level = "sev1",
            OccurredAtUtc = CreatedAt.AddHours(-1),
            ContextJson = "{\"ids\":[1,2],\"empty\":null}"
        };
        delivery.Message = message;
        delivery.MessageId = message.Id;
        using var handler = new StubHandler(async (request, token) =>
        {
            Assert.AreEqual("alert-center", request.Headers.GetValues("X-Source").Single());
            Assert.AreEqual("unit-test", request.Headers.GetValues("X-Trace").Single());
            Assert.AreEqual(delivery.Id.ToString(), request.Headers.GetValues("Idempotency-Key").Single());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var payload = json.RootElement;
            Assert.AreEqual(3, payload.EnumerateObject().Count());
            Assert.AreEqual("alert", payload.GetProperty("event").GetString());
            var nested = payload.GetProperty("nested");
            Assert.AreEqual(delivery.Id, nested.GetProperty("id").GetGuid());
            Assert.AreEqual(message.Id, nested.GetProperty("message").GetGuid());
            Assert.AreEqual(message.Title, nested.GetProperty("title").GetString());
            Assert.AreEqual(message.Content, nested.GetProperty("content").GetString());
            Assert.AreEqual(message.Level, nested.GetProperty("level").GetString());
            Assert.AreEqual(message.OccurredAtUtc, nested.GetProperty("time").GetDateTime());
            var items = payload.GetProperty("items");
            Assert.AreEqual(6, items.GetArrayLength());
            Assert.AreEqual(2, items[0].GetProperty("ids")[1].GetInt32());
            Assert.AreEqual(JsonValueKind.Null, items[0].GetProperty("empty").ValueKind);
            Assert.AreEqual(items[0].GetRawText(), items[1].GetProperty("copy").GetRawText());
            Assert.AreEqual(message.Title, items[2].GetString());
            Assert.AreEqual(42, items[3].GetInt32());
            Assert.IsTrue(items[4].GetBoolean());
            Assert.AreEqual(JsonValueKind.Null, items[5].ValueKind);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await new WebhookChannelAdapter(new StubClientFactory(handler), protector, localizer: TestLocalization.For<WebhookChannelAdapter>()).DeliverAsync(delivery);

        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        Assert.IsTrue(protector.TryUnprotect(delivery.Channel.WebhookHeadersProtected, out var savedHeaders));
        Assert.AreEqual(headers, savedHeaders);
        Assert.AreEqual("literal {{unknown}}", message.Title);
        Assert.AreEqual("{\"ids\":[1,2],\"empty\":null}", message.ContextJson);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(null, "null")]
    [DataRow("null", "null")]
    [DataRow("42", "42")]
    [DataRow("false", "false")]
    [DataRow("\"text\"", "\"text\"")]
    [DataRow("[1,true,null]", "[1,true,null]")]
    public async Task ContextReplacementRetainsJsonTypeIncludingNull(string? context, string expected)
    {
        var delivery = Delivery();
        delivery.Message = new ReportedMessage { ContextJson = context };
        delivery.MessageId = delivery.Message.Id;
        delivery.Channel!.WebhookPayloadTemplate = "{\"first\":\"{{context}}\",\"second\":[\"{{context}}\"]}";
        using var handler = new StubHandler(async (request, token) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.AreEqual(expected, json.RootElement.GetProperty("first").GetRawText());
            Assert.AreEqual(expected, json.RootElement.GetProperty("second")[0].GetRawText());
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var result = await Adapter(handler).DeliverAsync(delivery);

        Assert.IsTrue(result.Succeeded, result.ErrorMessage);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow("missing-channel")]
    [DataRow("invalid-endpoint")]
    [DataRow("unreadable-headers")]
    [DataRow("invalid-headers")]
    [DataRow("reserved-header")]
    [DataRow("invalid-template")]
    [DataRow("non-object-template")]
    [DataRow("unknown-placeholder")]
    [DataRow("invalid-context")]
    public async Task InvalidStoredConfigurationFailsBeforeCreatingAnHttpClient(string scenario)
    {
        var delivery = Delivery();
        var protector = Protector();
        switch (scenario)
        {
            case "missing-channel": delivery.Channel = null; break;
            case "invalid-endpoint": delivery.Channel!.Endpoint = "relative/path"; break;
            case "unreadable-headers": delivery.Channel!.WebhookHeadersProtected = "v1:not-base64"; break;
            case "invalid-headers":
            case "reserved-header":
                var headers = scenario == "invalid-headers" ? "[]" : "{\"Idempotency-Key\":\"override\"}";
                Assert.IsTrue(protector.TryProtect(headers, out var encrypted));
                delivery.Channel!.WebhookHeadersProtected = encrypted;
                break;
            case "invalid-template": delivery.Channel!.WebhookPayloadTemplate = "{"; break;
            case "non-object-template": delivery.Channel!.WebhookPayloadTemplate = "[]"; break;
            case "unknown-placeholder": delivery.Channel!.WebhookPayloadTemplate = "{\"nested\":[\"{{unknown}}\"]}"; break;
            case "invalid-context": delivery.Message = new ReportedMessage { ContextJson = "{" }; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No request should be sent."));
        var factory = new StubClientFactory(handler);

        var result = await new WebhookChannelAdapter(factory, protector, localizer: TestLocalization.For<WebhookChannelAdapter>()).DeliverAsync(delivery);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(scenario is "missing-channel" or "invalid-endpoint"
            ? "Invalid Webhook URL. Please check channel configuration." : "Invalid Webhook custom configuration. Please check channel configuration.", result.ErrorMessage);
        Assert.IsNull(factory.LastName);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task ContentOnlyHeadersCannotBeAddedAsRequestHeadersAndPreventSending()
    {
        var delivery = Delivery();
        var protector = Protector();
        Assert.IsTrue(protector.TryProtect("{\"Last-Modified\":\"Fri, 16 Oct 2026 08:00:00 GMT\"}", out var encrypted));
        delivery.Channel!.WebhookHeadersProtected = encrypted;
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("No request should be sent."));
        var factory = new StubClientFactory(handler);

        var result = await new WebhookChannelAdapter(factory, protector, localizer: TestLocalization.For<WebhookChannelAdapter>()).DeliverAsync(delivery);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Invalid Webhook custom headers. Please check channel configuration.", result.ErrorMessage);
        Assert.IsNull(factory.LastName);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    [DataRow(200, true)]
    [DataRow(201, true)]
    [DataRow(204, true)]
    [DataRow(299, true)]
    [DataRow(300, false)]
    [DataRow(400, false)]
    [DataRow(401, false)]
    [DataRow(429, false)]
    [DataRow(500, false)]
    [DataRow(503, false)]
    public async Task OnlySuccessfulHttpStatusesSucceedAndFailuresDoNotExposeResponseBodies(int status, bool success)
    {
        using var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("private receiver diagnostics")
        }));

        var result = await Adapter(handler).DeliverAsync(Delivery());

        Assert.AreEqual(success, result.Succeeded);
        Assert.AreEqual(success ? null : $"Webhook returned HTTP {status}. Please check the receiver and retry manually.", result.ErrorMessage);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task NetworkErrorsAreSanitizedWithoutAutomaticRetries()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("private endpoint and receiver diagnostics"));

        var result = await Adapter(handler).DeliverAsync(Delivery());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Unable to connect to Webhook. Please check the address, network, or receiving service.", result.ErrorMessage);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task TimeoutReturnsAnUnknownOutcomeWithoutAutomaticRetries()
    {
        using var handler = new StubHandler((_, _) => throw new TaskCanceledException("private timeout diagnostics"));

        var result = await Adapter(handler).DeliverAsync(Delivery());

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("Webhook request timed out. The receiver may have received the message. Please verify and retry.", result.ErrorMessage);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesInsteadOfBecomingADeliveryFailure()
    {
        using var source = new CancellationTokenSource();
        Action cancel = source.Cancel;
        using var handler = new StubHandler((_, token) =>
        {
            Assert.IsTrue(token.CanBeCanceled);
            cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => Adapter(handler).DeliverAsync(Delivery(), source.Token));

        Assert.IsTrue(source.IsCancellationRequested);
        Assert.AreEqual(1, handler.Calls);
    }

    private static PushDelivery Delivery() => new()
    {
        CreatedAtUtc = CreatedAt,
        Channel = new PushChannel { Type = "webhook", Endpoint = "https://receiver.example/hook" }
    };

    private static WebhookConfigurationProtector Protector() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Webhook:HeaderProtectionKey"] = Convert.ToBase64String(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray())
        }).Build());

    private static WebhookChannelAdapter Adapter(HttpMessageHandler handler) => new(new StubClientFactory(handler), Protector(), localizer: TestLocalization.For<WebhookChannelAdapter>());

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? LastName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastName = name;
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
