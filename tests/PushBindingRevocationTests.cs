using Aiursoft.AlertCenter.Entities;
using Aiursoft.AlertCenter.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.Tests;

public partial class PushCenterTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LimitedBindingSavePreservesHiddenChannelsAndRevocationPreventsFurtherChanges(bool roleGrant)
    {
        await AddAdministrators();
        var application = await Application();
        var granted = await Channel();
        var hidden = await Channel();
        Assert.IsInstanceOfType<RedirectToActionResult>(await BindAllLevels(application.Id, granted.Id, hidden.Id));
        _db.ApplicationUserAccesses.Add(new ApplicationUserAccess
        {
            ApplicationId = application.Id, UserId = "ordinary-user", CanViewIncidents = true, CanUpdateIncidents = true, CanManageApiKeys = true, CanManageApplication = true
        });
        string? roleId = null;
        if (roleGrant)
        {
            var role = new IdentityRole("Revocable binding role");
            roleId = role.Id;
            _db.Roles.Add(role);
            _db.UserRoles.Add(new IdentityUserRole<string> { RoleId = role.Id, UserId = "ordinary-user" });
            _db.PushChannelBindingRoles.Add(new PushChannelBindingRole { ChannelId = granted.Id, RoleId = role.Id });
        }
        else
            _db.PushChannelBindingUsers.Add(new PushChannelBindingUser { ChannelId = granted.Id, UserId = "ordinary-user" });
        await _db.SaveChangesAsync();
        var manager = Controller("ordinary-user", administrator: false);

        Assert.IsInstanceOfType<RedirectToActionResult>(await manager.SaveBindings(application.Id,
            new ApplicationPushBindingsRequest { Sev1ChannelIds = [granted.Id] }));
        _db.ChangeTracker.Clear();
        var selected = await _db.ApplicationPushChannels.SingleAsync(link => link.ChannelId == granted.Id);
        Assert.IsTrue(selected.ReceiveSev1);
        Assert.IsFalse(selected.ReceiveSev0 || selected.ReceiveSev2 || selected.ReceiveSev3 || selected.ReceiveSev4);
        var hiddenBinding = await _db.ApplicationPushChannels.SingleAsync(link => link.ChannelId == hidden.Id);
        Assert.IsTrue(hiddenBinding.ReceiveSev0 && hiddenBinding.ReceiveSev1 && hiddenBinding.ReceiveSev2 && hiddenBinding.ReceiveSev3);
        Assert.IsFalse(hiddenBinding.ReceiveSev4);

        if (roleGrant)
            _db.UserRoles.Remove(await _db.UserRoles.SingleAsync(link => link.UserId == "ordinary-user" && link.RoleId == roleId));
        else
            _db.PushChannelBindingUsers.Remove(await _db.PushChannelBindingUsers.SingleAsync(link => link.ChannelId == granted.Id));
        await _db.SaveChangesAsync();

        Assert.IsFalse(await _channelAccess.CanBindAsync(manager.User, granted.Id));
        Assert.IsInstanceOfType<ForbidResult>(await manager.SaveBindings(application.Id,
            new ApplicationPushBindingsRequest { Sev2ChannelIds = [granted.Id] }));
        Assert.IsInstanceOfType<RedirectToActionResult>(await manager.SaveBindings(application.Id, new ApplicationPushBindingsRequest()));
        _db.ChangeTracker.Clear();
        var saved = await _db.ApplicationPushChannels.ToListAsync();
        Assert.AreEqual(2, saved.Count);
        var retained = saved.Single(link => link.ChannelId == granted.Id);
        Assert.IsTrue(retained.ReceiveSev1);
        Assert.IsFalse(retained.ReceiveSev0 || retained.ReceiveSev2 || retained.ReceiveSev3 || retained.ReceiveSev4);
        var retainedHidden = saved.Single(link => link.ChannelId == hidden.Id);
        Assert.IsTrue(retainedHidden.ReceiveSev0 && retainedHidden.ReceiveSev1 && retainedHidden.ReceiveSev2 && retainedHidden.ReceiveSev3);
        Assert.IsFalse(retainedHidden.ReceiveSev4);
    }
}
