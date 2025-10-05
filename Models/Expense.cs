namespace Ipoteka.Models;
public record ExpenseRecord
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime Date { get; set; }
}