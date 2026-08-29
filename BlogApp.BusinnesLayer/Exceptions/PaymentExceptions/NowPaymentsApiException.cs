using System.Net;

namespace BlogApp.BusinnesLayer.Exceptions.PaymentExceptions;

public class NowPaymentsApiException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public NowPaymentsApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
