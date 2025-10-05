namespace Ipoteka.Models;

public record CategoryRecord
{
    public long UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal PlannedAmount { get; set; }
    public DateTime CreatedAt { get; set; }
}
