using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FinXmlProcessor.Application;
using FinXmlProcessor.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _services;

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    private NavigationItem? _selectedItem;

    public MainViewModel(IServiceProvider services, ProcessingRunner runner)
    {
        _services = services;
        Runner = runner;
        Items =
        [
            new NavigationItem("Dashboard", "Latest result, next schedule and Run Now", typeof(DashboardViewModel)),
            new NavigationItem("Process File", "Choose an XML file and generate a workbook", typeof(ProcessFileViewModel)),
            new NavigationItem("History", "Previous jobs, reports and reruns", typeof(HistoryViewModel)),
            new NavigationItem("Quarantine", "Inputs that could not be processed", typeof(QuarantineViewModel)),
            new NavigationItem("Profiles", "Mapping profiles and validation status", typeof(ProfilesViewModel)),
            new NavigationItem("Settings", "Folders, schedule, SFTP, delivery and diagnostics", typeof(SettingsViewModel)),
        ];
    }

    public ObservableCollection<NavigationItem> Items { get; }

    public ProcessingRunner Runner { get; }

    public string WindowTitle => $"{AppInfo.ProductName} {AppInfo.Version}";

    public Task InitializeAsync()
    {
        SelectedItem = Items[0];
        return Task.CompletedTask;
    }

    public void Navigate(string title)
    {
        NavigationItem? item = Items.FirstOrDefault(i => string.Equals(i.Title, title, StringComparison.Ordinal));
        if (item is not null)
        {
            SelectedItem = item;
        }
    }

    partial void OnSelectedItemChanged(NavigationItem? value)
    {
        if (value is null)
        {
            return;
        }

        value.Instance ??= (PageViewModel)_services.GetRequiredService(value.ViewModelType);
        CurrentPage = value.Instance;
        _ = value.Instance.ActivateAsync();
    }
}

public sealed class NavigationItem
{
    public NavigationItem(string title, string description, Type viewModelType)
    {
        Title = title;
        Description = description;
        ViewModelType = viewModelType;
    }

    public string Title { get; }

    public string Description { get; }

    public Type ViewModelType { get; }

    public PageViewModel? Instance { get; set; }

    public override string ToString() => Title;
}
