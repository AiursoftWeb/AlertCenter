using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class QueryIncidentsRequest
{
    [Range(1, 1000000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 50;
    public bool ActiveOnly { get; set; }
}
