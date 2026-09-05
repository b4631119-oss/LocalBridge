using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LocalBridge.Models;

namespace LocalBridge.Services;

/// <summary>
/// Сервис обнаружения устройств в локальной Wi-Fi сети (Zero-Configuration Discovery).
///
/// Протокол:
///   UDP Broadcast на порт 8890, каждые 3–5 сек.
///   Пакет — JSON: { "DeviceName", "OperatingSystem", "IpAddress", "Port" }
///
/// Таймаут: если устройство молчало 12 сек — удаляется из списка.
/// </summary>
public sealed class NetworkDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 8890;
    private const int BroadcastIntervalMs = 4000; // 4 секунды
    private const int DeviceTimeoutMs = 12000;    // 12 секунд

    private readonly string _deviceName;
    private readonly string _operatingSystem;
    private readonly int _fileTransferPort;
    private readonly string _localIp;

    private CancellationTokenSource? _cts;
    private UdpClient? _udpClient;
    private byte[]? _announcementPayload;

    /// <summary>
    /// Событие: список устройств обновлён.
    /// Вызывается из фонового потока — подписчик должен переключиться на UI-поток.
    /// </summary>
    public event Action<List<DeviceModel>>? OnDevicesUpdated;

    /// <summary>Текущий список живых устройств (включая себя).</summary>
    public List<DeviceModel> Devices { get; private set; } = new();

    private readonly object _lock = new();

    public NetworkDiscoveryService(
        string deviceName,
        string operatingSystem,
        int fileTransferPort,
        string localIp)
    {
        _deviceName = deviceName;
        _operatingSystem = operatingSystem;
        _fileTransferPort = fileTransferPort;
        _localIp = localIp;
    }

    /// <summary>
    /// Запуск сервиса: BroadcastReceiver + Listener одновременно.
    /// </summary>
    public void Start()
    {
        if (_cts is { IsCancellationRequested: false })
            return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Добавляем себя в список
        var self = CreateSelfModel();
        lock (_lock)
        {
            Devices.Add(self);
        }

        // Пакет анонса переиспользуется BroadcastLoop и ручным Rescan()
        _announcementPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            _deviceName,
            _operatingSystem,
            IpAddress = _localIp,
            Port = _fileTransferPort
        }));

        // Два фоновых потока: отправка и приём
        _ = Task.Run(() => BroadcastLoop(token), token);
        _ = Task.Run(() => ReceiveLoop(token), token);
        // Очистка устаревших устройств
        _ = Task.Run(() => CleanupLoop(token), token);
    }

    /// <summary>Остановка сервиса.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        try { _udpClient?.Close(); } catch { /* ignore */ }
        _udpClient = null;
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Ручное сканирование: немедленно отправляет анонс в сеть
    /// (не дожидаясь следующего 4-секундного цикла).
    /// </summary>
    public void Rescan()
    {
        if (_announcementPayload is not { } payload)
            return;

        try
        {
            using var sender = new UdpClient { EnableBroadcast = true };
            sender.Send(payload, payload.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
        }
        catch { /* одиночная ошибка — следующий цикл повторит отправку */ }
    }

    // ════════════════════════════════════════════════════════════════════
    //  BROADCAST — отправка JSON-пакета каждые 4 сек
    // ════════════════════════════════════════════════════════════════════

    private async Task BroadcastLoop(CancellationToken ct)
    {
        using var sender = new UdpClient();
        sender.EnableBroadcast = true;
        var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_announcementPayload is { } payload)
                    await sender.SendAsync(payload, payload.Length, endpoint).ConfigureAwait(false);
                await Task.Delay(BroadcastIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (Exception) { await Task.Delay(2000, ct).ConfigureAwait(false); }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  LISTENER — приём UDP-пакетов от других устройств
    // ════════════════════════════════════════════════════════════════════

    private async Task ReceiveLoop(CancellationToken ct)
    {
        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

        var selfModel = CreateSelfModel();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct).ConfigureAwait(false);
                var json = Encoding.UTF8.GetString(result.Buffer);

                if (string.IsNullOrWhiteSpace(json)) continue;

                var packet = JsonSerializer.Deserialize<DiscoveryPacket>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (packet is null
                    || string.IsNullOrWhiteSpace(packet.IpAddress)
                    || string.IsNullOrWhiteSpace(packet.DeviceName))
                    continue;

                // Игнорируем пакеты от самого себя
                if (packet.IpAddress == _localIp && packet.Port == _fileTransferPort)
                    continue;

                var device = new DeviceModel
                {
                    DeviceName = packet.DeviceName,
                    OperatingSystem = packet.OperatingSystem ?? "Unknown",
                    IpAddress = packet.IpAddress,
                    Port = packet.Port > 0 ? packet.Port : _fileTransferPort
                };

                bool changed;
                lock (_lock)
                {
                    var existing = Devices.FirstOrDefault(d =>
                        d.IpAddress == device.IpAddress && d.Port == device.Port);

                    if (existing is not null)
                    {
                        existing.LastSeen = DateTime.UtcNow;

                        // Уведомляем ТОЛЬКО при реальном изменении данных (имя/ОС),
                        // а не на каждый heartbeat — иначе UI пересоздаёт список
                        // и сбрасывает выделение выбранного устройства.
                        changed = existing.DeviceName != device.DeviceName
                                  || existing.OperatingSystem != device.OperatingSystem;

                        if (changed)
                        {
                            existing.DeviceName = device.DeviceName;
                            existing.OperatingSystem = device.OperatingSystem;
                        }
                    }
                    else
                    {
                        Devices.Add(device);
                        changed = true;
                    }
                }

                if (changed)
                    OnDevicesUpdated?.Invoke(GetSnapshot());
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (Exception) { await Task.Delay(1000, ct).ConfigureAwait(false); }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  CLEANUP — удаление устройств, не отвечавших 12 сек
    // ════════════════════════════════════════════════════════════════════

    private async Task CleanupLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                bool changed;

                lock (_lock)
                {
                    var before = Devices.Count;
                    Devices.RemoveAll(d =>
                        d.IpAddress != _localIp  // себя не удаляем
                        && (now - d.LastSeen).TotalMilliseconds > DeviceTimeoutMs);
                    changed = Devices.Count != before;
                }

                if (changed)
                    OnDevicesUpdated?.Invoke(GetSnapshot());
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* ignore */ }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Вспомогательные методы
    // ════════════════════════════════════════════════════════════════════

    private DeviceModel CreateSelfModel() => new()
    {
        DeviceName = _deviceName,
        OperatingSystem = _operatingSystem,
        IpAddress = _localIp,
        Port = _fileTransferPort
    };

    /// <summary>Потокобезопасный снапшот списка.</summary>
    private List<DeviceModel> GetSnapshot()
    {
        lock (_lock)
        {
            return Devices.ToList();
        }
    }

    /// <summary>
    /// Определение текущей ОС для UDP-пакета.
    /// </summary>
    public static string DetectOperatingSystem()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsAndroid()) return "Android";
        if (OperatingSystem.IsIOS()) return "iOS";
        return Environment.OSVersion.Platform.ToString();
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    // ── Внутренняя модель для десериализации JSON ──

    private sealed class DiscoveryPacket
    {
        public string DeviceName { get; set; } = string.Empty;
        public string OperatingSystem { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; }
    }
}
