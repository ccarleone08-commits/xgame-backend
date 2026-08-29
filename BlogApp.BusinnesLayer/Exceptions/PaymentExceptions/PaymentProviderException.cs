namespace BlogApp.BusinnesLayer.Exceptions.PaymentExceptions;

public class PaymentProviderException : Exception
{
    public PaymentProviderException(string message) : base(message)
    {
    }

    public PaymentProviderException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
