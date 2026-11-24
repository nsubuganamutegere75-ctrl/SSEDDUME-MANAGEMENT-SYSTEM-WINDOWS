using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private const string AppTitle = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM — Windows Companion Station";
    private const string MutexName = @"Global\SseddumeDrugShop_WindowsCompanion_v1";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Activate existing window if already running
            IntPtr existingHwnd = FindWindow(null, AppTitle);
            if (existingHwnd != IntPtr.Zero)
            {
                ShowWindow(existingHwnd, SW_RESTORE);
                SetForegroundWindow(existingHwnd);
            }
            else
            {
                MessageBox.Show(
                    "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM is already running on this station.",
                    "Instance Running",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
    }
}

internal sealed class MainForm : Form
{
    private const string WindowTitle = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM — Windows Companion Station";
    private readonly WebView2 _webView;
    private readonly CompanionServer _server;
    private readonly string _appDataDir;

    public MainForm()
    {
        Text = WindowTitle;
        Width = 1380;
        Height = 850;
        MinimumSize = new Size(1024, 680);
        StartPosition = FormStartPosition.CenterScreen;

        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SseddumeDrugShop"
        );
        Directory.CreateDirectory(_appDataDir);

        _server = new CompanionServer(_appDataDir, preferredPort: 8080);
        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        Load += async (_, _) =>
        {
            _server.Start();
            await InitWebViewAsync();
        };

