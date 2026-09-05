using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalBridge.Models;
using LocalBridge.Services;

namespace LocalBridge.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>Подсказка, когда не выбрано ни одно устройство из списка.</summary>
    private const string NoDeviceSelectedHint = "Выберите устройство из списка слева для отправки";

    private readonly FileTransferService _service;
    private readonly NetworkDiscoveryService _discovery;
    private CancellationTokenSource? _sendCts;

    // ════════════════════════════════════════════════════════════════════
    //  Observable-свойства (привязки UI)
    // ════════════════════════════════════════════════════════════════════

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendTextCommand))]
    private string _selectedFileName = "Файл не выбран";

    [ObservableProperty]
    private string _statusText = NoDeviceSelectedHint;

    [ObservableProperty]
    private double _progressValue;

    /// <summary>
    /// IP-адрес целевого устройства. Заполняется ТОЛЬКО автоматически
    /// при выборе устройства из списка — пользователь его не видит и не редактирует.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendTextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteFromClipboardAndSendCommand))]
    private string _targetIp = string.Empty;

    /// <summary>Выбранное устройство из списка DiscoveredDevices (для дружелюбных статусов).</summary>
    [ObservableProperty]
    private DeviceModel? _selectedDevice;

    [ObservableProperty]
    private bool _isSending;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendTextCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteFromClipboardAndSendCommand))]
    private string _textToSend = string.Empty;

    /// <summary>Обнаруженные устройства в сети (привязка к sidebar).</summary>
    public ObservableCollection<DeviceModel> DiscoveredDevices { get; } = new();

    /// <summary>Полный путь к выбранному файлу (не отображается, используется для отправки).</summary>
    private string? _selectedFilePath;

    // ════════════════════════════════════════════════════════════════════
    //  Конструктор — инициализация сервиса и запуск сервера
    // ════════════════════════════════════════════════════════════════════

    public MainViewModel()
    {
        _service = new FileTransferService();

        // ── Запуск сетевого обнаружения устройств ──
        _discovery = new NetworkDiscoveryService(
            deviceName: Environment.MachineName,
            operatingSystem: NetworkDiscoveryService.DetectOperatingSystem(),
            fileTransferPort: 8889,
            localIp: _service.LocalIp);

        _discovery.OnDevicesUpdated += (devices) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ApplyDeviceDiff(devices));
        };

        _discovery.Start();

#if ANDROID
        // Android: входящие файлы сохраняем в публичную папку Downloads,
        // чтобы пользователь нашёл их любым файловым менеджером
        // (/storage/emulated/0/Download/LocalBridge/).
        var downloadsRoot = Android.OS.Environment.GetExternalStoragePublicDirectory(
                Android.OS.Environment.DirectoryDownloads)?.AbsolutePath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloadsDir = Path.Combine(downloadsRoot, "LocalBridge");
#else
        var downloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "LocalBridge");
