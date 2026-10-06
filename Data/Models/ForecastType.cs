namespace PMDashboard.Data.Models;

/// <summary>
/// An admin-managed forecast sheet type (e.g. "O", "M", "L", "B", "P" from the data center
/// equipment buildout forecasts). Each upload is classified as one of these, which determines
/// which ForecastFields/ForecastHeaderMappings apply when parsing it.
/// </summary>
public class ForecastType
{
    public int    Id        { get; set; }
    public string Code      { get; set; } = "";   // short code as seen in the source sheets, e.g. "O"
    public string Name      { get; set; } = "";   // display name, e.g. "Type O"
    public int    SortOrder { get; set; }
    public bool   IsBuiltIn { get; set; }

    public DateTime LastModified { get; set; }
}
