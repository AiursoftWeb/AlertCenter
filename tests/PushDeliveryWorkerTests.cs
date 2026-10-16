using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Services;
using Aiursoft.AlertCenter.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aiursoft.AlertCenter.Tests;

[TestClass]
public class PushDeliveryWorkerTests
{
    private SqliteContext _db = null!;
    private ServiceProvider _services = null!;
    private string _databasePath = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"alert-delivery-worker-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddLogging();
        services.AddDbContext<SqliteContext>(options => options.UseSqlite($"DataSource={_databasePath};Pooling=False"));
        services.AddScoped<TemplateDbContext>(provider => provider.GetRequiredService<SqliteContext>());
        services.AddScoped<PushDeliveryProcessor>();
        _services = services.BuildServiceProvider();
        _db = _services.GetRequiredService<SqliteContext>();
        await _db.Database.MigrateAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _services.DisposeAsync();
        File.Delete(_databasePath);
    }

    [TestMethod]
    public async Task BatchMarksOnlyExpiredSendingDeliveriesAsFailed()
    {
        var channel = new PushChannel { Name = "Channel", Type = "unknown" };
        var expired = new PushDelivery
        {
            Channel = channel,
            Status = PushDelivery.Sending,
            AttemptedAtUtc = DateTime.UtcNow.AddMinutes(-3)
        };
        var active = new PushDelivery
        {
            Channel = channel,
            Status = PushDelivery.Sending,
            AttemptedAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        _db.PushDeliveries.AddRange(expired, active);
        await _db.SaveChangesAsync();

        await Worker().ProcessBatchAsync();

        _db.ChangeTracker.Clear();
        var deliveries = await _db.PushDeliveries.OrderBy(delivery => delivery.AttemptedAtUtc).ToListAsync();
        Assert.AreEqual(PushDelivery.Failed, deliveries[0].Status);
        Assert.Contains("result unknown", deliveries[0].Error!);
        Assert.IsNotNull(deliveries[0].CompletedAtUtc);
        Assert.AreEqual(PushDelivery.Sending, deliveries[1].Status);
        Assert.IsNull(deliveries[1].Error);
        Assert.IsNull(deliveries[1].CompletedAtUtc);
    }

    [TestMethod]
    public async Task BatchProcessesAtMostTwentyPendingDeliveriesInCreationOrder()
    {
        var channel = new PushChannel { Name = "Channel", Type = "unknown" };
        var deliveries = Enumerable.Range(0, 21).Select(index => new PushDelivery
        {
            Channel = channel,
            CreatedAtUtc = DateTime.UtcNow.AddSeconds(index)
        }).ToArray();
        _db.PushDeliveries.AddRange(deliveries);
        await _db.SaveChangesAsync();

        await Worker().ProcessBatchAsync();

        _db.ChangeTracker.Clear();
        var saved = await _db.PushDeliveries.OrderBy(delivery => delivery.CreatedAtUtc).ToListAsync();
        Assert.AreEqual(20, saved.Count(delivery => delivery.Status == PushDelivery.Failed));
        Assert.AreEqual(1, saved.Count(delivery => delivery.Status == PushDelivery.Pending));
        Assert.AreEqual(deliveries[^1].Id, saved.Single(delivery => delivery.Status == PushDelivery.Pending).Id);
        Assert.IsTrue(saved.Take(20).All(delivery => delivery.Attempts == 1));
        Assert.AreEqual(0, saved[^1].Attempts);
    }

    private PushDeliveryWorker Worker() => new(_services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<PushDeliveryWorker>.Instance, localizer: TestLocalization.For<PushDeliveryWorker>());
}