        FormClosing += (_, _) =>
        {
            _server.Dispose();
            _webView.Dispose();
        };
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
            string htmlContent = LoadEmbeddedHtmlOrGenerateFallback();
            await File.WriteAllTextAsync(indexPath, htmlContent, Encoding.UTF8);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            // Map using HTTP to eliminate Mixed Content restrictions when calling companion HTTP APIs
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "sseddume.station",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow
            );

            // Setup Direct JavaScript <-> C# IPC Message Pipeline
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            _webView.Source = new Uri("http://sseddume.station/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "Microsoft Edge WebView2 Runtime is required.\nPlease install the Evergreen WebView2 Runtime from Microsoft.",
                "Runtime Missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to initialize desktop environment:\n{ex.Message}",
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string message = e.TryGetWebMessageAsString();
            if (string.Equals(message, "get-status", StringComparison.OrdinalIgnoreCase))
            {
                var payload = _server.GetPairingInfo();
                _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
            }
        }
        catch { }
    }

    private string LoadEmbeddedHtmlOrGenerateFallback()
    {
        var asm = Assembly.GetExecutingAssembly();
        string resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith("app.html", StringComparison.OrdinalIgnoreCase)) ?? "";

        if (!string.IsNullOrEmpty(resourceName))
        {
            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }

        // Functional, interactive standalone UI fallback if app.html is not embedded
        var info = _server.GetPairingInfo();
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Sseddume DSMS Companion</title>
            <style>
                :root { --primary: #0284c7; --bg: #0f172a; --card: #1e293b; --text: #f8fafc; --accent: #22c55e; }
                body { margin: 0; padding: 24px; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: var(--bg); color: var(--text); }
                .container { max-width: 900px; margin: 0 auto; }
                .card { background: var(--card); border-radius: 12px; padding: 24px; margin-bottom: 20px; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.3); border: 1px solid #334155; }
                h1 { margin-top: 0; font-size: 24px; color: var(--primary); }
                .badge { display: inline-block; padding: 4px 10px; border-radius: 9999px; font-size: 12px; font-weight: bold; background: #065f46; color: #34d399; }
                .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 16px; margin-top: 16px; }
                .stat-box { background: #0f172a; padding: 14px; border-radius: 8px; border: 1px solid #334155; }
                .stat-label { font-size: 12px; color: #94a3b8; text-transform: uppercase; }
                .stat-value { font-size: 16px; font-weight: 600; margin-top: 4px; word-break: break-all; }
                .token-box { background: #0f172a; color: #38bdf8; padding: 12px; font-family: monospace; font-size: 16px; border-radius: 6px; border: 1px dashed var(--primary); margin: 12px 0; }
                button { background: var(--primary); color: white; border: none; padding: 10px 16px; border-radius: 6px; cursor: pointer; font-weight: 600; transition: opacity 0.2s; }
                button:hover { opacity: 0.9; }
                #logOutput { width: 100%; height: 260px; background: #020617; color: #a5f3fc; border: 1px solid #334155; border-radius: 6px; font-family: monospace; font-size: 12px; padding: 12px; box-sizing: border-box; resize: none; }
            </style>
        </head>
        <body>
            <div class="container">
                <div class="card">
                    <div style="display:flex; justify-content:space-between; align-items:center;">
                        <h1>SSEDDUME DRUG SHOP MANAGEMENT SYSTEM</h1>
                        <span class="badge">SYSTEM READY</span>
                    </div>
                    <p style="color:#94a3b8; margin-top:0;">Windows Station Companion Active & Syncing</p>
                    <div class="grid">
                        <div class="stat-box"><div class="stat-label">Station Hostname</div><div class="stat-value">{{info.Host}}</div></div>
                        <div class="stat-box"><div class="stat-label">LAN Service IP</div><div class="stat-value">{{info.LanIp}}</div></div>
                        <div class="stat-box"><div class="stat-label">Local Port</div><div class="stat-value">{{info.Port}}</div></div>
                    </div>
                    <h3>Pairing Security Token</h3>
                    <div class="token-box" id="tokenDisplay">{{info.Token}}</div>
                    <div style="display:flex; gap:10px;">
                        <button onclick="navigator.clipboard.writeText('{{info.Token}}'); alert('Token copied!');">Copy Pair Token</button>
                        <button onclick="sendTestEvent()">Push Diagnostic Event</button>
                        <button onclick="refreshLogs()">Refresh Stream</button>
                    </div>
                </div>
                <div class="card">
                    <h3>Real-Time Event Stream</h3>
                    <textarea id="logOutput" readonly placeholder="Waiting for incoming POS & Companion events..."></textarea>
                </div>
            </div>
            <script>
                const endpoint = "http://localhost:{{info.Port}}";
                const token = "{{info.Token}}";

                async function refreshLogs() {
                    try {
                        const res = await fetch(`${endpoint}/api/events`, { headers: { 'X-Pair-Token': token } });
                        const data = await res.json();
                        document.getElementById('logOutput').value = data.map(d => JSON.stringify(d)).join('\n');
                    } catch (e) {
                        document.getElementById('logOutput').value = "Error connecting to companion stream: " + e.message;
                    }
                }

                async function sendTestEvent() {
                    const testPayload = {
                        eventId: "EVT-" + Date.now(),
                        timestamp: new Date().toISOString(),
                        action: "DIAGNOSTIC_TEST",
                        station: "{{info.Host}}"
                    };
                    await fetch(`${endpoint}/api/events`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json', 'X-Pair-Token': token },
                        body: JSON.stringify(testPayload)
                    });
                    refreshLogs();
                }

                window.addEventListener('load', refreshLogs);
                setInterval(refreshLogs, 5000);
            </script>
        </body>
        </html>
        """;
    }
}

public sealed record CompanionInfo(bool Ok, string Host, string LanIp, int Port, string Endpoint, string Token, string QrPayload);

internal sealed class CompanionServer : IDisposable
{
    private readonly int _preferredPort;
    private readonly string _eventLogPath;
    private readonly string _pairToken;
    private readonly string _safeHost;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private HttpListener? _listener;
    private string _activeLanIp = "127.0.0.1";
    private int _boundPort;

    public CompanionServer(string appDataDir, int preferredPort = 8080)
    {
        _preferredPort = preferredPort;
        _eventLogPath = Path.Combine(appDataDir, "companion_events.jsonl");
        _pairToken = LoadOrCreatePairToken(Path.Combine(appDataDir, "pair_token.txt"));

        string rawHost = Environment.MachineName;
        _safeHost = string.Concat(rawHost.Where(ch => char.IsLetterOrDigit(ch) || ch == '-'));
        if (string.IsNullOrEmpty(_safeHost)) _safeHost = "Station";
    }

    public CompanionInfo GetPairingInfo()
    {
        string endpoint = $"http://{_activeLanIp}:{_boundPort}";
        return new CompanionInfo(
            Ok: true,
            Host: _safeHost,
            LanIp: _activeLanIp,
            Port: _boundPort,
            Endpoint: endpoint,
            Token: _pairToken,
            QrPayload: $"SSEDDUME-WIN|{_safeHost}|{endpoint}|{_pairToken}"
        );
    }

    public void Start()
    {
        _activeLanIp = DetectBestLanIp();

        // Attempt preferred port down through 5 fallbacks
        for (int p = _preferredPort; p <= _preferredPort + 5; p++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{p}/");
            listener.Prefixes.Add($"http://127.0.0.1:{p}/");

            if (_activeLanIp != "127.0.0.1")
            {
                try { listener.Prefixes.Add($"http://{_activeLanIp}:{p}/"); } catch { }
            }

            try
            {
                listener.Start();
                _listener = listener;
                _boundPort = p;
                Task.Run(() => ListenLoopAsync(_cts.Token));
                return;
            }
            catch
            {
                listener.Close();
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleHttpRequestAsync(ctx), ct);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch { }
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext ctx)
    {
        // Universal CORS Preflight Handlers
        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
        ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Pair-Token");

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            // 1. Station Discovery / Pairing Info (Localhost Only for Security)
            if (path == "/api/pair-info" && ctx.Request.HttpMethod == "GET")
            {
                if (!ctx.Request.IsLocal)
                {
                    await WriteJsonAsync(ctx.Response, 403, "{\"error\":\"Access denied: Pairing endpoint is local-only.\"}");
                    return;
                }

                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(GetPairingInfo()));
                return;
            }

            // 2. Events Ingestion (POST)
            if (path == "/api/events" && ctx.Request.HttpMethod == "POST")
            {
                if (!IsAuthorized(ctx.Request))
                {
                    await WriteJsonAsync(ctx.Response, 401, "{\"error\":\"Unauthorized: Invalid or missing X-Pair-Token\"}");
                    return;
                }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();

                if (!string.IsNullOrWhiteSpace(body))
                {
                    await _fileLock.WaitAsync();
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            var lines = new List<string>();
                            foreach (var element in doc.RootElement.EnumerateArray())
                            {
                                lines.Add(JsonSerializer.Serialize(element));
                            }
                            await File.AppendAllLinesAsync(_eventLogPath, lines, Encoding.UTF8);
                        }
                        else
                        {
                            string minified = JsonSerializer.Serialize(doc.RootElement);
                            await File.AppendAllTextAsync(_eventLogPath, minified + Environment.NewLine, Encoding.UTF8);
                        }
                    }
                    finally
                    {
                        _fileLock.Release();
                    }
                }

                await WriteJsonAsync(ctx.Response, 200, "{\"ok\":true}");
                return;
            }

            // 3. Events Stream Retrieval (GET)
            if (path == "/api/events" && ctx.Request.HttpMethod == "GET")
            {
                if (!IsAuthorized(ctx.Request))
                {
                    await WriteJsonAsync(ctx.Response, 401, "{\"error\":\"Unauthorized: Invalid or missing X-Pair-Token\"}");
                    return;
                }

                var recentLines = await ReadRecentEventsAsync(maxLines: 1000);
                string jsonArray = "[" + string.Join(",", recentLines) + "]";

                await WriteJsonAsync(ctx.Response, 200, jsonArray);
                return;
            }

            await WriteJsonAsync(ctx.Response, 404, "{\"error\":\"Endpoint not found\"}");
        }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx.Response, 400, "{\"error\":\"Malformed JSON request body\"}");
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(ctx.Response, 500, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (request.IsLocal) return true;

        string? headerToken = request.Headers["X-Pair-Token"];
        if (string.IsNullOrEmpty(headerToken)) return false;

        byte[] expected = Encoding.UTF8.GetBytes(_pairToken);
        byte[] actual = Encoding.UTF8.GetBytes(headerToken);

        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private async Task<List<string>> ReadRecentEventsAsync(int maxLines)
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_eventLogPath)) return new List<string>();

            // Circular Queue reading: O(1) eviction without loading full files into memory
            var queue = new Queue<string>(maxLines);
            using var stream = new FileStream(_eventLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (queue.Count >= maxLines) queue.Dequeue();
                    queue.Enqueue(line);
                }
            }
            return queue.ToList();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int status, string json)
    {
        try
        {
            resp.StatusCode = status;
            resp.ContentType = "application/json; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
        finally
        {
            try { resp.Close(); } catch { }
        }
    }

    private static string LoadOrCreatePairToken(string tokenFile)
    {
        if (File.Exists(tokenFile))
        {
            string existing = File.ReadAllText(tokenFile).Trim();
            if (!string.IsNullOrEmpty(existing)) return existing;
        }

        string created = "PAIR-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        File.WriteAllText(tokenFile, created);
        return created;
    }

    private static string DetectBestLanIp()
    {
        try
        {
            // First attempt: Connect to DNS to determine active outgoing route
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

        // Fallback for offline LANs: Enumerate physical adapters excluding virtual/VPN adapters
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                string desc = ni.Description.ToLowerInvariant();
                if (desc.Contains("virtual") || desc.Contains("hyper-v") || desc.Contains("wsl") ||
                    desc.Contains("vmware") || desc.Contains("tailscale") || desc.Contains("zerotier"))
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = ua.Address.ToString();
                        if (!ip.StartsWith("169.254.") && ip != "127.0.0.1")
                            return ip;
                    }
                }
            }
        }
        catch { }

        return "127.0.0.1";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _fileLock.Dispose();
        _cts.Dispose();
    }
}
