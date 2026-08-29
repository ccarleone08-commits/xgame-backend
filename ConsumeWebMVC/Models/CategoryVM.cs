using BlogApp.Core.Entities;
using System.ComponentModel;

namespace ConsumeWebMVC.Models
{
    public class CategoryVM
    {
        [DisplayName("Category")]
        public int Id {  get; set; }
        public string Name { get; set; }
        public string Icon { get; set; }
        public static implicit operator Category(CategoryVM cat)
        {
            Category category = new Category
            {
                Id=cat.Id,
                Icon = cat.Icon,
                Name = cat.Name,
            };
            return category;
        }
    }
}
