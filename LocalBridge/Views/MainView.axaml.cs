
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using LocalBridge.Models;
using LocalBridge.ViewModels;

namespace LocalBridge.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Обработчик выбора устройства в списке — вызывает команду SelectDeviceCommand.
    /// </summary>
    private void DeviceList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is DeviceModel device)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.SelectDeviceCommand.Execute(device);
            }
        }
    }

    /// <summary>
    /// Проверка при наведении: разрешаем Drop только для файлов.
    /// Avalonia 12: DragEventArgs.DataTransfer + DataFormat.File + DataTransferExtensions.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    /// <summary>
    /// Обработка Drop: извлекаем первый файл и передаём в ViewModel.
    /// Avalonia 12: IDataTransfer.TryGetFile() (sync, через DataTransferExtensions).
    /// </summary>
    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
            return;

        var file = e.DataTransfer.TryGetFile();
        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        if (path is null)
            return;

        if (DataContext is MainViewModel vm)
        {
            vm.SetSelectedFile(path);
        }
    }
}