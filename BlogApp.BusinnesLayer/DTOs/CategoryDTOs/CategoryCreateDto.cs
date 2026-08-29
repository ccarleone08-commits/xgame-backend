using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.DTOs.CategoryDTOs;

public class CategoryCreateDto
{
    public string Name { get; set; }
    public string Icon { get; set; }
    public static implicit operator Category(CategoryCreateDto cat)
    {
        Category category = new Category
        {
            Icon = cat.Icon,
            Name = cat.Name,
        };
        return category;
    }
}
