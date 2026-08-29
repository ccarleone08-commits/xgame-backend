using Microsoft.AspNetCore.Http;

namespace BlogApp.BusinnesLayer.Exceptions.Common;

public class ExistsException<T> : Exception, IBaseException
{
    public string ErrorMessage { get; }
    public int StatuCode => StatusCodes.Status409Conflict;
    public ExistsException() : base(typeof(T).Name + "is exists")
    {
        ErrorMessage = typeof(T).Name + "is exist";
    }
    public ExistsException(string message) : base(message)
    {
        ErrorMessage = message;
    }
}

