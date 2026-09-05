using System;

namespace LocalBridge.Models;

/// <summary>
/// Модель обнаруженного устройства в локальной Wi-Fi сети.
/// </summary>
public sealed class DeviceModel : IEquatable<DeviceModel>
{
    /// <summary>Имя устройства (например "MacBook Alex"). Может обновляться при heartbeat.</summary>
    public required string DeviceName { get; set; }

    /// <summary>Операционная система (Linux, Android, Windows, macOS). Может обновляться при heartbeat.</summary>
    public required string OperatingSystem { get; set; }

    /// <summary>IP-адрес устройства в локальной сети.</summary>
    public required string IpAddress { get; init; }

    /// <summary>Порт HTTP-сервера приёма файлов.</summary>
    public required int Port { get; init; }

    /// <summary>Эмодзи-иконка в зависимости от ОС.</summary>
    public string Icon => OperatingSystem.ToLowerInvariant() switch
    {
        "windows" or "win" => "🖥️",
        "linux"             => "🐧",
        "macos" or "mac"    => "💻",
        "android"           => "📱",
        "ios" or "iphone"   => "📱",
        _                   => "📡"
    };

    /// <summary>Время последнего UDP-пакета (для таймаута 12 сек).</summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    // ── IEquatable — сравнение по IP:Port (уникальность устройства) ──

    public bool Equals(DeviceModel? other)
    {
        if (other is null) return false;
        return IpAddress == other.IpAddress && Port == other.Port;
    }

    public override bool Equals(object? obj) => Equals(obj as DeviceModel);
    public override int GetHashCode() => HashCode.Combine(IpAddress, Port);
    public override string ToString() => $"{DeviceName} ({IpAddress}:{Port})";
}
