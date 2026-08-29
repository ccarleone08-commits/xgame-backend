using Microsoft.AspNetCore.Http;

namespace BlogApp.BusinnesLayer.Exceptions.Common;

public class NotFoundException<T> : Exception, IBaseException
{
    public int StatuCode => StatusCodes.Status404NotFound;
    public string ErrorMessage { get; }
    public NotFoundException() : base(typeof(T).Name +" "+ "is not found")
    {
        ErrorMessage = typeof(T).Name + "is not found";
    }
    public NotFoundException(string msg) : base(msg) { }
}