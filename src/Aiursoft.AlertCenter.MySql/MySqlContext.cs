using System.Diagnostics.CodeAnalysis;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.MySql;

[ExcludeFromCodeCoverage]

public class MySqlContext(DbContextOptions<MySqlContext> options) : TemplateDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<Incident>().Property(incident => incident.IncidentKey).UseCollation("utf8mb4_0900_bin");
        builder.Entity<Incident>().Property(incident => incident.ActiveMatchKey).UseCollation("utf8mb4_0900_bin");
        builder.Entity<Incident>().Property(incident => incident.AlertTitle).UseCollation("utf8mb4_0900_bin");
    }
}
