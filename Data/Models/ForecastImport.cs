namespace PMDashboard.Data.Models;

/// <summary>One upload event — a single file classified as one ForecastType.</summary>
public class ForecastImport
{
    public int           Id             { get; set; }
    public int            ForecastTypeId { get; set; }
    public ForecastType?  ForecastType   { get; set; }
    public string         FileName       { get; set; } = "";
    public string UploadedBy     { get; set; } = "";
    public DateTime UploadedAtUtc { get; set; }
    public int    RowCount       { get; set; }

    public DateTime LastModified { get; set; }
}
