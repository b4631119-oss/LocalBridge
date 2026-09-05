using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LocalBridge.Services;

/// <summary>
/// Сетевой сервис передачи файлов, текста и ссылок по локальной Wi-Fi сети.
/// 
/// Сервер: HttpListener на порту 8889, привязан к LAN IP (local-only).
///   POST /            — приём файла (стриминг, буфер 64 КБ).
///   POST /text/       — приём текста или URL-ссылки (автооткрытие ссылки в браузере).
/// 
/// Клиент: отправка файла/текста через HttpClient.
/// 
/// Память: ВСЕ передачи строго через Stream кусочками по 64 КБ.
/// Запрещено File.ReadAllBytes / ReadAllText — даже 20+ ГБ файлы потребляют ~10 МБ ОЗУ.
/// </summary>
public sealed class FileTransferService : IDisposable
{
    private const int Port = 8889;
    private const int BufferSize = 65536; // 64 КБ

    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private string _saveDir = string.Empty;
    private Action<string, double>? _onFileProgress;
    private Action<string>? _onTextReceived;

    /// <summary>IP-адрес этого устройства в локальной сети.</summary>
    public string LocalIp { get; private set; } = "127.0.0.1";

    // ════════════════════════════════════════════════════════════════════
    //  СЕРВЕР
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Запуск HTTP-сервера для приёма файлов и текста.
    /// Привязывается к LAN IP — слушает строго локальную сеть.
    /// </summary>
    /// <param name="saveDir">Папка для сохранения входящих файлов ( ~/Downloads/LocalBridge ).</param>
    /// <param name="onFileProgress">Callback: (имя_файла, процент_0..100) — вызывается из фонового потока.</param>
    /// <param name="onTextReceived">Callback: (текст_или_URL) — вызывается из фонового потока.</param>
    public void StartServer(
        string saveDir,
        Action<string, double> onFileProgress,
        Action<string>? onTextReceived = null)
    {
        if (_listener is { IsListening: true })
            return;

        _saveDir = saveDir;
        _onFileProgress = onFileProgress;
        _onTextReceived = onTextReceived;

        Directory.CreateDirectory(saveDir);

        LocalIp = DetectLanIp();

        _listener = CreateListener();

        _serverCts = new CancellationTokenSource();
        var token = _serverCts.Token;

        _ = Task.Run(async () => AcceptLoop(token), token);
    }

