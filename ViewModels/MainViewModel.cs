using CommunityToolkit.Mvvm.ComponentModel;

using PMDashboard.Services;

namespace PMDashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PmMaintenanceViewModel _pmVm         = new();
    private readonly SitesViewModel         _sitesVm      = new();
    private readonly OrdersViewModel        _ordersVm     = new();
    private readonly WorkloadViewModel      _workloadVm   = new();
    private readonly ConfigViewModel        _configVm     = new();
    private readonly AssignmentViewModel    _assignmentVm = new();
    private readonly PmDetailViewModel      _pmDetailVm   = new();
    private readonly ReportBuilderViewModel _reportBuilderVm = new();
    private readonly CustomDataViewModel    _customDataVm    = new();

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private string _geoFilter = "All";

    public MainViewModel()
    {
        _workloadVm.Load();
        _currentPage = _workloadVm;
    }

    partial void OnGeoFilterChanged(string value)
    {
        _pmVm.ApplyGeoFilter(value);
        _sitesVm.ApplyGeoFilter(value);
        _workloadVm.ApplyGeoFilter(value);
    }

    public void NavigateToPm()
    {
        AuditService.Log("Navigate", "Page", details: "PM Maintenance");
        _pmVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _pmVm;
    }

    public void NavigateToSites()
    {
        AuditService.Log("Navigate", "Page", details: "Sites");
        _sitesVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _sitesVm;
    }

    public void NavigateToOrders()
    {
        AuditService.Log("Navigate", "Page", details: "Orders");
        _ordersVm.Load();
        CurrentPage = _ordersVm;
    }

    public void NavigateToReportBuilder()
    {
        AuditService.Log("Navigate", "Page", details: "Report Builder");
        _reportBuilderVm.Load();
        CurrentPage = _reportBuilderVm;
    }

    public void NavigateToCustomData()
    {
        AuditService.Log("Navigate", "Page", details: "Custom Data");
        _customDataVm.Load();
        CurrentPage = _customDataVm;
    }

    public void NavigateToWorkload()
    {
        AuditService.Log("Navigate", "Page", details: "Workload");
        _workloadVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _workloadVm;
    }

    public void NavigateToConfig()
    {
        AuditService.Log("Navigate", "Page", details: "Configuration");
        _configVm.Load();
        CurrentPage = _configVm;
    }

    public void NavigateToAssignment()
    {
        AuditService.Log("Navigate", "Page", details: "Assignment");
        _assignmentVm.Load();
        CurrentPage = _assignmentVm;
    }

    public void NavigateToPmDetail(int pmId)
    {
        _pmDetailVm.NavigateBack        = NavigateToWorkload;
        _pmDetailVm.NavigateToSiteRomps = siteId => NavigateToSiteRompsFromPm(siteId, pmId);
        _pmDetailVm.Load(pmId);
        CurrentPage = _pmDetailVm;
    }

    /// <summary>
    /// Navigates to the SitesPage pre-drilled to a specific site's ROMs.
    /// The back button on SitesPage returns to the originating PM Detail page.
    /// </summary>
    private void NavigateToSiteRompsFromPm(int siteId, int pmId)
    {
        _sitesVm.BackButtonLabel   = $"← {_pmDetailVm.PmName}";
        _sitesVm.HasBackButton     = true;
        _sitesVm.NavigateBackAction = () => NavigateToPmDetail(pmId);
        _sitesVm.NavigateDirectToSiteRomps(siteId);
        CurrentPage = _sitesVm;
    }
}
