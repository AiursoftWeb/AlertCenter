using System.Diagnostics.CodeAnalysis;
using Aiursoft.AlertCenter.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.AlertCenter.MySql;

[ExcludeFromCodeCoverage]

public class MySqlContext(DbContextOptions<MySqlContext> options) : TemplateDbContext(options)
{
}
