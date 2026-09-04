using System.Collections.ObjectModel;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace FinXmlProcessor.Desktop.ViewModels;

public sealed class DocumentationSection
{
    public DocumentationSection(string title, string resourceName, string repositoryPath)
    {
        Title = title;
        ResourceName = resourceName;
        RepositoryPath = repositoryPath;
    }

    public string Title { get; }

    public string ResourceName { get; }

    public string RepositoryPath { get; }

    public string Markdown { get; set; } = string.Empty;

    public override string ToString() => Title;
}

/// <summary>Shows the feature and technical guides. The markdown is embedded from the docs folder at build time.</summary>
public sealed partial class DocumentationViewModel : PageViewModel
{
    private readonly ILogger<DocumentationViewModel> _logger;

    [ObservableProperty]
    private DocumentationSection? _selectedSection;

    public DocumentationViewModel(ILogger<DocumentationViewModel> logger)
    {
        _logger = logger;
        Sections =
        [
            new DocumentationSection("Feature Documentation", "feature-documentation.md", "docs/feature-documentation.md"),
            new DocumentationSection("Technical Documentation", "technical-documentation.md", "docs/technical-documentation.md"),
        ];
        SelectedSection = Sections[0];
    }

    public override string Title => "Documentation";

    public ObservableCollection<DocumentationSection> Sections { get; }

    public override Task ActivateAsync() => GuardAsync(() =>
    {
        foreach (DocumentationSection section in Sections)
        {
            if (section.Markdown.Length == 0)
            {
                section.Markdown = Load(section.ResourceName);
            }
        }

        // Re-assign so bindings pick up the loaded text.
        DocumentationSection? current = SelectedSection;
        SelectedSection = null;
        SelectedSection = current;
        return Task.CompletedTask;
    }, _logger, null, "Loading documentation");

    private string Load(string resourceName)
    {
        var uri = new Uri($"avares://FinXmlProcessor.Desktop/Docs/{resourceName}");
        try
        {
            using Stream stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            _logger.LogWarning(ex, "Documentation resource {Resource} could not be loaded", resourceName);
            return $"# Documentation unavailable\n\nThe embedded document `{resourceName}` could not be loaded. The same content is in the repository at `{resourceName}`.";
        }
    }
}