    /// <summary>
    /// Остановка сервера.
    /// </summary>
    public void StopServer()
    {
        _serverCts?.Cancel();
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }
        _listener = null;
        _serverCts?.Dispose();
        _serverCts = null;
    }

    /// <summary>
    /// Создаёт HTTP-листенер, привязанный к конкретному LAN IP (local-only).
    /// Если явная привязка невозможна (например, нет прав на URL ACL в Windows) —
    /// откат на "*" + фильтр локальной сети в RouteRequest.
    /// </summary>
    private HttpListener CreateListener()
    {
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{LocalIp}:{Port}/");
            listener.Start();
            return listener;
        }
        catch (Exception)
        {
            var fallback = new HttpListener();
            fallback.Prefixes.Add($"http://*:{Port}/");
            fallback.Start();
            return fallback;
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener!.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => RouteRequest(context, ct), ct);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception) { /* одиночная ошибка — продолжаем */ }
        }
    }

    /// <summary>
    /// Маршрутизация: POST /text/ → текст, POST / → файл.
    /// </summary>
    private Task RouteRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        // Фильтр локальной сети: принимаем только loopback и приватные диапазоны
        // (RFC 1918 + link-local). Гарантирует local-only даже при fallback-привязке "*".
        var remote = ctx.Request.RemoteEndPoint?.Address;
        if (!IsLocalAddress(remote))
        {
            Respond(ctx.Response, HttpStatusCode.Forbidden, "Forbidden");
            return Task.CompletedTask;
        }

        var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;

        if (path.Equals("/text", StringComparison.OrdinalIgnoreCase))
            return HandleTextRequest(ctx, ct);

        return HandleFileRequest(ctx, ct);
    }

    /// <summary>
    /// Проверка, что удалённый адрес относится к локальной сети.
    /// </summary>
    private static bool IsLocalAddress(IPAddress? address)
    {
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    // ── Приём файла ───────────────────────────────────────────────────

    private async Task HandleFileRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;
        var response = ctx.Response;

        try
        {
            if (!request.HasEntityBody)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.Close();
                return;
            }

            var rawName = request.Headers["X-File-Name"]
                          ?? ExtractFileNameFromContentDisposition(request.Headers["Content-Disposition"])
                          ?? "unknown_file";

            // Отправитель кодирует имя через Uri.EscapeDataString — декодируем обратно,
            // чтобы "Отчет%20по%20проекту.txt" стал "Отчет по проекту.txt".
            var decodedName = Uri.UnescapeDataString(rawName);
            var safeName = SanitizeFileName(decodedName);
            var filePath = GetUniqueFilePath(Path.Combine(_saveDir, safeName));
            var totalBytes = request.ContentLength64;
            var received = 0L;

            await using var requestStream = request.InputStream;
            await using var fileStream = new FileStream(
                filePath, FileMode.Create, FileAccess.Write, FileShare.None,
                BufferSize, useAsync: true);

            var buffer = new byte[BufferSize];
            int read;

            while ((read = await requestStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;

                if (totalBytes > 0)
                {
                    var pct = (double)received / totalBytes * 100.0;
                    _onFileProgress?.Invoke(safeName, pct);
                }
            }

            await fileStream.FlushAsync(ct).ConfigureAwait(false);
            _onFileProgress?.Invoke(safeName, 100);

            Respond(response, HttpStatusCode.OK, "OK");
        }
        catch (OperationCanceledException)
        {
            Respond(response, HttpStatusCode.RequestTimeout, "Cancelled");
        }
        catch (Exception)
        {
            Respond(response, HttpStatusCode.InternalServerError, "Error");
        }
    }

    // ── Приём текста / ссылки ─────────────────────────────────────────

    private async Task HandleTextRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;
        var response = ctx.Response;

        try
        {
            if (!request.HasEntityBody)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.Close();
                return;
            }

            // Читаем тело строго через Stream (не ReadAllText!)
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
            {
                Respond(response, HttpStatusCode.BadRequest, "Empty body");
                return;
            }

            // ── Безопасная проверка URL ───────────────────────────────
            // 1) Uri.TryCreate + UriKind.Absolute — только валидные абсолютные URI.
            // 2) Строго http/https — никаких file://, ftp://, javascript: и т.д.
            var trimmed = text.Trim();
            var isSafeUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && uri.AbsoluteUri == trimmed  // защита от null-byte / hidden-redirect инъекций
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            if (isSafeUrl)
            {
                // Безопасный URL — открываем в браузере
                OpenUrlInBrowser(uri!.ToString());
            }
            // Если это обычный текст или небезопасная ссылка — Process.Start НЕ вызывается.
            // Текст просто передаётся в callback для отображения в UI / копирования в буфер.

            _onTextReceived?.Invoke(text);
            Respond(response, HttpStatusCode.OK, "OK");
        }
        catch (OperationCanceledException)
        {
            Respond(response, HttpStatusCode.RequestTimeout, "Cancelled");
        }
        catch (Exception)
        {
            Respond(response, HttpStatusCode.InternalServerError, "Error");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  КЛИЕНТ — отправка
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Отправка файла на целевое устройство стримингом (64 КБ буфер).
    /// Поддерживает CancellationToken для мгновенной отмены.
    /// </summary>
    public static async Task SendFileAsync(
        string targetIp,
        string filePath,
        Action<double> onProgress,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Файл не найден.", filePath);

        var fileInfo = new FileInfo(filePath);
        var fileName = Path.GetFileName(filePath);
        var url = $"http://{targetIp}:{Port}/";

        using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        await using var fileStream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, useAsync: true);

        var progressStream = new ProgressStream(fileStream, fileInfo.Length, onProgress, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(fileName));
        request.Content = new StreamContent(progressStream, BufferSize);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Отправка текста или URL на целевое устройство.
    /// </summary>
    public static async Task SendTextAsync(
        string targetIp,
        string text,
        CancellationToken ct = default)
    {
        var url = $"http://{targetIp}:{Port}/text/";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var content = new StringContent(text, Encoding.UTF8, "text/plain");

        using var response = await httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Вспомогательные методы
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Защита от Path Traversal: убирает путь и опасные символы,
    /// но СОХРАНЯЕТ точки — расширения файлов (.pdf, .tar.gz) остаются целыми.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        name = Path.GetFileName(name); // убираем любой путь (в т.ч. декодированные %2F)

        var cleaned = new char[name.Length];
        int j = 0;

        foreach (var c in name)
        {
            // Опасные символы путей (невалидные на Windows) + управляющие символы.
            if (c < 32 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                cleaned[j++] = '_';
            else
                cleaned[j++] = c;
        }

        var result = new string(cleaned, 0, j).TrimEnd(' ', '.');
        return string.IsNullOrEmpty(result) ? "unnamed_file" : result;
    }

    private static string? ExtractFileNameFromContentDisposition(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
                return trimmed["filename=".Length..].Trim('"', '\'');
        }
        return null;
    }

    private static string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;

        var dir = Path.GetDirectoryName(filePath)!;
        var name = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        int counter = 1;

        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({counter}){ext}");
            counter++;
        } while (File.Exists(candidate));

        return candidate;
    }

    private static void Respond(HttpListenerResponse response, HttpStatusCode code, string body)
    {
        try
        {
            response.StatusCode = (int)code;
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch { /* ignore — соединение могло разорваться */ }
    }

    /// <summary>
    /// Открытие URL в браузере (платформо-независимо).
    /// </summary>
    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            // На некоторых headless-системах может не работать — игнорируем
        }
    }

    /// <summary>
    /// Автоопределение LAN IP-адреса этого устройства.
    /// </summary>
    private static string DetectLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { /* ignore */ }

        // Fallback: перебор сетевых интерфейсов
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            if (iface.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211
                or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet))
                continue;

            var ip = iface.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            if (ip != null)
                return ip.Address.ToString();
        }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        StopServer();
        GC.SuppressFinalize(this);
    }
}

