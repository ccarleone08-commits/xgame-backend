namespace BlogApp.BusinnesLayer.DTOs.WithdrawDTOs
{
    public class WithdrawReviewDto
    {
        public int WithdrawRequestId { get; set; }
        public bool IsApproved { get; set; }
        public string? Note { get; set; }
    }
}
