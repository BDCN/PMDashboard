namespace PMDashboard.Services;

public record ImportResult(int Added, int Updated, int Skipped, int LocationsCreated, int SitesCreated, int RompsCreated);
