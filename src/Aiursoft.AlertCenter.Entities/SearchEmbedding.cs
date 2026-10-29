using System.ComponentModel.DataAnnotations;

namespace Aiursoft.AlertCenter.Entities;

public class SearchEmbedding
{
    [MaxLength(64)] public string Id { get; set; } = string.Empty;
    public byte[] Vector { get; set; } = [];
    public DateTime LastAccessedAtUtc { get; set; }
}
