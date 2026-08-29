namespace BlogApp.BusinnesLayer.DTOs.PaymentDTOs;

public class PaymentCreateResultDto
{
    public PaymentCreateResultDto(int paymentId, string paymentUrl)
    {
        PaymentId = paymentId;
        PaymentUrl = paymentUrl;
    }

    public int PaymentId { get; set; }
    public string PaymentUrl { get; set; }
}
