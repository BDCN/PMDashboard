using CommunityToolkit.Mvvm.ComponentModel;

namespace PMDashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PmMaintenanceViewModel _pmVm         = new();
    private readonly TeamsViewModel         _teamsVm      = new();
    private readonly OrgChartViewModel      _orgChartVm   = new();
    private readonly SitesViewModel         _sitesVm      = new();
    private readonly OrdersViewModel        _ordersVm     = new();
    private readonly WorkloadViewModel      _workloadVm   = new();
    private readonly ConfigViewModel        _configVm     = new();
    private readonly AssignmentViewModel    _assignmentVm = new();
    private readonly PmDetailViewModel      _pmDetailVm   = new();
    private readonly ReportBuilderViewModel _reportBuilderVm = new();
    private readonly CustomDataViewModel    _customDataVm    = new();
    private readonly UserManagementViewModel _userManagementVm = new();
    private readonly ForecastPlanningViewModel _forecastPlanningVm = new();
    private readonly ForecastTimelineViewModel _forecastTimelineVm = new();
    private readonly CoveragePlannerViewModel  _coveragePlannerVm  = new();

    [ObservableProperty]
    private object _currentPage;

    [ObservableProperty]
    private string _geoFilter = "All";

    public MainViewModel()
    {
        _workloadVm.Load();
        _currentPage = _workloadVm;
        _coveragePlannerVm.OpenSettings = () =>
        {
            _configVm.PendingScrollTarget = "Coverage";
            NavigateToConfig();
        };

        // App-lifetime subscription (this VM lives as long as MainWindow). Workload has its own.
        App.CloudSync.DataChanged += OnCloudDataChanged;
    }

    /// <summary>
    /// Another computer's changes just landed in the local DB — reload the page on screen so it
    /// doesn't silently show stale data until the user navigates away and back. Only pages whose
    /// reload preserves what the user was doing; PM Maintenance (inline-edit grid), Custom Data,
    /// Config and the import/report pages are skipped so a background tick can't discard a
    /// half-finished edit. Every other page picks the change up on its next navigation anyway.
    /// </summary>
    private void OnCloudDataChanged(string _) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if      (CurrentPage == _sitesVm)      _sitesVm.RefreshFromSync();
                else if (CurrentPage == _assignmentVm) _assignmentVm.RefreshFromSync();
                else if (CurrentPage == _pmDetailVm)   _pmDetailVm.Load(_pmDetailVm.CurrentPmId);
                else if (CurrentPage == _coveragePlannerVm) _coveragePlannerVm.Load();
                else if (CurrentPage == _teamsVm)      _teamsVm.Load();
                else if (CurrentPage == _orgChartVm)   _orgChartVm.RefreshFromSync(); // skipped while there are unsaved edits
            }
            catch { /* a background refresh must never crash the app — next navigation reloads */ }
        });

    partial void OnGeoFilterChanged(string value)
    {
        _pmVm.ApplyGeoFilter(value);
        _sitesVm.ApplyGeoFilter(value);
        _workloadVm.ApplyGeoFilter(value);
        _forecastTimelineVm.ApplyGeoFilter(value);
        if (CurrentPage == _coveragePlannerVm) _coveragePlannerVm.ApplyGeoFilter(value);
        _orgChartVm.ApplyGeoFilter(value);
    }

    public void NavigateToPm()
    {
        _pmVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _pmVm;
    }

    public void NavigateToOrgChart()
    {
        _orgChartVm.OpenPm    = id => NavigateToPmDetail(id, NavigateToOrgChart, "← Back to Org Chart");
        _orgChartVm.OpenTeams = NavigateToTeams;
        _orgChartVm.ApplyGeoFilter(GeoFilter);
        _orgChartVm.Load();
        CurrentPage = _orgChartVm;
    }

    public void NavigateToTeams()
    {
        _teamsVm.Load();
        CurrentPage = _teamsVm;
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

    public void NavigateToUserManagement()
    {
        _userManagementVm.Load();
        CurrentPage = _userManagementVm;
    }

    public void NavigateToForecastPlanning()
    {
        _forecastPlanningVm.Load();
        CurrentPage = _forecastPlanningVm;
    }

    public void NavigateToForecastTimeline()
    {
        _forecastTimelineVm.ApplyGeoFilter(GeoFilter);
        CurrentPage = _forecastTimelineVm;
    }

    public void NavigateToCoveragePlanner()
    {
        _coveragePlannerVm.SetGeoFilter(GeoFilter);
        _coveragePlannerVm.Load(); // always recalculate on entry — assignments may have changed elsewhere
        CurrentPage = _coveragePlannerVm;
    }

    public void NavigateToAssignment()
    {
        _assignmentVm.Load();
        CurrentPage = _assignmentVm;
    }

    public void NavigateToPmDetail(int pmId) => NavigateToPmDetail(pmId, NavigateToWorkload, "← Back to Workload");

    /// <summary>PM Detail with a page-specific Back button (e.g. back to the Org Chart).</summary>
    public void NavigateToPmDetail(int pmId, Action back, string backLabel)
    {
        _pmDetailVm.NavigateBack        = back;
        _pmDetailVm.BackLabel           = backLabel;
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
        var pmBack = _pmDetailVm.NavigateBack ?? NavigateToWorkload;
        var pmBackLabel = _pmDetailVm.BackLabel;
        _sitesVm.NavigateBackAction = () => NavigateToPmDetail(pmId, pmBack, pmBackLabel);
        _sitesVm.NavigateDirectToSiteRomps(siteId);
        CurrentPage = _sitesVm;
    }
}
