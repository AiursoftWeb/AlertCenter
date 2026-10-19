using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiursoft.AlertCenter.MySql.Migrations;

[DbContext(typeof(MySqlContext))]
[Migration("20261019052000_IndependentIncidentAcknowledgement")]
public class IndependentIncidentAcknowledgement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE `Incidents`
            SET `AcknowledgedAtUtc` = COALESCE(
                (SELECT MAX(e.`CreatedAtUtc`) FROM `IncidentTimelineEntries` e
                 WHERE e.`IncidentId` = `Incidents`.`Id` AND e.`Type` = 'acked'
                   AND (`Incidents`.`AcknowledgedAtUtc` IS NULL OR e.`CreatedAtUtc` > `Incidents`.`AcknowledgedAtUtc`)),
                `AcknowledgedAtUtc`);
            UPDATE `Incidents` SET `Status` = 'new' WHERE `Status` = 'acked';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("UPDATE `Incidents` SET `Status` = 'acked' WHERE `Status` = 'new' AND `AcknowledgedAtUtc` IS NOT NULL;");
    }
}
