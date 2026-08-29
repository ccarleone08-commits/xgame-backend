namespace BlogApp.Core.Entities;

public class CoinPackage : BaseEntity
{
    public string Name { get; set; } = null!;
    public decimal CoinAmount { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = "usd";
    public bool IsActive { get; set; } = true;

    public ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();
}