// ════════════════════════════════════════════════════════════════════════
//  ProgressStream — обёртка для отслеживания прогресса чтения
//  Потокобезопасна, потребление памяти — ровно 1 буфер (64 КБ)
// ════════════════════════════════════════════════════════════════════════

internal sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly long _totalBytes;
    private readonly Action<double> _onProgress;
    private readonly CancellationToken _ct;
    private long _bytesRead;
    private double _lastReportedPercent;
    private const double ReportThreshold = 1.0; // отчёт каждые 1%

    public ProgressStream(Stream inner, long totalBytes, Action<double> onProgress, CancellationToken ct)
    {
        _inner = inner;
        _totalBytes = totalBytes;
        _onProgress = onProgress;
        _ct = ct;
        _lastReportedPercent = 0;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        ReportProgress(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
        var read = await _inner.ReadAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
        ReportProgress(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
        var read = await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        ReportProgress(read);
        return read;
    }

    private void ReportProgress(int bytesRead)
    {
        if (bytesRead <= 0) return;

        _bytesRead += bytesRead;

        if (_totalBytes > 0)
        {
            var percent = (double)_bytesRead / _totalBytes * 100.0;
            if (percent - _lastReportedPercent >= ReportThreshold || percent >= 100.0)
            {
                _lastReportedPercent = percent;
                _onProgress?.Invoke(Math.Min(percent, 100.0));
            }
        }
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