#endif

        _service.StartServer(
            downloadsDir,
            onFileProgress: (fileName, pct) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StatusText = $"Получение: {fileName} ({pct:F1}%)";
                    ProgressValue = pct;
                });
            },
            onTextReceived: (text) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    // Копируем полученный текст в системный буфер обмена (Avalonia 12 API)
                    var topLevel = Application.Current?.ApplicationLifetime is
                        IClassicDesktopStyleApplicationLifetime desktop
                        ? desktop.MainWindow : null;
                    if (topLevel?.Clipboard is { } clipboard)
                    {
                        await clipboard.SetValueAsync(DataFormat.Text, text);
                    }

                    StatusText = text.Length > 80
                        ? $"📋 Текст скопирован: {text[..80]}..."
                        : $"📋 Текст скопирован: {text}";
                });
            });
    }

    /// <summary>
    /// Дифференциальное обновление списка устройств: существующие элементы НЕ
    /// пересоздаются (ListBox сохраняет выделение), добавляются только новые,
    /// удаляются только исчезнувшие из сети.
    /// </summary>
    private void ApplyDeviceDiff(List<DeviceModel> devices)
    {
        // Удаляем устройства, пропавшие из сети
        var gone = DiscoveredDevices.Where(d => !devices.Any(n => n.Equals(d))).ToList();
        foreach (var d in gone)
            DiscoveredDevices.Remove(d);

        // Добавляем новые устройства
        foreach (var n in devices)
        {
            if (!DiscoveredDevices.Any(d => d.Equals(n)))
                DiscoveredDevices.Add(n);
        }

        // Выбранное устройство ушло из сети — сбрасываем выбор и показываем подсказку
        if (SelectedDevice is not null && !DiscoveredDevices.Contains(SelectedDevice))
        {
            SelectedDevice = null;
            TargetIp = string.Empty;
            StatusText = NoDeviceSelectedHint;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Команды
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Открыть нативный диалог выбора файла.
    /// После выбора — файл готов к отправке (нужно нажать «Отправить»).
    /// </summary>
    [RelayCommand]
    private async Task SelectFileAsync(TopLevel? topLevel)
    {
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите файл для отправки",
            AllowMultiple = false
        });

        if (files.Count == 0) return;

        var file = files[0];
        var path = file.TryGetLocalPath();
        if (path is null)
        {
            StatusText = "Ошибка: не удалось получить путь к файлу.";
            return;
        }

        _selectedFilePath = path;
        SelectedFileName = file.Name;
        StatusText = $"Выбран: {file.Name} — готов к отправке";
        ProgressValue = 0;
    }

    /// <summary>
    /// Отправить выбранный файл на TargetIp.
    /// Доступна только если файл выбран и IP введён.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendFile))]
    private async Task SendFileAsync()
    {
        if (string.IsNullOrEmpty(_selectedFilePath)) return;

        // Отмена предыдущей передачи, если была
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;

        IsSending = true;
        ProgressValue = 0;
        StatusText = $"Отправка {Path.GetFileName(_selectedFilePath)} → {TargetDeviceName}...";

        try
        {
            await FileTransferService.SendFileAsync(
                TargetIp,
                _selectedFilePath,
                onProgress: (pct) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ProgressValue = pct;
                        StatusText = $"Отправка {Path.GetFileName(_selectedFilePath)}... {pct:F1}%";
                    });
                },
                ct);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ProgressValue = 100;
                StatusText = $"✓ Отправлено {Path.GetFileName(_selectedFilePath)} на {TargetDeviceName}";
            });
        }
        catch (OperationCanceledException)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = "Отправка отменена.";
                ProgressValue = 0;
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"✗ Ошибка: {ex.Message}";
                ProgressValue = 0;
            });
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = false);
        }
    }

    private bool CanSendFile() =>
        !IsSending
        && !string.IsNullOrEmpty(_selectedFilePath)
        && !string.IsNullOrWhiteSpace(TargetIp);

    /// <summary>
    /// Отмена текущей передачи.
    /// </summary>
    [RelayCommand]
    private void CancelSend()
    {
        _sendCts?.Cancel();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Отправка текста / ссылок
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Отправка текста или ссылки на TargetIp через /text/ эндпоинт.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSendText))]
    private async Task SendTextAsync()
    {
        if (string.IsNullOrWhiteSpace(TextToSend))
            return;

        if (string.IsNullOrWhiteSpace(TargetIp))
        {
            StatusText = NoDeviceSelectedHint;
            return;
        }

        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;

        IsSending = true;
        StatusText = $"Отправка текста → {TargetDeviceName}...";

        try
        {
            await FileTransferService.SendTextAsync(TargetIp, TextToSend, ct);

            // Определяем, это URL или простой текст
            var isUrl = Uri.TryCreate(TextToSend.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusText = isUrl
                    ? $"✓ Ссылка открыта на {TargetDeviceName}"
                    : $"✓ Текст отправлен на {TargetDeviceName}";
            });
        }
        catch (OperationCanceledException)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = "Отправка отменена.");
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = $"✗ Ошибка: {ex.Message}");
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = false);
        }
    }

    private bool CanSendText() =>
        !IsSending
        && !string.IsNullOrWhiteSpace(TargetIp)
        && !string.IsNullOrWhiteSpace(TextToSend);

    /// <summary>
    /// Считывает текст из системного буфера обмена, подставляет в TextToSend и отправляет.
    /// </summary>
    [RelayCommand]
    private async Task PasteFromClipboardAndSendAsync(TopLevel? topLevel)
    {
        if (topLevel is null) return;

        try
        {
            if (topLevel.Clipboard is { } clipboard)
            {
                var text = await clipboard.TryGetTextAsync();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    TextToSend = text;
                    StatusText = $"📋 Из буфера обмена: {(text.Length > 60 ? text[..60] + "..." : text)}";

                    // Автоматическая отправка
                    if (CanSendText())
                    {
                        await SendTextAsync();
                    }
                    else if (string.IsNullOrWhiteSpace(TargetIp))
                    {
                        StatusText = NoDeviceSelectedHint;
                    }
                }
                else
                {
                    StatusText = "Буфер обмена пуст.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"✗ Ошибка чтения буфера: {ex.Message}";
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Выбор устройства
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Выбор устройства из списка — автоматически подставляет его IP в TargetIp.
    /// Пользователю IP не показывается.
    /// </summary>
    [RelayCommand]
    private void SelectDevice(DeviceModel? device)
    {
        if (device is null) return;
        TargetIp = device.IpAddress;
        SelectedDevice = device;
        StatusText = $"✓ Выбрано устройство: {device.DeviceName} — готово к отправке";
        ProgressValue = 0;
    }

    /// <summary>Имя выбранного устройства для статусных сообщений (без IP).</summary>
    private string TargetDeviceName => SelectedDevice?.DeviceName ?? "устройство";

    /// <summary>
    /// Ручное сканирование сети (кнопка «Обновить сеть»).
    /// </summary>
    [RelayCommand]
    private void RefreshNetwork() => _discovery.Rescan();

    /// <summary>
    /// Переключение вкладки (0=Файлы, 1=Буфер, 2=Ссылка).
    /// </summary>
    [RelayCommand]
    private void SwitchTab(int index)
    {
        SelectedTabIndex = index;
    }

    /// <summary>
    /// Установка выбранного файла по пути (из Drag & Drop или другого источника).
    /// </summary>
    public void SetSelectedFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            StatusText = "Ошибка: файл не найден.";
            return;
        }

        _selectedFilePath = filePath;
        SelectedFileName = Path.GetFileName(filePath);
        StatusText = $"Файл готов: {SelectedFileName}";
        ProgressValue = 0;
    }

    // ════════════════════════════════════════════════════════════════════
    //  IDisposable
    // ════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _discovery.Dispose();
        _service.Dispose();
        GC.SuppressFinalize(this);
    }
}