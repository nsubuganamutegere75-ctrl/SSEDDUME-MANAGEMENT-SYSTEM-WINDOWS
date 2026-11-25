using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const string MutexName = @"Global\SseddumeDrugShop_UniversalGateway_v1";

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
    private System.Windows.Forms.Timer? _watchTimer;
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
    private readonly HashSet<string> _boundPrefixes = new(StringComparer.OrdinalIgnoreCase);
    private string _primaryIp = "127.0.0.1";
    private List<string> _allLanIps = new();

    public MainForm()
    {
        Text = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM — Universal Terminal & POS Gateway";
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
            StartWatcher();
            await InitWebViewAsync();
        };
        FormClosing += (_, _) =>
        {
            try { _watchTimer?.Stop(); _watchTimer?.Dispose(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
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

    private static List<string> DetectAllLanIps()
    {
        var ranked = new List<(int score, string ip)>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            string desc = (ni.Description ?? "").ToLowerInvariant();
            string name = (ni.Name ?? "").ToLowerInvariant();
            bool isVirtual = desc.Contains("virtual") || desc.Contains("pseudo") ||
                             desc.Contains("hyper-v") || desc.Contains("vmware") ||
                             desc.Contains("wsl") || name.Contains("vethernet");

            int baseScore = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 100 :
                            ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 80 : 20;
            if (isVirtual) baseScore -= 50;

            var ipProps = ni.GetIPProperties();
            bool hasGw = ipProps.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                g.Address.ToString() != "0.0.0.0");
            if (hasGw) baseScore += 25;

            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ip = ua.Address.ToString();
                    if (string.IsNullOrWhiteSpace(ip) || ip == "127.0.0.1" || ip.StartsWith("169.254."))
                        continue;

                    int sc = baseScore;
                    if (ip.StartsWith("192.168.137.")) sc += 25;
                    else if (ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.")) sc += 10;

                    ranked.Add((sc, ip));
                }
            }
        }

        return ranked
            .OrderByDescending(x => x.score)
            .Select(x => x.ip)
            .Distinct()
            .ToList();
    }

    private void TryAddPrefix(HttpListener listener, string prefix)
    {
        if (_boundPrefixes.Contains(prefix)) return;
        try
        {
            listener.Prefixes.Add(prefix);
            _boundPrefixes.Add(prefix);
        }
        catch { }
    }

    private void SyncInterfacesAndPrefixes(bool notifyUi = false)
    {
        _allLanIps = DetectAllLanIps();
        _primaryIp = _allLanIps.FirstOrDefault() ?? "127.0.0.1";

        if (_listener != null && _listener.IsListening)
        {
            foreach (var ip in _allLanIps)
            {
                TryAddPrefix(_listener, $"http://{ip}:{_port}/");
            }
        }

        if (notifyUi && _webView.CoreWebView2 != null)
        {
            string safeHost = SanitizeTag(Environment.MachineName, "STATION");
            var msg = new { type = "GATEWAY_INFO", data = BuildGatewaySnapshot(safeHost) };
            try { _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { }
        }
    }

    private void StartWatcher()
    {
        _watchTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _watchTimer.Tick += (_, _) =>
        {
            string prev = _primaryIp;
            SyncInterfacesAndPrefixes(notifyUi: false);
            if (_primaryIp != prev) SyncInterfacesAndPrefixes(notifyUi: true);
        };
        _watchTimer.Start();
    }

    private void StartGatewayServer()
    {
        _allLanIps = DetectAllLanIps();
        _primaryIp = _allLanIps.FirstOrDefault() ?? "127.0.0.1";

        var listener = new HttpListener();
        TryAddPrefix(listener, $"http://localhost:{_port}/");
        TryAddPrefix(listener, $"http://127.0.0.1:{_port}/");
        TryAddPrefix(listener, $"http://+:{_port}/");

        foreach (var ip in _allLanIps)
        {
            TryAddPrefix(listener, $"http://{ip}:{_port}/");
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
                var fallback = new HttpListener();
                _boundPrefixes.Clear();
                TryAddPrefix(fallback, $"http://localhost:{_port}/");
                TryAddPrefix(fallback, $"http://127.0.0.1:{_port}/");
                foreach (var ip in _allLanIps)
                {
                    TryAddPrefix(fallback, $"http://{ip}:{_port}/");
                }
                fallback.Start();
                _listener = fallback;
                _ = Task.Run(ListenLoopAsync);
            }
            catch { }
        }
    }

    private void RunElevatedFirewallAndAclHelper()
    {
        try
        {
            string cmd =
                $"netsh http add urlacl url=http://+:{_port}/ user=Everyone & " +
                $"netsh advfirewall firewall add rule name=\"Sseddume POS Gateway {_port}\" dir=in action=allow protocol=TCP localport={_port}";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {cmd}",
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            var proc = Process.Start(psi);
            proc?.WaitForExit(8000);
            StartGatewayServer();
            SyncInterfacesAndPrefixes(notifyUi: true);
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
        string ip = _primaryIp;
        string endpoint = $"http://{ip}:{_port}";
        return new
        {
            ok = true,
            host = safeHost,
            branchCode = _branchCode,
            branchName = _branchName,
            lanIp = ip,
            lanIps = _allLanIps,
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
        string safeHost = SanitizeTag(Environment.MachineName, "STATION");

        try
        {
            if (path == "/api/ping" && ctx.Request.HttpMethod == "GET")
            {
                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(new
                {
                    ok = true,
                    service = "SSEDDUME-POS",
                    host = safeHost,
                    branchCode = _branchCode,
                    branchName = _branchName,
                    lanIp = _primaryIp
                }));
                return;
            }

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

    private void WireWebViewIpc()
    {
        if (_webView.CoreWebView2 == null) return;
        _webView.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                string cmd = doc.RootElement.GetProperty("cmd").GetString() ?? "";
                string safeHost = SanitizeTag(Environment.MachineName, "STATION");
                SyncInterfacesAndPrefixes(notifyUi: false);

                if (cmd == "GET_GATEWAY_INFO")
                {
                    var payload = new { type = "GATEWAY_INFO", data = BuildGatewaySnapshot(safeHost) };
                    _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
                }
                else if (cmd == "REPAIR_PORT_8080")
                {
                    RunElevatedFirewallAndAclHelper();
                }
                else if (cmd == "ROTATE_KEYS")
                {
                    RotatePairingTokens();
                    var payload = new { type = "GATEWAY_INFO", data = BuildGatewaySnapshot(safeHost) };
                    _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
                }
                else if (cmd == "SAVE_BRANCH")
                {
                    string code = doc.RootElement.TryGetProperty("branchCode", out var c) ? (c.GetString() ?? _branchCode) : _branchCode;
                    string name = doc.RootElement.TryGetProperty("branchName", out var n) ? (n.GetString() ?? _branchName) : _branchName;
                    SaveBranchConfig(code, name);
                    var payload = new { type = "GATEWAY_INFO", data = BuildGatewaySnapshot(safeHost) };
                    _webView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
                }
                else if (cmd == "PUSH_EVENT")
                {
                    if (doc.RootElement.TryGetProperty("event", out var evEl))
                    {
                        lock (_fileLock)
                        {
                            File.AppendAllText(_eventLogPath, evEl.GetRawText() + Environment.NewLine, Encoding.UTF8);
                            CompactEventLogIfNeeded();
                        }
                    }
                }
                else if (cmd == "POLL_EVENTS")
                {
                    string[] lines;
                    lock (_fileLock)
                    {
                        lines = File.Exists(_eventLogPath)
                            ? File.ReadAllLines(_eventLogPath, Encoding.UTF8)
                            : Array.Empty<string>();
                    }
                    string jsonArray = "[" + string.Join(",", lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(1000)) + "]";
                    _webView.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"EVENTS_BATCH\",\"data\":{jsonArray}}}");
                }
            }
            catch { }
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
            await File.WriteAllTextAsync(indexPath, ReadEmbeddedHtml(), Encoding.UTF8);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsZoomControlEnabled = true;

            WireWebViewIpc();

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
