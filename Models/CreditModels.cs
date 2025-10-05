namespace Ipoteka.Models;

public record CreditData
{
    public decimal InitialAmount { get; set; }
    public decimal CurrentAmount { get; set; }
    public DateTime LastUpdated { get; set; }
}

public record PaymentRecord
{
    public long UserId { get; set; }
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
    public decimal NewBalance { get; set; }
}
