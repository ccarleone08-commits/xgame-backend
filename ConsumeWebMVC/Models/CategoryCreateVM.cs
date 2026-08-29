using BlogApp.Core.Entities;

namespace ConsumeWebMVC.Models;

public class CategoryCreateVM
{
    public string Name { get; set; }
    public string Icon { get; set; }

    public static implicit operator Category(CategoryCreateVM cat)
    {
        Category category = new Category
        {
            Icon = cat.Icon,
            Name = cat.Name,
        };
        return category;
    }
}
