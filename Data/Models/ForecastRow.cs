namespace PMDashboard.Data.Models;

/// <summary>
/// One imported forecast data row. SiteId is the resolved match against the app's own Sites
/// (null if the raw site code didn't match anything); SiteCode keeps the raw value regardless,
/// so an unmatched row is still visible/searchable rather than silently dropped. Values are
/// stored as JSON keyed by ForecastField.Key, same pattern as CustomRow.ValuesJson.
/// </summary>
public class ForecastRow
{
    public int              Id               { get; set; }
    public int               ForecastImportId { get; set; }
    public ForecastImport?   ForecastImport   { get; set; }
    public int               ForecastTypeId   { get; set; }
    public ForecastType?     ForecastType     { get; set; }
    public string?           SiteCode         { get; set; }
    public int?              SiteId           { get; set; }
    public Site?             Site             { get; set; }
    public string            ValuesJson       { get; set; } = "{}";

    public DateTime LastModified { get; set; }
}
