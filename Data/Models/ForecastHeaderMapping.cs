namespace PMDashboard.Data.Models;

/// <summary>
/// Remembers that a specific raw spreadsheet header, for a specific ForecastType, maps to a
/// specific ForecastField — so re-uploading the same sheet shape needs no re-mapping the second
/// time. When the same header text appears more than once in a type's header row (seen in real
/// sheets — "po_number", "OSD", and "MK2/Titus" each appear twice in Type L), ColumnIndex
/// disambiguates which occurrence this mapping is for; -1 means "match by name only, position
/// doesn't matter", which covers the common case of a header that appears once.
/// </summary>
public class ForecastHeaderMapping
{
    public int           Id              { get; set; }
    public int            ForecastTypeId  { get; set; }
    public ForecastType?  ForecastType    { get; set; }
    public string         SourceHeader    { get; set; } = "";
    public int             ColumnIndex     { get; set; } = -1;
    public int             ForecastFieldId { get; set; }
    public ForecastField?  ForecastField   { get; set; }

    public DateTime LastModified { get; set; }
}
