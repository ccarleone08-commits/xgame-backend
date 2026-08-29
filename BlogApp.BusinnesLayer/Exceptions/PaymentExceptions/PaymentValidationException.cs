namespace BlogApp.BusinnesLayer.Exceptions.PaymentExceptions;

public class PaymentValidationException : Exception
{
    public PaymentValidationException(string message) : base(message)
    {
    }
}
