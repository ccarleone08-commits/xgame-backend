using BlogApp.BusinnesLayer.DTOs.UserDTOs;
using BlogApp.Core.Repositories;
using FluentValidation;

namespace BlogApp.BusinnesLayer.Validators.User;

public class RegisterCreateDtoValidator : AbstractValidator<RegisterCreateDto>
{
    readonly IUserRepository _repo;
    public RegisterCreateDtoValidator(IUserRepository repo)
    {
        _repo = repo;

        RuleFor(x => x.Username)
            .NotEmpty()
            .NotNull()
            .WithMessage("UserName Bos ola bilmez")
            .Must(x => _repo.GetByUserName(x).Result == null)
            .WithMessage("Usrname Exist");

        RuleFor(x => x.Email)
            .NotEmpty()
            .NotNull()
            .Matches("^[\\w-\\.]+@([\\w-]+\\.)+[\\w-]{2,4}$")
            .WithMessage("Email formatinda daxil et");

        RuleFor(x => x.Password)
            .NotNull()
            .NotEmpty();
        //      .MinimumLength(8).WithMessage("Password must be at least 8 characters long.")
        //    .Matches(@"[A-Z]").WithMessage("Password must contain at least one uppercase letter.")
        //    .Matches(@"[a-z]").WithMessage("Password must contain at least one lowercase letter.")
        //    .Matches(@"\d").WithMessage("Password must contain at least one number.");


    }
}
