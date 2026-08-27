namespace PMDashboard.Data.Models;

/// <summary>
/// A keyword (substring) matched case-insensitively in ItemDescription to identify a category.
/// Multiple keywords can belong to the same category — any match triggers the category score.
/// Examples: "PIU", "PIE", "CDP", "ATS" → all belong to the PAX category.
/// </summary>
public class EquipmentKeyword
{
    public int      Id           { get; set; }
    public int      CategoryId   { get; set; }
    public EquipmentCategory Category { get; set; } = null!;
    public string   Keyword      { get; set; } = "";
    public DateTime LastModified { get; set; }
}
