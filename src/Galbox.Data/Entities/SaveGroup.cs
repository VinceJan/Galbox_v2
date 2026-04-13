namespace Galbox.Data.Entities;

/// <summary>
/// Represents a save group (branch management).
/// </summary>
public class SaveGroup
{
    public int GroupId { get; set; }
    public int GameId { get; set; }
    public string GroupName { get; set; } = string.Empty; // "A路线", "B路线"
    public string? Description { get; set; }
    public DateTime CreatedDate { get; set; }
    public bool IsActive { get; set; } = true;

    // Navigation properties
    public GameInfo Game { get; set; }
    public ICollection<SaveRecord> Saves { get; set; } = new List<SaveRecord>();
}