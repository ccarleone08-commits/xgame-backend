using BlogApp.Core.Entities;
using System.ComponentModel;

namespace ConsumeWebAPI.Models;

public class CategoryVM
{
    [DisplayName("Category")]
    public string Name { get; set; }
    public string Icon { get; set; }
    public static implicit operator Category(CategoryVM cat)
    {
        Category category = new Category
        {
            Icon = cat.Icon,
            Name = cat.Name,
        };
        return category;
    }
}