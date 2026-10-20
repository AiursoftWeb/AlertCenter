using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.SDK.Models;

public sealed class CreateIncidentCommentRequest
{
    [Required, StringLength(4000)]
    public string Markdown { get; set; } = string.Empty;
}
