using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SseddumeDrugShop;

internal static class Program
{
    private const string MutexName = @"Global\SseddumeDrugShop_WindowsCompanion_v1";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM is already running on this workstation.",
                "SSEDDUME DSMS",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private HttpListener? _listener;
    private readonly string _appDataDir;
    private readonly string _eventLogPath;
    private readonly string _ownerTokenFile;
    private readonly string _staffTokenFile;
    private string _ownerToken;
    private string _staffToken;
    private readonly int _port = 8080;
    private readonly object _fileLock = new();
    private string _activeLanIp = "127.0.0.1";

    public MainForm()
    {
        Text = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM — Secure Terminal & Mobile POS Gateway";
        Width = 1380;
        Height = 850;
        MinimumSize = new Size(1024, 680);
        StartPosition = FormStartPosition.CenterScreen;

        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SseddumeDrugShop"
        );
        Directory.CreateDirectory(_appDataDir);
        _eventLogPath = Path.Combine(_appDataDir, "companion_events.jsonl");
        _ownerTokenFile = Path.Combine(_appDataDir, "owner_pair_token.txt");
        _staffTokenFile = Path.Combine(_appDataDir, "staff_pair_token.txt");

        _ownerToken = LoadOrCreateToken(_ownerTokenFile, "OWN-");
        _staffToken = LoadOrCreateToken(_staffTokenFile, "STF-");

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        Load += async (_, _) =>
        {
            StartGatewayServer();
            await InitWebViewAsync();
        };
        FormClosing += (_, _) =>
        {
            try { _listener?.Stop(); } catch { }
        };
    }

    private static string LoadOrCreateToken(string path, string prefix)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(existing)) return existing;
        }
        string created = prefix + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        File.WriteAllText(path, created);
        return created;
    }

    private void RotatePairingTokens()
    {
        lock (_fileLock)
        {
            _ownerToken = "OWN-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            _staffToken = "STF-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            File.WriteAllText(_ownerTokenFile, _ownerToken);
            File.WriteAllText(_staffTokenFile, _staffToken);
        }
    }

    private static string DetectBestLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                string ip = endPoint.Address.ToString();
                if (!string.IsNullOrWhiteSpace(ip) && !ip.StartsWith("169.254.") && ip != "127.0.0.1")
                    return ip;
            }
        }
        catch { }

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ip = ua.Address.ToString();
                    if (!ip.StartsWith("169.254.") && ip != "127.0.0.1") return ip;
                }
            }
        }
        return "127.0.0.1";
    }

    private void StartGatewayServer()
    {
        _activeLanIp = DetectBestLanIp();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{_port}/");
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

        if (_activeLanIp != "127.0.0.1")
        {
            try { listener.Prefixes.Add($"http://{_activeLanIp}:{_port}/"); } catch { }
        }

        try
        {
            listener.Start();
            _listener = listener;
            _ = Task.Run(ListenLoopAsync);
        }
        catch
        {
            try
            {
                var localOnly = new HttpListener();
                localOnly.Prefixes.Add($"http://localhost:{_port}/");
                localOnly.Prefixes.Add($"http://127.0.0.1:{_port}/");
                localOnly.Start();
                _listener = localOnly;
                _ = Task.Run(ListenLoopAsync);
            }
            catch { }
        }
    }

    private void EnsureLanPrefixBound(string newIp)
    {
        if (string.IsNullOrWhiteSpace(newIp) || newIp == "127.0.0.1" || newIp == _activeLanIp || _listener == null)
            return;
        try
        {
            string prefix = $"http://{newIp}:{_port}/";
            if (!_listener.Prefixes.Contains(prefix))
            {
                _listener.Prefixes.Add(prefix);
            }
            _activeLanIp = newIp;
        }
        catch { }
    }

    private async Task ListenLoopAsync()
    {
        while (_listener != null && _listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleHttpRequestAsync(ctx));
            }
            catch { break; }
        }
    }

    private bool HasOwnerClearance(HttpListenerRequest req)
    {
        string? token = req.Headers["X-Pair-Token"];
        if (string.Equals(token, _ownerToken, StringComparison.Ordinal)) return true;
        // Local UI must also present the Owner token for /api/admin/* routes
        return false;
    }

    private bool HasOperationalClearance(HttpListenerRequest req)
    {
        if (req.IsLocal) return true;
        string? token = req.Headers["X-Pair-Token"];
        return string.Equals(token, _ownerToken, StringComparison.Ordinal) ||
               string.Equals(token, _staffToken, StringComparison.Ordinal);
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext ctx)
    {
        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
        ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Pair-Token");

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        string currentIp = DetectBestLanIp();
        if (currentIp != "127.0.0.1") EnsureLanPrefixBound(currentIp);

        string safeHost = string.Concat(Environment.MachineName.Where(ch => char.IsLetterOrDigit(ch) || ch == '-'));
        if (string.IsNullOrEmpty(safeHost)) safeHost = "Station";
        string endpoint = $"http://{_activeLanIp}:{_port}";
        string staffQrPayload = $"SSEDDUME-POS|STAFF|{safeHost}|{endpoint}|{_staffToken}";
        string ownerQrPayload = $"SSEDDUME-POS|OWNER|{safeHost}|{endpoint}|{_ownerToken}";

        try
        {
            // Localhost workstation UI bootstrap endpoint
            if (path == "/api/gateway-info" && ctx.Request.HttpMethod == "GET")
            {
                if (!ctx.Request.IsLocal)
                {
                    await WriteJsonAsync(ctx.Response, 403, "{\"error\":\"Local workstation access only\"}");
                    return;
                }
                var info = new
                {
                    ok = true,
                    host = safeHost,
                    lanIp = _activeLanIp,
                    endpoint,
                    staffToken = _staffToken,
                    ownerToken = _ownerToken,
                    staffQrPayload,
                    ownerQrPayload
                };
                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(info));
                return;
            }

            // Strictly protected /api/admin/* routes — requires Owner / Administrator clearance
            if (path.StartsWith("/api/admin/", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasOwnerClearance(ctx.Request))
                {
                    await WriteJsonAsync(ctx.Response, 403, "{\"error\":\"Forbidden: Owner / Administrator clearance required\"}");
                    return;
                }

                if (path == "/api/admin/rotate-keys" && ctx.Request.HttpMethod == "POST")
                {
                    RotatePairingTokens();
                    string newEndpoint = $"http://{_activeLanIp}:{_port}";
                    var rotated = new
                    {
                        ok = true,
                        staffToken = _staffToken,
                        ownerToken = _ownerToken,
                        staffQrPayload = $"SSEDDUME-POS|STAFF|{safeHost}|{newEndpoint}|{_staffToken}",
                        ownerQrPayload = $"SSEDDUME-POS|OWNER|{safeHost}|{newEndpoint}|{_ownerToken}"
                    };
                    await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(rotated));
                    return;
                }

                if (path == "/api/admin/audit-stream" && ctx.Request.HttpMethod == "GET")
                {
                    string[] lines;
                    lock (_fileLock)
                    {
                        lines = File.Exists(_eventLogPath)
                            ? File.ReadAllLines(_eventLogPath, Encoding.UTF8)
                            : Array.Empty<string>();
                    }
                    string jsonArray = "[" + string.Join(",", lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(2000)) + "]";
                    await WriteJsonAsync(ctx.Response, 200, jsonArray);
                    return;
                }

                await WriteJsonAsync(ctx.Response, 404, "{\"error\":\"Unknown admin endpoint\"}");
                return;
            }

            // /api/events — Accessible to both Staff Terminals and Owner/Admin devices
            if (path == "/api/events")
            {
                if (!HasOperationalClearance(ctx.Request))
                {
                    await WriteJsonAsync(ctx.Response, 401, "{\"error\":\"Unauthorized terminal credential\"}");
                    return;
                }

                if (ctx.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    string body = await reader.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        lock (_fileLock)
                        {
                            using var doc = JsonDocument.Parse(body);
                            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                            {
                                var lines = doc.RootElement.EnumerateArray().Select(el => el.GetRawText());
                                File.AppendAllLines(_eventLogPath, lines, Encoding.UTF8);
                            }
                            else
                            {
                                File.AppendAllText(_eventLogPath, doc.RootElement.GetRawText() + Environment.NewLine, Encoding.UTF8);
                            }
                        }
                    }
                    await WriteJsonAsync(ctx.Response, 200, "{\"ok\":true}");
                    return;
                }

                if (ctx.Request.HttpMethod == "GET")
                {
                    string[] lines;
                    lock (_fileLock)
                    {
                        lines = File.Exists(_eventLogPath)
                            ? File.ReadAllLines(_eventLogPath, Encoding.UTF8)
                            : Array.Empty<string>();
                    }
                    // Strip sensitive audit-only entries for non-owner requests
                    bool isOwner = ctx.Request.IsLocal || string.Equals(ctx.Request.Headers["X-Pair-Token"], _ownerToken, StringComparison.Ordinal);
                    var filtered = lines
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Where(l => isOwner || !l.Contains("\"AUDIT_NOTE\"", StringComparison.Ordinal))
                        .TakeLast(1000);
                    string jsonArray = "[" + string.Join(",", filtered) + "]";
                    await WriteJsonAsync(ctx.Response, 200, jsonArray);
                    return;
                }
            }

            await WriteJsonAsync(ctx.Response, 404, "{\"error\":\"Endpoint not found\"}");
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(ctx.Response, 500, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, string json)
    {
        resp.StatusCode = status;
        resp.ContentType = "application/json; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        resp.Close();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(_appDataDir, "WebView2Data");
            string webRoot = Path.Combine(_appDataDir, "www");
            Directory.CreateDirectory(userDataFolder);
            Directory.CreateDirectory(webRoot);

            string indexPath = Path.Combine(webRoot, "index.html");
            await File.WriteAllTextAsync(indexPath, ReadEmbeddedHtml(), Encoding.UTF8);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "sseddume.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow
            );

            _webView.Source = new Uri("https://sseddume.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to initialize Windows workstation container:\n{ex.Message}",
                "SSEDDUME DSMS Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private static string ReadEmbeddedHtml()
    {
        var asm = Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream("SseddumeDrugShop.app.html");
        if (stream == null)
        {
            throw new InvalidOperationException("Embedded app.html resource was not found inside the executable.");
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
