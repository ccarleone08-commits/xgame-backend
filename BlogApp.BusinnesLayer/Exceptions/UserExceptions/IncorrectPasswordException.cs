using Microsoft.AspNetCore.Http;

namespace BlogApp.BusinnesLayer.Exceptions.UserExceptions;

public class IncorrectPasswordException : Exception, IBaseException
{
    public int StatuCode => StatusCodes.Status400BadRequest;
    public string ErrorMessage { get; }
    public IncorrectPasswordException()
    {
        ErrorMessage = "Password is wrong, please check again";
    }
    public IncorrectPasswordException(string message) : base(message)
    {
        message = ErrorMessage;
    }
}
