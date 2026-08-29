namespace BlogApp.BusinnesLayer.Exceptions;

public interface IBaseException
{
    int StatuCode { get; }
    string ErrorMessage { get; }
}
