namespace BlogApp.BusinnesLayer.DTOs.DepositDTOs
{
    public class BankReviewDto
    {
        public int DepositRequestId { get; set; }
        public bool IsApproved { get; set; }
        public string? Note { get; set; }
    }
}
