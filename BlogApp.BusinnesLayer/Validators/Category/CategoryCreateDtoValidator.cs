using BlogApp.BusinnesLayer.DTOs.CategoryDTOs;
using FluentValidation;

namespace BlogApp.BusinnesLayer.Validators.Category;

public class CategoryCreateDtoValidator : AbstractValidator<CategoryCreateDto>
{
    public CategoryCreateDtoValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .NotNull()
            .WithMessage("Bos olmamalidir")
            .MaximumLength(64);
        RuleFor(x => x.Icon)
            .NotEmpty()
            .NotNull()
            .Matches("http(s)?://([\\w-]+\\.)+[\\w-]+(/[\\w- ./?%&=]*)?")
            .WithMessage("Link olmalidir")
            .MaximumLength(128);
    }
}
