namespace BlogApp.Core.Entities;

public class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreateDate { get; set; } = DateTime.Now;
    public bool IsDeleted { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
