using CommunityToolkit.Mvvm.ComponentModel;

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
        _pmVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _pmVm;
    }

    public void NavigateToSites()
    {
        _sitesVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _sitesVm;
    }

    public void NavigateToOrders()
    {
        _ordersVm.Load();
        CurrentPage = _ordersVm;
    }

    public void NavigateToReportBuilder()
    {
        _reportBuilderVm.Load();
        CurrentPage = _reportBuilderVm;
    }

    public void NavigateToCustomData()
    {
        _customDataVm.Load();
        CurrentPage = _customDataVm;
    }

    public void NavigateToWorkload()
    {
        _workloadVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _workloadVm;
    }

    public void NavigateToConfig()
    {
        _configVm.Load();
        CurrentPage = _configVm;
    }

    public void NavigateToAssignment()
    {
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
