using System.Data;
using System.Text;
using Aiursoft.AlertCenter.Configuration;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Aiursoft.AlertCenter.Services;

public class OnCallMaintenanceWorker(IServiceScopeFactory scopes, ILogger<OnCallMaintenanceWorker> logger) : BackgroundService
{
    internal async Task ProcessAsync(DateTime now, CancellationToken token = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var groupIds = await db.OnCallGroups.AsNoTracking().Select(group => group.Id).ToListAsync(token);
        foreach (var groupId in groupIds)
        {
            await using var groupScope = scopes.CreateAsyncScope();
            var context = groupScope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            try
            {
                await MaintainGroupAsync(context,
                    groupScope.ServiceProvider.GetRequiredService<IStringLocalizer<OnCallScheduleGenerator>>(),
                    groupScope.ServiceProvider.GetRequiredService<IStringLocalizer<OnCallMaintenanceWorker>>(),
                    groupScope.ServiceProvider.GetRequiredService<IOptions<EmailSettings>>().Value.PublicBaseUrl,
                    groupId, now, token);
            }
            catch (DbUpdateException exception)
            {
                // 唯一键或版本冲突表示另一实例已处理，下一轮重新读取。
                logger.LogWarning("On-call maintenance conflicted for {GroupId}: {ExceptionType}.", groupId, exception.GetType().Name);
            }
            catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException)
            {
                logger.LogError("Invalid on-call template for {GroupId}: {Message}", groupId, exception.Message);
            }
        }
    }

    internal static async Task MaintainGroupAsync(TemplateDbContext db, IStringLocalizer<OnCallScheduleGenerator> generatorLocalizer,
        IStringLocalizer<OnCallMaintenanceWorker> localizer, string baseUrl, Guid groupId, DateTime now, CancellationToken token)
    {
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            await OnCallScheduleService.LockGroupAsync(db, groupId, token);
            var group = await db.OnCallGroups.Include(group => group.Rule).ThenInclude(rule => rule!.HandoverPoints)
                .Include(group => group.Members).SingleOrDefaultAsync(group => group.Id == groupId, token);
            if (group == null) return;
            if (group.AutoExtend && group.Rule != null && group.Members.Count > 0)
                await ExtendAsync(db, new OnCallScheduleGenerator(generatorLocalizer), group, now, token);
            await QueueHandoversAsync(db, group, now, baseUrl, localizer, token);
            if (!group.HandoverCheckedThroughUtc.HasValue || group.HandoverCheckedThroughUtc < now)
                group.HandoverCheckedThroughUtc = now;
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
        });
    }

    private static async Task ExtendAsync(TemplateDbContext db, OnCallScheduleGenerator generator, OnCallGroup group,
        DateTime now, CancellationToken token)
    {
        var last = await db.OnCallShifts.Include(shift => shift.Members).Where(shift => shift.GroupId == group.Id)
            .OrderByDescending(shift => shift.EndsAtUtc).ThenByDescending(shift => shift.StartsAtUtc).ThenBy(shift => shift.Id)
            .FirstOrDefaultAsync(token);
        var start = DateTime.SpecifyKind(last?.EndsAtUtc ?? (group.AnchorUtc > now ? group.AnchorUtc : now), DateTimeKind.Utc);
        var rule = group.Rule!;
        if (last == null && group.AnchorUtc < now && rule.HandoverPoints.Count == 0)
        {
            var minutes = rule.ShiftMinutes ?? checked(rule.ShiftHours * 60);
            if (minutes <= 0) throw new ArgumentOutOfRangeException(nameof(group));
            start = DateTime.SpecifyKind(group.AnchorUtc.AddMinutes(Math.Floor((now - group.AnchorUtc).TotalMinutes / minutes) * minutes), DateTimeKind.Utc);
        }
        var until = now.AddDays(28);
        if (start >= until) return;
        var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        var history = last?.Members.OrderBy(member => Array.IndexOf(ids, member.UserId)).Select(member =>
            new GeneratedOnCallShift(member.UserId, last.StartsAtUtc, last.EndsAtUtc)).ToList() ?? [];
        if (last is { Members.Count: 0 }) history.Add(new(string.Empty, last.StartsAtUtc, last.EndsAtUtc));
        if (last?.RotationPosition is { } cursor && last.Members.Count > 0)
        {
            var expected = Enumerable.Range(0, last.Members.Count).Select(index =>
                new GeneratedOnCallShift(ids[OnCallScheduleGenerator.QueueIndex(rule, ids.Length, cursor + index)],
                    last.StartsAtUtc, last.EndsAtUtc, RotationPosition: cursor + index)).ToList();
            if (expected.Select(shift => shift.UserId).ToHashSet(StringComparer.Ordinal).SetEquals(last.Members.Select(member => member.UserId)))
                history = expected;
        }
        var count = rule.HandoverPoints.Count == 0
            ? (int)Math.Ceiling((until - start).TotalMinutes / (rule.ShiftMinutes ?? rule.ShiftHours * 60))
            : ((int)Math.Ceiling((until - start).TotalDays / 7) + 2) * rule.HandoverPoints.Count;
        var generated = generator.GenerateRange(rule, DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc), ids,
            start, count, TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId), history);
        foreach (var batch in generated.Where(shift => shift.StartsAtUtc < until).GroupBy(shift => new { shift.StartsAtUtc, shift.EndsAtUtc }))
            db.OnCallShifts.Add(new OnCallShift
            {
                GroupId = group.Id, StartsAtUtc = batch.Key.StartsAtUtc, EndsAtUtc = batch.Key.EndsAtUtc,
                Source = OnCallShiftSource.Generated, RotationPosition = batch.First().RotationPosition,
                Members = batch.Select(shift => new OnCallShiftMember { UserId = shift.UserId }).ToList()
            });
        group.ScheduleVersion = Guid.NewGuid();
        await db.SaveChangesAsync(token);
    }

    private static async Task QueueHandoversAsync(TemplateDbContext db, OnCallGroup group, DateTime now, string baseUrl,
        IStringLocalizer localizer, CancellationToken token)
    {
        // 首次启用只检查当前班段；随后用持久化游标补齐轮询间隔或重启期间的交接。
        var checkedThrough = group.HandoverCheckedThroughUtc;
        var shifts = await db.OnCallShifts.Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc <= now &&
                (checkedThrough.HasValue ? shift.StartsAtUtc > checkedThrough.Value : shift.EndsAtUtc > now))
            .OrderBy(shift => shift.StartsAtUtc).ToListAsync(token);
        if (shifts.Count == 0) return;
        var earliest = shifts.Min(shift => shift.StartsAtUtc);
        var previous = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
            .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc < now && shift.EndsAtUtc >= earliest)
            .ToListAsync(token);
        var shiftIds = shifts.Select(shift => shift.Id).ToArray();
        var receipts = await db.Set<OnCallHandoverReceipt>().AsNoTracking().Where(receipt => shiftIds.Contains(receipt.ShiftId)).ToListAsync(token);
        var sent = receipts.Select(receipt => $"{receipt.ShiftId:N}:{receipt.UserId}").ToHashSet(StringComparer.Ordinal);
        var incidents = await db.Incidents.AsNoTracking().Where(incident => incident.Application.OnCallGroupId == group.Id &&
            incident.Status != Incident.Resolved).OrderBy(incident => incident.Level).ThenBy(incident => incident.OccurredAtUtc)
            .ToListAsync(token);
        var body = new StringBuilder().AppendLine(localizer["Unresolved incident handover"].Value).AppendLine(group.Name);
        foreach (var incident in incidents)
            body.AppendLine($"{incident.Level.ToUpperInvariant()} | {incident.Title} | {incident.Status} | ACK: {incident.IsAcknowledged} | {incident.OwnerDisplayName ?? incident.OwnerId ?? localizer["None"].Value}")
                .AppendLine($"{baseUrl.TrimEnd('/')}/Incidents/Details/{incident.Id}");
        var notified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shift in shifts)
        {
            var previousIds = previous.Where(old => old.StartsAtUtc < shift.StartsAtUtc && old.EndsAtUtc >= shift.StartsAtUtc)
                .SelectMany(old => old.Members.Select(member => member.UserId)).ToHashSet(StringComparer.Ordinal);
            foreach (var person in shift.Members.Where(member => !previousIds.Contains(member.UserId)))
            {
                var key = $"{shift.Id:N}:{person.UserId}";
                if (sent.Contains(key)) continue;
                db.Add(new OnCallHandoverReceipt { ShiftId = shift.Id, UserId = person.UserId, ProcessedAtUtc = now });
                if (incidents.Count == 0 || !notified.Add(person.UserId)) continue;
                db.PushDeliveries.Add(new PushDelivery
                {
                    CreatedAtUtc = now,
                    OnCallEmail = new OnCallEmailSnapshot
                    {
                        GroupId = group.Id, GroupName = group.Name, ShiftId = shift.Id, StartsAtUtc = shift.StartsAtUtc,
                        EndsAtUtc = shift.EndsAtUtc, NotificationAtUtc = shift.StartsAtUtc, HandoverKey = key,
                        HandoverContent = body.ToString(), Recipients = [new OnCallEmailRecipient
                            { UserId = person.UserId, DisplayName = person.User.DisplayName, Email = person.User.Email }]
                    }
                });
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessAsync(DateTime.UtcNow, stoppingToken);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("On-call maintenance failed with {ExceptionType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
