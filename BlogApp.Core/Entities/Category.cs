namespace BlogApp.Core.Entities;

public class Category : BaseEntity
{
    public string Name { get; set; } = null!;
    public string Icon { get; set; } = null!;
}
