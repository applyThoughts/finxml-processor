#pragma warning disable CS0618 // The pre-11.3 drag/drop data API remains supported and is the documented file-drop path.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FinXmlProcessor.Desktop.ViewModels;

namespace FinXmlProcessor.Desktop.Views;

public partial class ProcessFileView : UserControl
{
    public ProcessFileView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ProcessFileViewModel vm)
        {
            return;
        }

        IStorageItem? item = e.Data.GetFiles()?.FirstOrDefault();
        string? path = item?.TryGetLocalPath();
        if (path is not null && File.Exists(path))
        {
            vm.AcceptDroppedFile(path);
        }
    }
}
