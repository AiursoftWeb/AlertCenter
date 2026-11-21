using System.Data;
using System.Globalization;
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
        if (groupIds.Count == 0) return;
        var policy = await new SeverityPolicyStore(db).LoadAsync(token);
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
                    groupId, now, token, groupScope.ServiceProvider.GetRequiredService<IStringLocalizerFactory>()
                        .Create(typeof(OnCallScheduleValidator)), logger, policy);
            }
            catch (DbUpdateException exception)
            {
                // 唯一键或版本冲突表示另一实例已处理，下一轮重新读取。
                logger.LogWarning("On-call maintenance conflicted for {GroupId}: {ExceptionType}.", groupId, exception.GetType().Name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogError(exception, "On-call maintenance failed for {GroupId}.", groupId);
            }
        }
    }

    internal static async Task MaintainGroupAsync(TemplateDbContext db, IStringLocalizer<OnCallScheduleGenerator> generatorLocalizer,
        IStringLocalizer<OnCallMaintenanceWorker> localizer, string baseUrl, Guid groupId, DateTime now, CancellationToken token,
        IStringLocalizer? validatorLocalizer = null, ILogger<OnCallMaintenanceWorker>? logger = null,
        IncidentSeverityPolicy? policy = null)
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
            {
                if (transaction != null) await transaction.CreateSavepointAsync("OnCallExtension", token);
                try
                {
                    await ExtendAsync(db, new OnCallScheduleGenerator(generatorLocalizer), group, now, token, validatorLocalizer);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException or
                    TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    logger?.LogError(exception, "On-call extension failed for {GroupId}; continuing handover processing.", groupId);
                    // 续排保存后也可能失败；同时回滚数据库与跟踪状态，避免半成品随交接提交。
                    if (transaction != null) await transaction.RollbackToSavepointAsync("OnCallExtension", token);
                    db.ChangeTracker.Clear();
                    group = await db.OnCallGroups.SingleAsync(item => item.Id == groupId, token);
                }
                if (transaction != null) await transaction.ReleaseSavepointAsync("OnCallExtension", token);
            }
            await QueueHandoversAsync(db, group, now, baseUrl, localizer, policy, token);
            if (!group.HandoverCheckedThroughUtc.HasValue || group.HandoverCheckedThroughUtc < now)
                group.HandoverCheckedThroughUtc = now;
            await db.SaveChangesAsync(token);
            if (transaction != null) await transaction.CommitAsync(token);
        });
    }

    private static async Task ExtendAsync(TemplateDbContext db, OnCallScheduleGenerator generator, OnCallGroup group,
        DateTime now, CancellationToken token, IStringLocalizer? validatorLocalizer)
    {
        var shifts = db.OnCallShifts.Where(shift => shift.GroupId == group.Id);
        var anchor = DateTime.SpecifyKind(group.AnchorUtc, DateTimeKind.Utc);
        var baseline = group.AutoExtendedThroughUtc ?? now;
        var start = DateTime.SpecifyKind(baseline > now ? baseline : now, DateTimeKind.Utc);
        if (start < anchor) start = anchor;
        var until = now.AddDays(28);
        if (start >= until) return;

        var ids = group.Members.OrderBy(member => member.RotationOrder).Select(member => member.UserId).ToArray();
        var rule = group.Rule!;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(group.TimeZoneId);
        var boundary = OnCallScheduleGenerator.GenerationStart(rule, anchor, zone, start);
        var history = await OnCallScheduleGenerator.LoadHistory(shifts, rule, ids, boundary, start, token);
        var first = generator.GenerateRange(rule, anchor, ids, boundary, 1, zone, history,
            OnCallScheduleGenerator.FragmentPosition(history, boundary));
        var position = first[0].RotationPosition!.Value;
        var boundaries = new List<DateTime>();
        var through = start;
        while (through < until)
        {
            through = boundaries.Count == 0 ? first[0].EndsAtUtc : OnCallScheduleGenerator.NextBoundary(rule, zone, through);
            boundaries.Add(through);
        }

        var occupied = await shifts.AsNoTracking().Where(shift =>
                shift.StartsAtUtc < through && shift.EndsAtUtc > start && shift.StartsAtUtc < shift.EndsAtUtc)
            .OrderBy(shift => shift.StartsAtUtc).Select(shift => new { shift.StartsAtUtc, shift.EndsAtUtc }).ToListAsync(token);
        var additions = new List<OnCallShift>();
        var index = 0;
        var from = start;
        foreach (var to in boundaries)
        {
            List<GeneratedOnCallShift>? assignments = null;
            var freeFrom = from;
            // 已有班段只用于扣除占用区间；同一模板班的空闲片段共享一次轮换。
            while (index < occupied.Count && occupied[index].StartsAtUtc < to)
            {
                var block = occupied[index];
                AddFragment(freeFrom, block.StartsAtUtc);
                if (block.EndsAtUtc > freeFrom) freeFrom = block.EndsAtUtc;
                if (freeFrom >= to) break;
                index++;
            }
            AddFragment(freeFrom, to);
            if (assignments != null) position = assignments[^1].RotationPosition!.Value + 1;
            from = to;

            void AddFragment(DateTime fragmentStart, DateTime fragmentEnd)
            {
                if (fragmentStart >= fragmentEnd) return;
                assignments ??= OnCallScheduleGenerator.AssignShift(rule, ids, position, rule.StaffPerShift, fragmentStart, fragmentEnd);
                additions.Add(new OnCallShift
                {
                    GroupId = group.Id, StartsAtUtc = fragmentStart, EndsAtUtc = fragmentEnd,
                    Source = OnCallShiftSource.Generated, RotationPosition = assignments[0].RotationPosition,
                    Members = assignments.Select(shift => new OnCallShiftMember { UserId = shift.UserId }).ToList()
                });
            }
        }
        var overlaps = OnCallScheduleValidator.OverlapErrors(additions, [], validatorLocalizer);
        if (overlaps.Count > 0) throw new ArgumentException(string.Join(" ", overlaps.Select(overlap => overlap.Message)));
        db.OnCallShifts.AddRange(additions);
        group.ScheduleVersion = Guid.NewGuid();
        group.AutoExtendedThroughUtc = through;
        await db.SaveChangesAsync(token);
    }

    private static async Task QueueHandoversAsync(TemplateDbContext db, OnCallGroup group, DateTime now, string baseUrl,
        IStringLocalizer localizer, IncidentSeverityPolicy? policy, CancellationToken token)
    {
        // 当前班段每轮按回执检查，覆盖手动换人和补建；游标补齐轮询间隔或重启期间已结束的班段。
        var checkedThrough = group.HandoverCheckedThroughUtc;
        var shifts = await db.OnCallShifts.Include(shift => shift.Members).ThenInclude(member => member.User)
            .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc <= now &&
                (shift.EndsAtUtc > now || checkedThrough.HasValue && shift.StartsAtUtc > checkedThrough.Value))
            .OrderBy(shift => shift.StartsAtUtc).ToListAsync(token);
        if (shifts.Count == 0) return;
        var earliest = shifts.Min(shift => shift.StartsAtUtc);
        var previous = await db.OnCallShifts.AsNoTracking().Include(shift => shift.Members)
            .Where(shift => shift.GroupId == group.Id && shift.StartsAtUtc < now && shift.EndsAtUtc >= earliest)
            .ToListAsync(token);
        var shiftIds = shifts.Select(shift => shift.Id).ToArray();
        var receipts = await db.Set<OnCallHandoverReceipt>().AsNoTracking().Where(receipt => shiftIds.Contains(receipt.ShiftId)).ToListAsync(token);
        var sent = receipts.Select(receipt => $"{receipt.ShiftId:N}:{receipt.UserId}").ToHashSet(StringComparer.Ordinal);
        List<HandoverIncident>? incidents = null;
        var access = new ApplicationAccessService(db);
        baseUrl = baseUrl.TrimEnd('/');
        var considered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shift in shifts)
        {
            var expired = shift.EndsAtUtc <= now;
            var previousIds = previous.Where(old => old.StartsAtUtc < shift.StartsAtUtc && old.EndsAtUtc >= shift.StartsAtUtc)
                .SelectMany(old => old.Members.Select(member => member.UserId)).ToHashSet(StringComparer.Ordinal);
            foreach (var person in shift.Members)
            {
                var key = $"{shift.Id:N}:{person.UserId}";
                if (sent.Contains(key)) continue;
                db.Add(new OnCallHandoverReceipt { ShiftId = shift.Id, UserId = person.UserId, ProcessedAtUtc = now });
                if (expired || previousIds.Contains(person.UserId) || !considered.Add(person.UserId)) continue;
                if (incidents is { Count: 0 }) continue;
                var applicationIds = await access.AccessibleApplicationIds(person.UserId, ApplicationPermission.ViewIncidents)
                    .Where(id => db.SourceApplications.Any(application => application.Id == id && application.OnCallGroupId == group.Id))
                    .ToHashSetAsync(token);
                if (applicationIds.Count == 0) continue;
                if (incidents == null)
                {
                    var severityPolicy = policy ?? await new SeverityPolicyStore(db).LoadAsync(token);
                    var unresolved = await db.Incidents.Where(incident => incident.Application.OnCallGroupId == group.Id &&
                            incident.Status != Incident.Resolved)
                        .Select(incident => new HandoverIncident(incident.ApplicationId, incident.Application.Name,
                            incident.Level, incident.AcknowledgedAtUtc.HasValue)).ToListAsync(token);
                    incidents = unresolved.Where(incident => severityPolicy.Allows(incident.Level, "email")).ToList();
                }
                var visible = incidents.Where(incident => applicationIds.Contains(incident.ApplicationId)).ToList();
                if (visible.Count == 0) continue;
                var summary = BuildHandoverSummary(visible, baseUrl, localizer);
                var body = BuildHandoverBody(group, shift, summary, baseUrl, localizer);
                db.PushDeliveries.Add(new PushDelivery
                {
                    CreatedAtUtc = now,
                    OnCallEmail = new OnCallEmailSnapshot
                    {
                        GroupId = group.Id, GroupName = group.Name, ShiftId = shift.Id, StartsAtUtc = shift.StartsAtUtc,
                        // 十六进制时间戳区分重新加入的交接，并在最长 UserId 下保持 key 不超过 500 字符。
                        EndsAtUtc = shift.EndsAtUtc, NotificationAtUtc = shift.StartsAtUtc, HandoverKey = $"{key}:{now.Ticks:x}",
                        HandoverContent = body, Recipients = [new OnCallEmailRecipient
                            { UserId = person.UserId, DisplayName = person.User.DisplayName, Email = person.User.Email }]
                    }
                });
            }
        }
    }

    private sealed record HandoverIncident(Guid ApplicationId, string ApplicationName, string Level, bool IsAcknowledged);

    private const int HandoverApplicationLimit = 20;

    private static string BuildHandoverSummary(List<HandoverIncident> incidents, string baseUrl, IStringLocalizer localizer)
    {
        var levels = string.Join(" · ", incidents.GroupBy(incident => incident.Level)
            .OrderBy(level => level.Key, StringComparer.Ordinal)
            .Select(level => $"{level.Key.ToUpperInvariant()} {level.Count()}"));
        var applications = incidents.GroupBy(incident => new { incident.ApplicationId, incident.ApplicationName })
            .Select(application => new { application.Key, Count = application.Count() })
            .OrderByDescending(application => application.Count)
            .ThenBy(application => application.Key.ApplicationName, StringComparer.Ordinal)
            .ThenBy(application => application.Key.ApplicationId).ToList();
        var summary = new StringBuilder()
            .AppendLine(localizer["Unresolved incidents: {0} total ({1}), {2} unacknowledged",
                incidents.Count, levels, incidents.Count(incident => !incident.IsAcknowledged)].Value)
            .AppendLine()
            .AppendLine(localizer["View by application:"].Value);
        foreach (var application in applications.Take(HandoverApplicationLimit))
        {
            var label = localizer["{0} ({1} incidents)", application.Key.ApplicationName, application.Count].Value;
            summary.AppendLine($"  {label}  {baseUrl}/Incidents?ApplicationId={application.Key.ApplicationId}");
        }
        if (applications.Count > HandoverApplicationLimit)
            summary.AppendLine(localizer["{0} more applications", applications.Count - HandoverApplicationLimit].Value);
        return summary.ToString();
    }

    private static string BuildHandoverBody(OnCallGroup group, OnCallShift shift, string summary, string baseUrl,
        IStringLocalizer localizer)
    {
        // 无效时区不应阻断交接；改用 UTC 并在正文中标明。
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(group.TimeZoneId, out var teamZone) ? teamZone : TimeZoneInfo.Utc;
        var zoneId = teamZone == null ? zone.Id : group.TimeZoneId;
        string LocalTime(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return new StringBuilder()
            .AppendLine(localizer["You are now on call: {0}", group.Name].Value)
            .AppendLine(localizer["Shift: {0} – {1} ({2})", LocalTime(shift.StartsAtUtc), LocalTime(shift.EndsAtUtc), zoneId].Value)
            .Append(summary)
            .AppendLine()
            .AppendLine(localizer["My on-call shifts: {0}", $"{baseUrl}/OnCall/Mine"].Value)
            .AppendLine(localizer["Team schedule: {0}", $"{baseUrl}/OnCall/ScheduleDetails/{group.Id}"].Value)
            .ToString();
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
