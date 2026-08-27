namespace PMDashboard.Data.Models;

/// <summary>
/// Junction table: many-to-many between Romp and EquipmentCategory.
/// A ROMP may have zero or more equipment types; each type contributes its weight
/// to the ROMP's equipment weight sum used in difficulty scoring.
/// </summary>
public class RompEquipmentType
{
    public int Id                  { get; set; }
    public int RompId              { get; set; }
    public Romp Romp               { get; set; } = null!;
    public int      EquipmentCategoryId { get; set; }
    public EquipmentCategory Category  { get; set; } = null!;
    public DateTime LastModified       { get; set; }
}
