using Aiursoft.AlertCenter.Authorization;
using System.Security.Claims;
using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Aiursoft.AlertCenter.Services;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

public partial class IncidentWorkflowTests
{
    private static ClaimsPrincipal Actor(string id, bool admin = false) => new(new ClaimsIdentity(
        admin ? [new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, "Administrators"), .. AppPermissions.GetAllPermissions().Select(permission => new Claim(AppPermissions.Type, permission.Key))]
            : [new Claim(ClaimTypes.NameIdentifier, id)], "test"));

    private IncidentWorkflowService Workflow() => new(_db, new ApplicationAccessService(_db), localizer: TestLocalization.For<IncidentWorkflowService>(), localizerFactory: TestLocalization.Factory);

    private async Task<Incident> StateIncident(string status = Incident.New)
    {
        var application = await CreateApplication();
        _db.Users.Add(new User { Id = "engineer", UserName = "engineer", DisplayName = "Engineer", Email = "engineer@example.com" });
        var incident = new Incident
        {
            ApplicationId = application.Id, Title = "Outage", Level = "sev2", Status = status,
            OwnerId = status == Incident.New ? null : "engineer", OwnerDisplayName = status == Incident.New ? null : "Engineer"
        };
        _db.Incidents.Add(incident);
        await _db.SaveChangesAsync();
        return incident;
    }

    [TestMethod]
    [DataRow(Incident.New, IncidentTimelineEntry.Acked, true)]
    [DataRow(Incident.New, IncidentTimelineEntry.OwnershipChanged, false)]
    [DataRow(Incident.New, IncidentTimelineEntry.Mitigated, false)]
    [DataRow(Incident.New, IncidentTimelineEntry.Resolved, false)]
    [DataRow(Incident.New, IncidentTimelineEntry.Reopened, false)]
    [DataRow(Incident.New, IncidentTimelineEntry.LevelChanged, true)]
    [DataRow(Incident.New, IncidentTimelineEntry.Updated, true)]
    [DataRow(Incident.Acked, IncidentTimelineEntry.Acked, false)]
    [DataRow(Incident.Acked, IncidentTimelineEntry.OwnershipChanged, true)]
    [DataRow(Incident.Acked, IncidentTimelineEntry.Mitigated, true)]
    [DataRow(Incident.Acked, IncidentTimelineEntry.Resolved, false)]
    [DataRow(Incident.Acked, IncidentTimelineEntry.Reopened, false)]
    [DataRow(Incident.Acked, IncidentTimelineEntry.LevelChanged, true)]
    [DataRow(Incident.Acked, IncidentTimelineEntry.Updated, true)]
    [DataRow(Incident.Mitigated, IncidentTimelineEntry.Acked, false)]
    [DataRow(Incident.Mitigated, IncidentTimelineEntry.OwnershipChanged, false)]
    [DataRow(Incident.Mitigated, IncidentTimelineEntry.Mitigated, false)]
    [DataRow(Incident.Mitigated, IncidentTimelineEntry.Resolved, true)]
    [DataRow(Incident.Mitigated, IncidentTimelineEntry.Reopened, true)]
    [DataRow(Incident.Mitigated, IncidentTimelineEntry.LevelChanged, true)]
    [DataRow(Incident.Mitigated, IncidentTimelineEntry.Updated, true)]
    [DataRow(Incident.Resolved, IncidentTimelineEntry.Acked, false)]
    [DataRow(Incident.Resolved, IncidentTimelineEntry.OwnershipChanged, false)]
    [DataRow(Incident.Resolved, IncidentTimelineEntry.Mitigated, false)]
    [DataRow(Incident.Resolved, IncidentTimelineEntry.Resolved, false)]
    [DataRow(Incident.Resolved, IncidentTimelineEntry.Reopened, true)]
    [DataRow(Incident.Resolved, IncidentTimelineEntry.LevelChanged, false)]
    [DataRow(Incident.Resolved, IncidentTimelineEntry.Updated, false)]
    public async Task EnforcesTransitionMatrixAndAuditsSuccessfulActions(string state, string action, bool allowed)
    {
        var incident = await StateIncident(state);
        var version = incident.Version;
        var oldOwner = incident.OwnerId;
        IncidentNoteRequest request = action == IncidentTimelineEntry.LevelChanged
            ? new IncidentLevelRequest { IncidentId = incident.Id, Version = version, Level = "sev0", Note = "Required reason" }
            : new IncidentNoteRequest { IncidentId = incident.Id, Version = version, Note = "Required reason" };
        var result = await Workflow().ExecuteAsync(Actor("admin", true), request, action, default);
        Assert.AreEqual(allowed ? 200 : 400, result.StatusCode, result.Error);
        _db.ChangeTracker.Clear();
        var saved = await _db.Incidents.SingleAsync();
        if (!allowed)
        {
            Assert.AreEqual(state, saved.Status);
            Assert.AreEqual(version, saved.Version);
            Assert.AreEqual(0, await _db.IncidentTimelineEntries.CountAsync());
            return;
        }
        Assert.AreNotEqual(version, saved.Version);
        var entry = await _db.IncidentTimelineEntries.SingleAsync();
        Assert.AreEqual("admin", entry.ActorId);
        Assert.AreEqual("admin", entry.ActorIdSnapshot);
        Assert.AreEqual("Administrator", entry.ActorName);
        Assert.AreEqual("Administrator", entry.ActorDisplayName);
        Assert.AreEqual(state, entry.PreviousStatus);
        Assert.AreEqual(oldOwner, entry.PreviousOwnerId);
        Assert.AreEqual(saved.Status, entry.NewStatus);
        Assert.AreEqual(saved.OwnerId, entry.NewOwnerId);
        Assert.AreEqual("Required reason", entry.Note);
        Assert.AreEqual("sev2", entry.PreviousLevel);
        Assert.AreEqual(saved.Level, entry.NewLevel);
        if (action is IncidentTimelineEntry.Acked or IncidentTimelineEntry.OwnershipChanged)
        {
            Assert.AreEqual("admin", saved.OwnerId);
            Assert.AreEqual("Administrator", saved.OwnerDisplayName);
            Assert.AreEqual(Incident.Acked, saved.Status);
        }
        if (action == IncidentTimelineEntry.Reopened)
        {
            Assert.AreEqual(Incident.New, saved.Status);
            Assert.IsNull(saved.OwnerId);
            Assert.IsNull(saved.OwnerDisplayName);
            Assert.IsNull(saved.AcknowledgedAtUtc);
            Assert.IsNull(saved.MitigatedAtUtc);
            Assert.IsNull(saved.ResolvedAtUtc);
        }
        if (action == IncidentTimelineEntry.Resolved)
        {
            Assert.AreEqual(Incident.Resolved, saved.Status);
            Assert.IsNotNull(saved.ResolvedAtUtc);
        }
        if (action == IncidentTimelineEntry.Mitigated)
        {
            Assert.AreEqual(Incident.Mitigated, saved.Status);
            Assert.IsNotNull(saved.MitigatedAtUtc);
            Assert.AreEqual(oldOwner, saved.OwnerId);
        }
        if (action == IncidentTimelineEntry.LevelChanged)
        {
            Assert.AreEqual(state, saved.Status);
            Assert.AreEqual(oldOwner, saved.OwnerId);
            Assert.AreEqual("sev0", saved.Level);
        }
    }

    [TestMethod]
    [DataRow(IncidentTimelineEntry.Mitigated, Incident.Acked)]
    [DataRow(IncidentTimelineEntry.Resolved, Incident.Mitigated)]
    [DataRow(IncidentTimelineEntry.LevelChanged, Incident.New)]
    [DataRow(IncidentTimelineEntry.Updated, Incident.New)]
    public async Task RequiredReasonsCannotBeBlankOrOversized(string action, string state)
    {
        var incident = await StateIncident(state);
        foreach (var note in new[] { null, "", "  \r\n", new string('x', 4001) })
        {
            var request = new IncidentLevelRequest { IncidentId = incident.Id, Version = incident.Version, Level = "sev1", Note = note };
            Assert.AreEqual(400, (await Workflow().ExecuteAsync(Actor("admin", true), request, action, default)).StatusCode);
        }
        Assert.AreEqual(0, await _db.IncidentTimelineEntries.CountAsync());
    }

    [TestMethod]
    public async Task IncidentAssigneeCannotActWithoutAnApplicationGrant()
    {
        var incident = await StateIncident(Incident.Acked);
        _db.Users.AddRange(new User { Id = "viewer", UserName = "viewer", DisplayName = "Viewer" },
            new User { Id = "outsider", UserName = "outsider", DisplayName = "Outsider" });
        _db.ApplicationUserAccesses.Add(new ApplicationUserAccess { CanViewIncidents = true, ApplicationId = incident.ApplicationId, UserId = "viewer" });
        await _db.SaveChangesAsync();
        foreach (var id in new[] { "viewer", "outsider" })
            Assert.AreEqual(403, (await Workflow().ExecuteAsync(Actor(id), new IncidentNoteRequest
            {
                IncidentId = incident.Id, Version = incident.Version, Note = "Attempt"
            }, IncidentTimelineEntry.Mitigated, default)).StatusCode);
        Assert.AreEqual(403, (await Workflow().ExecuteAsync(Actor("engineer"), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version, Note = "Owner mitigation"
        }, IncidentTimelineEntry.Mitigated, default)).StatusCode);
    }

    [TestMethod]
    public async Task ExplicitlyAuthorizedOceCanAckAndOwnershipMovesOnlyOnExplicitTakeOwnership()
    {
        var incident = await StateIncident();
        var group = new OnCallGroup { Name = "Current team" };
        _db.Users.Add(new User { Id = "next", UserName = "next", DisplayName = "Next" });
        _db.OnCallShifts.Add(new OnCallShift
        {
            Group = group, StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(7),
            Members = [new OnCallShiftMember { UserId = "engineer" }, new OnCallShiftMember { UserId = "next" }]
        });
        (await _db.SourceApplications.SingleAsync()).OnCallGroup = group;
        await _db.SaveChangesAsync();
        _db.ApplicationUserAccesses.AddRange(
            new ApplicationUserAccess { ApplicationId = incident.ApplicationId, UserId = "engineer", CanViewIncidents = true, CanUpdateIncidents = true },
            new ApplicationUserAccess { ApplicationId = incident.ApplicationId, UserId = "next", CanViewIncidents = true, CanUpdateIncidents = true });
        await _db.SaveChangesAsync();
        var service = Workflow();
        Assert.AreEqual(200, (await service.ExecuteAsync(Actor("engineer"), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version
        }, IncidentTimelineEntry.Acked, default)).StatusCode);
        Assert.AreEqual("engineer", incident.OwnerId);
        Assert.AreEqual(200, (await service.ExecuteAsync(Actor("next"), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version
        }, IncidentTimelineEntry.OwnershipChanged, default)).StatusCode);
        Assert.AreEqual("next", incident.OwnerId);
        Assert.AreEqual(Incident.Acked, incident.Status);
        var entry = await _db.IncidentTimelineEntries.SingleAsync(item => item.Type == IncidentTimelineEntry.OwnershipChanged);
        Assert.AreEqual("engineer", entry.PreviousOwnerId);
        Assert.AreEqual("next", entry.NewOwnerId);
    }

    [TestMethod]
    public async Task ReopenIsIndependentOfOtherActiveIncidents()
    {
        var incident = await StateIncident(Incident.Resolved);
        _db.Incidents.Add(new Incident
        {
            ApplicationId = incident.ApplicationId, Title = incident.Title,
            Level = "sev2",
        });
        await _db.SaveChangesAsync();
        var version = incident.Version;
        var result = await Workflow().ExecuteAsync(Actor("admin", true), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = version
        }, IncidentTimelineEntry.Reopened, default);
        Assert.AreEqual(200, result.StatusCode);
        Assert.AreEqual(Incident.New, incident.Status);
        Assert.AreNotEqual(version, incident.Version);
        Assert.AreEqual(1, await _db.IncidentTimelineEntries.CountAsync());
        Assert.AreEqual(0, await _db.PushDeliveries.CountAsync());
    }

    [TestMethod]
    public async Task DeletingOwnerRetainsOwnershipAuditAndDoesNotDeleteIncident()
    {
        var incident = await StateIncident(Incident.Acked);
        await Workflow().ExecuteAsync(Actor("admin", true), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version
        }, IncidentTimelineEntry.OwnershipChanged, default);
        _db.ChangeTracker.Clear();
        await _db.SourceApplications.ExecuteUpdateAsync(update => update.SetProperty(application => application.OwnerId, "engineer"));
        _db.Users.Remove(await _db.Users.SingleAsync(user => user.Id == "admin"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        var saved = await _db.Incidents.SingleAsync();
        Assert.IsNull(saved.OwnerId);
        Assert.AreEqual("Administrator", saved.OwnerDisplayName);
        var audit = await _db.IncidentTimelineEntries.SingleAsync();
        Assert.AreEqual("admin", audit.NewOwnerId);
        Assert.AreEqual("Administrator", audit.NewOwnerName);
        Assert.IsNull(audit.ActorId);
    }

    [TestMethod]
    public async Task OwnerlessNotePreservesActorIdentityAfterRenameAndDeletion()
    {
        var incident = await StateIncident();
        var result = await Workflow().ExecuteAsync(Actor("admin", true), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version, Note = "Investigating without claiming ownership"
        }, IncidentTimelineEntry.Updated, default);
        Assert.AreEqual(200, result.StatusCode, result.Error);
        _db.ChangeTracker.Clear();
        var audit = await _db.IncidentTimelineEntries.Include(entry => entry.Actor).SingleAsync();
        Assert.AreEqual(IncidentTimelineEntry.Updated, audit.Type);
        Assert.AreEqual("admin", audit.ActorId);
        Assert.AreEqual("admin", audit.ActorIdSnapshot);
        Assert.AreEqual("Administrator", audit.ActorName);
        Assert.AreEqual("Administrator", audit.ActorDisplayName);
        Assert.IsNull(audit.PreviousOwnerId);
        Assert.IsNull(audit.NewOwnerId);
        Assert.IsNull(audit.PreviousOwnerName);
        Assert.IsNull(audit.NewOwnerName);

        audit.Actor!.DisplayName = "Renamed administrator";
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        audit = await _db.IncidentTimelineEntries.Include(entry => entry.Actor).SingleAsync();
        Assert.AreEqual("Renamed administrator", audit.Actor!.DisplayName);
        Assert.AreEqual("admin", audit.ActorIdSnapshot);
        Assert.AreEqual("Administrator", audit.ActorName);
        Assert.AreEqual("Administrator", audit.ActorDisplayName);

        _db.ChangeTracker.Clear();
        await _db.SourceApplications.ExecuteUpdateAsync(update => update.SetProperty(application => application.OwnerId, "engineer"));
        _db.Users.Remove(await _db.Users.SingleAsync(user => user.Id == "admin"));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        audit = await _db.IncidentTimelineEntries.Include(entry => entry.Actor).SingleAsync();
        Assert.IsNull(audit.ActorId);
        Assert.IsNull(audit.Actor);
        Assert.AreEqual("admin", audit.ActorIdSnapshot);
        Assert.AreEqual("Administrator", audit.ActorName);
        Assert.AreEqual("Administrator", audit.ActorDisplayName);
        var saved = await _db.Incidents.SingleAsync();
        Assert.AreEqual(Incident.New, saved.Status);
        Assert.IsNull(saved.OwnerId);
        Assert.IsNull(saved.OwnerDisplayName);
    }

    [TestMethod]
    public void TimelineActorDisplayNameSupportsSystemAndLegacyEntries()
    {
        Assert.AreEqual("System", new IncidentTimelineEntry().ActorDisplayName);
        Assert.AreEqual("Legacy actor", new IncidentTimelineEntry
        {
            Type = IncidentTimelineEntry.Updated, ActorId = "legacy",
            Actor = new User { Id = "legacy", DisplayName = "Legacy actor" }
        }.ActorDisplayName);
        Assert.AreEqual("User deleted", new IncidentTimelineEntry
        {
            Type = IncidentTimelineEntry.Updated
        }.ActorDisplayName);
        Assert.AreEqual("User deleted", new IncidentTimelineEntry
        {
            ActorId = "legacy"
        }.ActorDisplayName);
        Assert.AreEqual("User deleted", new IncidentTimelineEntry
        {
            ActorIdSnapshot = "legacy"
        }.ActorDisplayName);
    }

    [TestMethod]
    public async Task InvalidVersionsLevelsAndDuplicateOwnershipHaveNoSideEffects()
    {
        var incident = await StateIncident(Incident.Acked);
        Assert.AreEqual(400, (await Workflow().ExecuteAsync(Actor("admin", true), new IncidentNoteRequest
        {
            IncidentId = incident.Id
        }, IncidentTimelineEntry.Mitigated, default)).StatusCode);
        foreach (var level in new[] { "", "sev5", "SEV0", "sev2" })
            Assert.AreEqual(400, (await Workflow().ExecuteAsync(Actor("admin", true), new IncidentLevelRequest
            {
                IncidentId = incident.Id, Version = incident.Version, Note = "Reason", Level = level
            }, IncidentTimelineEntry.LevelChanged, default)).StatusCode);
        Assert.AreEqual(403, (await Workflow().ExecuteAsync(Actor("engineer"), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version
        }, IncidentTimelineEntry.OwnershipChanged, default)).StatusCode);
        Assert.AreEqual(404, (await Workflow().ExecuteAsync(Actor("admin", true), new IncidentNoteRequest
        {
            IncidentId = Guid.NewGuid(), Version = Guid.NewGuid()
        }, IncidentTimelineEntry.Acked, default)).StatusCode);
        Assert.AreEqual(0, await _db.IncidentTimelineEntries.CountAsync());
    }

    [TestMethod]
    public async Task ApplicationRoleManagerCanHandleIncidentButOverlappingShiftDoesNotGrantOceRights()
    {
        var incident = await StateIncident();
        var role = new Microsoft.AspNetCore.Identity.IdentityRole("Responders");
        _db.Roles.Add(role);
        _db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = "engineer", RoleId = role.Id });
        _db.ApplicationRoleAccesses.Add(new ApplicationRoleAccess { ApplicationId = incident.ApplicationId, RoleId = role.Id, CanViewIncidents = true, CanUpdateIncidents = true, CanManageApiKeys = true, CanManageApplication = true });
        await _db.SaveChangesAsync();
        Assert.AreEqual(200, (await Workflow().ExecuteAsync(Actor("engineer"), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version
        }, IncidentTimelineEntry.Acked, default)).StatusCode);
        _db.Users.Add(new User { Id = "overlap", UserName = "overlap", DisplayName = "Overlapping OCE" });
        var group = new OnCallGroup { Name = "Invalid schedule" };
        _db.OnCallShifts.AddRange(new OnCallShift
        {
            Group = group, StartsAtUtc = DateTime.UtcNow.AddHours(-2), EndsAtUtc = DateTime.UtcNow.AddHours(6),
            Members = [new OnCallShiftMember { UserId = "overlap" }]
        }, new OnCallShift
        {
            Group = group, StartsAtUtc = DateTime.UtcNow.AddHours(-1), EndsAtUtc = DateTime.UtcNow.AddHours(7),
            Members = [new OnCallShiftMember { UserId = "overlap" }]
        });
        (await _db.SourceApplications.SingleAsync()).OnCallGroup = group;
        await _db.SaveChangesAsync();
        Assert.AreEqual(403, (await Workflow().ExecuteAsync(Actor("overlap"), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = incident.Version, Note = "Overlap cannot grant handling"
        }, IncidentTimelineEntry.Mitigated, default)).StatusCode);
    }

    [TestMethod]
    public async Task StaleVersionIsRejectedAndLevelChangeDoesNotAffectOtherIncidents()
    {
        var application = await CreateApplication();
        await Report(application, "Source warning", "sev3");
        var incident = await _db.Incidents.SingleAsync();
        var version = incident.Version;
        var result = await Workflow().ExecuteAsync(Actor("admin", true), new IncidentLevelRequest
        {
            IncidentId = incident.Id, Version = version, Level = "sev0", Note = "Impact expanded"
        }, IncidentTimelineEntry.LevelChanged, default);
        Assert.AreEqual(200, result.StatusCode);
        var next = await Report(application, "Source warning", "sev3");
        Assert.AreNotEqual(incident.Id, next.Id);
        Assert.AreEqual("sev3", next.Level);
        Assert.AreEqual("sev0", (await _db.Incidents.SingleAsync(item => item.Id == incident.Id)).Level);
        Assert.AreEqual(409, (await Workflow().ExecuteAsync(Actor("admin", true), new IncidentNoteRequest
        {
            IncidentId = incident.Id, Version = version
        }, IncidentTimelineEntry.Acked, default)).StatusCode);
        Assert.AreEqual(1, await _db.IncidentTimelineEntries.CountAsync(item => item.Type == IncidentTimelineEntry.LevelChanged));
    }
}
