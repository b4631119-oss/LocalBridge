<div align="center">

# 🌉 LocalBridge

**Fast, private, zero-configuration file & text sharing over your local network**

High-performance, cross-platform P2P utility for sharing files, text, and links
between devices on the same Wi-Fi / LAN — no cloud, no accounts, no IP hunting.

[![C#](https://img.shields.io/badge/C%23-13-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia%20UI-12.1-8B5CF6)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Linux%20%7C%20Windows%20%7C%20macOS-lightgrey)](#-building-from-source)

</div>

---

## ⬇️ Download / Скачать

| OS / ОС | Architecture / Архитектура | File to download / Файл для скачивания |
|---|---|---|
| 🐧 **Linux** | x86_64 (Intel / AMD) | `LocalBridge-linux-x64` |
| 🐧 **Linux** | ARM64 (Raspberry Pi, ARM) | `LocalBridge-linux-arm64` |
| 🪟 **Windows** | x86_64 (Intel / AMD) | `LocalBridge-win-x64.exe` |
| 🪟 **Windows** | ARM64 (Snapdragon, ARM) | `LocalBridge-win-arm64.exe` |
| 🍎 **macOS** | Apple Silicon (M1–M4) | `LocalBridge-osx-arm64` |
| 🍎 **macOS** | Intel (x86_64) | `LocalBridge-osx-x64` |

**Run / Запуск:**

```bash
# Linux — make executable, then run (same for -arm64)
chmod +x LocalBridge-linux-x64
./LocalBridge-linux-x64

# Windows — double-click, or from a terminal (same for -arm64)
.\LocalBridge-win-x64.exe

# macOS — double-click; if macOS blocks it: right-click → Open (same for both variants)
./LocalBridge-osx-arm64
```

> **Pick the right file / Как выбрать файл:** not sure about your chip? On Linux run
> `uname -m` (prints `x86_64` or `aarch64`); on Windows check Settings → System → About;
> on macOS click the Apple menu ( ) → About This Mac (M1/M2/M3/M4 = Apple Silicon).
>
> **How these files are produced / Как получить эти файлы:** every publish command in the
> **Publish a release** section below produces exactly the file name from this table.

---

## 📖 What is LocalBridge? / Что это такое?

**EN:** LocalBridge is a high-performance, cross-platform P2P utility for fast file, text, and URL
sharing in a local network. Devices on the same Wi-Fi find each other automatically — just pick a
device from the list and send. Everything stays on your LAN; no internet, no servers, no accounts.

**RU:** LocalBridge — это высокопроизводительная кроссплатформенная P2P-утилита для быстрой
передачи файлов, текста и ссылок в локальной сети. Устройства в одном Wi-Fi находят друг друга
автоматически — достаточно выбрать устройство из списка и отправить. Всё остаётся в вашей
локальной сети: без интернета, серверов и аккаунтов.

---

## ✨ Key Features / Ключевые возможности

### Zero-Configuration Discovery
**EN:** Devices announce themselves via **UDP broadcast** and are discovered within seconds —
no IP addresses to type, no setup, no QR codes. Just open the app and pick a device.
**RU:** Устройства анонсируют себя через **UDP broadcast** и обнаруживаются за секунды — не нужно
вводить IP-адреса, настраивать что-либо или сканировать QR-коды. Просто откройте приложение и
выберите устройство.

### Low Memory Footprint
**EN:** Files are streamed in **64 KB chunks**, so even **10 GB+ transfers** run without RAM spikes.
The UI stays responsive no matter the file size.
**RU:** Файлы передаются потоково **чанками по 64 КБ**, поэтому даже передача **файлов от 10 ГБ**
не вызывает скачков потребления памяти. Интерфейс остаётся отзывчивым при любом размере файла.

### Security First
**EN:** Local-only binding, **path traversal protection**, and strict URL validation — the app
refuses unsafe filenames and non-http(s) links. Nothing is exposed outside your network.
**RU:** Привязка только к локальной сети, **защита от Path Traversal** и строгая валидация ссылок —
приложение отклоняет опасные имена файлов и не-http(s) ссылки. Ничего не доступно за пределами
вашей сети.

### Modern UI
**EN:** Catppuccin Macchiato dark theme, drag & drop support, and a clean interface with **no raw
IP noise** — devices appear as friendly names with platform icons and online status.
**RU:** Тёмная тема Catppuccin Macchiato, поддержка Drag & Drop и чистый интерфейс **без «сырых»
IP-адресов** — устройства отображаются дружелюбными именами с иконками платформы и статусом
«В сети».

---

## 🏗️ Architecture / Архитектура

**EN:** Clean **C# / MVVM** stack built on **Avalonia 12**. The UI layer is fully separated from
the network layer: `MainViewModel` (CommunityToolkit.Mvvm) drives the views, while two self-contained
services handle networking.

**RU:** Чистый стек **C# / MVVM** на базе **Avalonia 12**. Слой интерфейса полностью отделён от
сетевого слоя: `MainViewModel` (CommunityToolkit.Mvvm) управляет представлениями, а вся сетевая
логика вынесена в два независимых сервиса.

| Component / Компонент | Responsibility / Назначение |
|---|---|
| `Views/` (Avalonia XAML) | UI: device sidebar, transfer tabs, status & progress |
| `ViewModels/MainViewModel.cs` | MVVM state & commands (`CommunityToolkit.Mvvm`) |
| `Services/NetworkDiscoveryService.cs` | Zero-config discovery — UDP broadcast on port **8890** |
| `Services/FileTransferService.cs` | HTTP file/text transfer server & client on port **8889**, 64 KB chunk streaming |
| `Models/DeviceModel.cs` | Discovered device (name, OS, IP:port kept internal to network logic) |

LocalBridge.slnx
├── LocalBridge/ # Shared core: UI + MVVM + services (net10.0)
│ ├── Models/
│ ├── Services/
│ ├── ViewModels/
│ └── Views/
└── LocalBridge.Desktop/ # 🖥️ Linux · Windows · macOS


### How it works / Как это работает
**EN:** Every device listens on port 8889 and announces itself over UDP broadcast on port 8890.
When you pick a device from the sidebar, its address is filled in automatically and the file/text is
pushed over plain HTTP — no intermediaries, maximal speed on your LAN.

**RU:** Каждое устройство слушает порт 8889 и анонсирует себя через UDP broadcast на порту 8890.
При выборе устройства из списка его адрес подставляется автоматически, и файл/текст отправляется
напрямую по HTTP — без посредников, на максимальной скорости вашей локальной сети.

---

## 🚀 Getting Started / Запуск и сборка

### Prerequisites / Требования
- **[.NET 10 SDK](https://dotnet.microsoft.com/download)** (or later)
- Git

### Run from source / Запуск из исходников

```bash
# Desktop (Linux / Windows / macOS)
dotnet run --project LocalBridge.Desktop

# Or build & run the whole solution
dotnet build LocalBridge.slnx
```

### Publish a release / Публикация релиза

All commands build **self-contained** binaries (no .NET runtime needed on the target machine)
and name the output exactly as in the **Download** table above.

Все команды собирают **self-contained** бинарники (рантайм .NET на целевом устройстве не нужен)
и дают выходной файл с именем точно из таблицы загрузки выше.

```bash
# ── Linux ──
# x86_64 (Intel / AMD)
dotnet publish LocalBridge.Desktop -c Release -r linux-x64 --self-contained -p:AssemblyName=LocalBridge-linux-x64
# ARM64 (Raspberry Pi, ARM)
dotnet publish LocalBridge.Desktop -c Release -r linux-arm64 --self-contained -p:AssemblyName=LocalBridge-linux-arm64

# ── Windows ──
# x86_64 (Intel / AMD)
dotnet publish LocalBridge.Desktop -c Release -r win-x64 --self-contained -p:AssemblyName=LocalBridge-win-x64
# ARM64 (Snapdragon, ARM)
dotnet publish LocalBridge.Desktop -c Release -r win-arm64 --self-contained -p:AssemblyName=LocalBridge-win-arm64

# ── macOS ──
# Apple Silicon (M1–M4)
dotnet publish LocalBridge.Desktop -c Release -r osx-arm64 --self-contained -p:AssemblyName=LocalBridge-osx-arm64
# Intel (x86_64)
dotnet publish LocalBridge.Desktop -c Release -r osx-x64 --self-contained -p:AssemblyName=LocalBridge-osx-x64
```

> **Note / Примечание:** если позже понадобится собирать под .NET Runtime вместо self-contained,
> уберите `--self-contained` и установите [.NET Runtime](https://dotnet.microsoft.com/download)
> на целевой машине.

---

## 🧭 Usage / Использование

1. Launch LocalBridge on **both** devices connected to the same Wi-Fi / LAN.
2. Wait a moment — devices appear in the left sidebar with name, platform icon, and **В сети**
   (Online) status.
3. Click a device card to select it as the target (the address is filled in automatically).
4. Pick a **📁 File**, paste **📋 text/clipboard**, or share a **🔗 link** and hit send.

---

## 📄 License / Лицензия

**EN:** Released under the [MIT License](LICENSE). Feel free to use, modify, and distribute.
**RU:** Распространяется под [лицензией MIT](LICENSE). Свободно используйте, изменяйте и
распространяйте.

<div align="center">

**Made with ❤️ and ☕ — LocalBridge**

</div>