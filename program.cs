using System;
using System.Collections.Generic;
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
    private const string MutexName = @"Global\SseddumeDrugShop_WindowsGateway_v3";

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
    private readonly string _branchConfigFile;
    private string _ownerToken;
    private string _staffToken;
    private string _branchCode = "LOC-MAIN";
    private string _branchName = "MAIN BRANCH";
    private readonly int _port = 8080;
    private readonly object _fileLock = new();
    private string _activeLanIp = "127.0.0.1";

    public MainForm()
    {
        Text = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM — Secure Terminal & Mobile POS Synchronization Gateway";
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
        _branchConfigFile = Path.Combine(_appDataDir, "branch_config.json");

        _ownerToken = LoadOrCreateToken(_ownerTokenFile, "OWN-");
        _staffToken = LoadOrCreateToken(_staffTokenFile, "STF-");
        LoadBranchConfig();

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

    private static string SanitizeTag(string raw, string fallback)
    {
        string cleaned = string.Concat(raw.Trim().ToUpperInvariant().Select(ch =>
            char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-'));
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        cleaned = cleaned.Trim('-');
        return string.IsNullOrEmpty(cleaned) ? fallback : cleaned[..Math.Min(cleaned.Length, 24)];
    }

    private void LoadBranchConfig()
    {
        try
        {
            if (File.Exists(_branchConfigFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_branchConfigFile, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("branchCode", out var c))
                    _branchCode = SanitizeTag(c.GetString() ?? "LOC-MAIN", "LOC-MAIN");
                if (doc.RootElement.TryGetProperty("branchName", out var n))
                {
                    string name = (n.GetString() ?? "MAIN BRANCH").Trim();
                    if (!string.IsNullOrEmpty(name)) _branchName = name;
                }
            }
        }
        catch { }
    }

    private void SaveBranchConfig(string branchCode, string branchName)
    {
        lock (_fileLock)
        {
            _branchCode = SanitizeTag(branchCode, "LOC-MAIN");
            _branchName = string.IsNullOrWhiteSpace(branchName) ? "MAIN BRANCH" : branchName.Trim();
            string json = JsonSerializer.Serialize(new { branchCode = _branchCode, branchName = _branchName });
            File.WriteAllText(_branchConfigFile, json, Encoding.UTF8);
        }
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
        return string.Equals(token, _ownerToken, StringComparison.Ordinal);
    }

    private bool HasOperationalClearance(HttpListenerRequest req)
    {
        if (req.IsLocal) return true;
        string? token = req.Headers["X-Pair-Token"];
        return string.Equals(token, _ownerToken, StringComparison.Ordinal) ||
               string.Equals(token, _staffToken, StringComparison.Ordinal);
    }

    private object BuildGatewaySnapshot(string safeHost)
    {
        string endpoint = $"http://{_activeLanIp}:{_port}";
        return new
        {
            ok = true,
            host = safeHost,
            branchCode = _branchCode,
            branchName = _branchName,
            lanIp = _activeLanIp,
            endpoint,
            staffToken = _staffToken,
            ownerToken = _ownerToken,
            staffQrPayload = $"SSEDDUME-POS|STAFF|{_branchCode}|{safeHost}|{endpoint}|{_staffToken}",
            ownerQrPayload = $"SSEDDUME-POS|OWNER|{_branchCode}|{safeHost}|{endpoint}|{_ownerToken}"
        };
    }

    private void CompactEventLogIfNeeded()
    {
        if (!File.Exists(_eventLogPath)) return;
        var lines = File.ReadAllLines(_eventLogPath, Encoding.UTF8)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();
        if (lines.Length <= 5000) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>(2500);
        for (int i = lines.Length - 1; i >= 0 && kept.Count < 2500; i--)
        {
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                if (doc.RootElement.TryGetProperty("event_id", out var idEl))
                {
                    string id = idEl.GetString() ?? "";
                    if (!string.IsNullOrEmpty(id) && !seen.Add(id)) continue;
                }
                kept.Add(lines[i]);
            }
            catch { }
        }
        kept.Reverse();
        File.WriteAllLines(_eventLogPath, kept, Encoding.UTF8);
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

        string safeHost = SanitizeTag(Environment.MachineName, "STATION");

        try
        {
            if (path == "/api/gateway-info" && ctx.Request.HttpMethod == "GET")
            {
                if (!ctx.Request.IsLocal)
                {
                    await WriteJsonAsync(ctx.Response, 403, "{\"error\":\"Local workstation access only\"}");
                    return;
                }
                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(BuildGatewaySnapshot(safeHost)));
                return;
            }

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
                    await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(BuildGatewaySnapshot(safeHost)));
                    return;
                }

                if (path == "/api/admin/branch" && ctx.Request.HttpMethod == "POST")
                {
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    string body = await reader.ReadToEndAsync();
                    using var doc = JsonDocument.Parse(body);
                    string code = doc.RootElement.TryGetProperty("branchCode", out var c) ? (c.GetString() ?? _branchCode) : _branchCode;
                    string name = doc.RootElement.TryGetProperty("branchName", out var n) ? (n.GetString() ?? _branchName) : _branchName;
                    SaveBranchConfig(code, name);
                    await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(BuildGatewaySnapshot(safeHost)));
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
                            CompactEventLogIfNeeded();
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
