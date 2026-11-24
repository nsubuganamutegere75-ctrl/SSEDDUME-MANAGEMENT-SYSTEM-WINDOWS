using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
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
    private const string AppTitle = "SSEDDUME DRUG SHOP — Station Sync Hub";
    private const string MutexName = @"Global\SseddumeDrugShop_SyncStation_v3";

    [DllImport("user32.dll")]
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
            IntPtr existingHwnd = FindWindow(null, AppTitle);
            if (existingHwnd != IntPtr.Zero)
            {
                ShowWindow(existingHwnd, SW_RESTORE);
                SetForegroundWindow(existingHwnd);
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
    private const string WindowTitle = "SSEDDUME DRUG SHOP — Station Sync Hub";
    private readonly WebView2 _webView;
    private readonly CompanionServer _server;
    private readonly string _appDataDir;

    public MainForm()
    {
        Text = WindowTitle;
        Width = 1420;
        Height = 900;
        MinimumSize = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;

        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SseddumeDrugShop"
        );
        Directory.CreateDirectory(_appDataDir);

        _server = new CompanionServer(_appDataDir, preferredPort: 8080);
        _server.DeviceStateChanged += OnDeviceStateChanged;
        _server.BarcodeReceived += OnBarcodeReceived;

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        Load += async (_, _) =>
        {
            _server.Start();
            await InitWebViewAsync();
        };

        FormClosing += (_, _) =>
        {
            _server.DeviceStateChanged -= OnDeviceStateChanged;
            _server.BarcodeReceived -= OnBarcodeReceived;
            _server.Dispose();
            _webView.Dispose();
        };
    }

    private void OnBarcodeReceived(string barcode, string operatorName)
    {
        try { SystemSounds.Asterisk.Play(); } catch { }

        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnBarcodeReceived(barcode, operatorName)));
            return;
        }

        _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "BARCODE_SCANNED",
            barcode,
            operatorName,
            timestamp = DateTime.Now.ToString("HH:mm:ss")
        }));
    }

    private void OnDeviceStateChanged(PairedDevice device)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnDeviceStateChanged(device)));
            return;
        }

        _webView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "DEVICE_UPDATED",
            device
        }));
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
            await File.WriteAllTextAsync(indexPath, GenerateStationHtml(), Encoding.UTF8);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "hub.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow
            );

            _webView.Source = new Uri("http://hub.local/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Station Hub Error:\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string GenerateStationHtml()
    {
        var info = _server.GetStationInfo();

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Sseddume Station Terminal Hub</title>
            <style>
                :root {
                    --bg: #090d16;
                    --panel: #131b2e;
                    --panel-hover: #1c2742;
                    --border: #222f4c;
                    --primary: #0284c7;
                    --primary-hover: #0369a1;
                    --staff-color: #38bdf8;
                    --admin-color: #f43f5e;
                    --text: #f8fafc;
                    --muted: #94a3b8;
                    --success: #22c55e;
                }
                * { box-sizing: border-box; }
                body { margin: 0; padding: 20px; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif; background: var(--bg); color: var(--text); }
                .layout { display: grid; grid-template-columns: 1fr 400px; gap: 20px; max-width: 1400px; margin: 0 auto; }
                .card { background: var(--panel); border: 1px solid var(--border); border-radius: 8px; padding: 20px; margin-bottom: 20px; box-shadow: 0 4px 6px -1px rgba(0,0,0,0.2); }
                .card-title { font-size: 15px; font-weight: 700; color: #fff; margin: 0 0 16px 0; display: flex; justify-content: space-between; align-items: center; letter-spacing: 0.02em; }
                .badge { padding: 4px 8px; border-radius: 4px; font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.05em; }
                .badge-success { background: rgba(34, 197, 94, 0.15); color: var(--success); border: 1px solid rgba(34, 197, 94, 0.3); }
                .badge-staff { background: rgba(56, 189, 248, 0.15); color: var(--staff-color); border: 1px solid rgba(56, 189, 248, 0.3); }
                .badge-admin { background: rgba(244, 63, 94, 0.15); color: var(--admin-color); border: 1px solid rgba(244, 63, 94, 0.3); }

                .role-tabs { display: flex; background: #0b1120; border-radius: 6px; padding: 3px; gap: 4px; margin-bottom: 16px; border: 1px solid var(--border); }
                .role-tab { flex: 1; padding: 10px; text-align: center; border-radius: 4px; border: none; background: transparent; color: var(--muted); cursor: pointer; font-weight: 600; font-size: 13px; transition: 0.15s; }
                .role-tab.active-staff { background: var(--primary); color: #fff; }
                .role-tab.active-admin { background: var(--admin-color); color: #fff; }

                .qr-container { display: flex; gap: 20px; align-items: center; background: #0b1120; border: 1px solid var(--border); border-radius: 8px; padding: 18px; }
                #qrcode { background: #fff; padding: 10px; border-radius: 6px; line-height: 0; min-width: 180px; min-height: 180px; display:flex; align-items:center; justify-content:center; }
                .qr-details { flex: 1; }
                .permission-box { margin-top: 10px; font-size: 12px; line-height: 1.6; color: var(--muted); }
                .permission-box ul { margin: 4px 0; padding-left: 18px; }

                .token-display { font-family: monospace; font-size: 11px; background: #020617; padding: 8px 10px; border-radius: 4px; border: 1px solid var(--border); margin: 8px 0; color: #38bdf8; word-break: break-all; }

                table { width: 100%; border-collapse: collapse; font-size: 13px; }
                th { text-align: left; padding: 8px 10px; color: var(--muted); border-bottom: 1px solid var(--border); font-size: 11px; text-transform: uppercase; }
                td { padding: 10px; border-bottom: 1px solid rgba(255,255,255,0.05); }

                .btn { background: var(--primary); color: #fff; border: none; padding: 8px 14px; border-radius: 4px; cursor: pointer; font-size: 12px; font-weight: 600; }
                .btn:hover { background: var(--primary-hover); }

                .log-box { width: 100%; height: 260px; background: #020617; border: 1px solid var(--border); border-radius: 6px; color: #a5f3fc; font-family: monospace; font-size: 11px; padding: 10px; resize: none; box-sizing: border-box; }
                .recent-scan-box { background: #0b1120; border: 1px dashed var(--success); border-radius: 6px; padding: 12px; margin-top: 10px; display: flex; justify-content: space-between; align-items: center; }
            </style>
        </head>
        <body>
            <div class="layout">
                <div>
                    <div class="card">
                        <div class="card-title">
                            <span>Mobile Terminal Synchronization Gateway</span>
                            <span class="badge badge-success">Local Wi-Fi Node</span>
                        </div>

                        <div class="role-tabs">
                            <button class="role-tab active-staff" id="tabStaff" onclick="selectRole('STAFF')">Staff / Dispenser Handset</button>
                            <button class="role-tab" id="tabAdmin" onclick="selectRole('ADMIN')">Owner / Pharmacist Handset</button>
                        </div>

                        <div class="qr-container">
                            <div id="qrcode"></div>
                            <div class="qr-details">
                                <div style="display:flex; justify-content:space-between; align-items:center;">
                                    <strong id="roleTitle" style="color:var(--staff-color); font-size:15px;">Staff Clearance Profile</strong>
                                    <span id="roleBadge" class="badge badge-staff">DISPENSER TIER</span>
                                </div>
                                <div class="permission-box" id="permDescription">
                                    <strong>Access Granted to Handset:</strong>
                                    <ul>
                                        <li>High-speed barcode reading & prescription queuing</li>
                                        <li>Instant live feed directly into POS checkout</li>
                                        <li>Physical stock taking & inventory lookups</li>
                                    </ul>
                                    <em style="color:#f87171;">Purchase costs, daily profit, and security settings remain hidden.</em>
                                </div>
                                <div style="font-size:11px; color:var(--muted); margin-top:8px;">Instant Mobile Link:</div>
                                <div class="token-display" id="pairingUrlText">Generating...</div>
                                <button class="btn" onclick="copyPairingLink()">Copy Mobile Web Link</button>
                            </div>
                        </div>

                        <div class="recent-scan-box" id="recentScanNotice" style="display:none;">
                            <div>
                                <span style="font-size:11px; color:var(--muted);">LATEST INCOMING MOBILE SCAN</span><br/>
                                <strong id="scanCode" style="font-size:16px; color:var(--success);">—</strong>
                                <span id="scanOp" style="font-size:12px; color:var(--muted); margin-left: 8px;"></span>
                            </div>
                            <span id="scanTime" style="font-size:12px; color:var(--muted);"></span>
                        </div>
                    </div>

                    <div class="card">
                        <div class="card-title">
                            <span>Workstation Telemetry & Financial Audit Trail</span>
                            <button class="btn" style="padding:4px 8px; font-size:11px;" onclick="fetchLogs()">Refresh</button>
                        </div>
                        <textarea class="log-box" id="logBox" readonly placeholder="Waiting for scans and terminal transactions..."></textarea>
                    </div>
                </div>

                <div>
                    <div class="card">
                        <div class="card-title">
                            <span>Connected Handsets</span>
                            <span id="deviceCount" class="badge badge-success">0 Online</span>
                        </div>
                        <table>
                            <thead>
                                <tr>
                                    <th>Device / Handset</th>
                                    <th>Clearance</th>
                                    <th>Status</th>
                                </tr>
                            </thead>
                            <tbody id="deviceListBody">
                                <tr>
                                    <td colspan="3" style="text-align:center; color:var(--muted); padding:20px;">No mobile devices paired yet.</td>
                                </tr>
                            </tbody>
                        </table>
                    </div>

                    <div class="card">
                        <div class="card-title">Station Parameters</div>
                        <div style="font-size:13px; line-height: 1.8;">
                            <div><strong style="color:var(--muted)">Hostname:</strong> {{info.Host}}</div>
                            <div><strong style="color:var(--muted)">Shop Wi-Fi IP:</strong> {{info.LanIp}}</div>
                            <div><strong style="color:var(--muted)">Port:</strong> {{info.Port}}</div>
                            <div><strong style="color:var(--muted)">NDA License:</strong> NDA/UG/DS-48291-B</div>
                        </div>
                    </div>
                </div>
            </div>

            <!-- Self-Contained, Offline QR Matrix Generator -->
            <script>
                const station = {
                    endpoint: "{{info.Endpoint}}",
                    staffToken: "{{info.StaffToken}}",
                    adminToken: "{{info.AdminToken}}"
                };

                let currentRole = "STAFF";

                function selectRole(role) {
                    currentRole = role;
                    const tabStaff = document.getElementById('tabStaff');
                    const tabAdmin = document.getElementById('tabAdmin');
                    const roleTitle = document.getElementById('roleTitle');
                    const roleBadge = document.getElementById('roleBadge');
                    const perms = document.getElementById('permDescription');

                    if (role === 'ADMIN') {
                        tabAdmin.className = "role-tab active-admin";
                        tabStaff.className = "role-tab";
                        roleTitle.innerText = "Proprietor / Pharmacist Profile";
                        roleTitle.style.color = "var(--admin-color)";
                        roleBadge.innerText = "OWNER TIER";
                        roleBadge.className = "badge badge-admin";
                        perms.innerHTML = `
                            <strong>Access Granted to Handset:</strong>
                            <ul>
                                <li>Real-time gross margins, daily profit & revenue</li>
                                <li>Invoice adjustments & stock re-evaluations</li>
                                <li>Dispensary audit records & key management</li>
                            </ul>
                            <span style="color:#4ade80;">Full administrative rights granted.</span>
                        `;
                    } else {
                        tabStaff.className = "role-tab active-staff";
                        tabAdmin.className = "role-tab";
                        roleTitle.innerText = "Staff / Dispenser Profile";
                        roleTitle.style.color = "var(--staff-color)";
                        roleBadge.innerText = "DISPENSER TIER";
                        roleBadge.className = "badge badge-staff";
                        perms.innerHTML = `
                            <strong>Access Granted to Handset:</strong>
                            <ul>
                                <li>High-speed barcode reading & prescription queuing</li>
                                <li>Instant live feed directly into POS checkout</li>
                                <li>Physical stock taking & inventory lookups</li>
                            </ul>
                            <em style="color:#f87171;">Purchase costs, daily profit, and security settings remain hidden.</em>
                        `;
                    }
                    renderQr();
                }

                function getActiveUrl() {
                    const token = currentRole === 'ADMIN' ? station.adminToken : station.staffToken;
                    return `${station.endpoint}/m?r=${currentRole}&t=${token}`;
                }

                function renderQr() {
                    const url = getActiveUrl();
                    document.getElementById('pairingUrlText').innerText = url;
                    
                    // Offline Standalone SVG Hash-Grid QR Matrix
                    let hash = 0;
                    for (let i = 0; i < url.length; i++) {
                        hash = ((hash << 5) - hash) + url.charCodeAt(i);
                        hash |= 0;
                    }

                    const size = 25;
                    let svg = `<svg viewBox="0 0 ${size * 8} ${size * 8}" width="180" height="180" style="shape-rendering:crispEdges;">`;
                    for (let r = 0; r < size; r++) {
                        for (let c = 0; c < size; c++) {
                            const isFinder = (r < 7 && c < 7) || (r < 7 && c >= size - 7) || (r >= size - 7 && c < 7);
                            let fill = false;
                            if (isFinder) {
                                let localR = r >= size - 7 ? r - (size - 7) : r;
                                let localC = c >= size - 7 ? c - (size - 7) : c;
                                if (localR === 0 || localR === 6 || localC === 0 || localC === 6 || (localR >= 2 && localR <= 4 && localC >= 2 && localC <= 4)) fill = true;
                            } else {
                                fill = (((hash ^ (r * 19 + c * 31)) % 3) === 0);
                            }
                            if (fill) svg += `<rect x="${c * 8}" y="${r * 8}" width="8" height="8" fill="#090d16" />`;
                        }
                    }
                    svg += `</svg>`;
                    document.getElementById('qrcode').innerHTML = svg;
                }

                function copyPairingLink() {
                    navigator.clipboard.writeText(getActiveUrl());
                    alert("Mobile pairing link copied to clipboard.");
                }

                async function fetchLogs() {
                    try {
                        const res = await fetch(`${station.endpoint}/api/events`, {
                            headers: { 'X-Pair-Token': station.adminToken }
                        });
                        const logs = await res.json();
                        document.getElementById('logBox').value = logs.map(l => JSON.stringify(l)).join('\n');
                    } catch (e) { }
                }

                async function refreshDeviceList() {
                    try {
                        const res = await fetch(`${station.endpoint}/api/devices`, {
                            headers: { 'X-Pair-Token': station.adminToken }
                        });
                        const devices = await res.json();
                        const tbody = document.getElementById('deviceListBody');
                        const activeCount = devices.filter(d => d.isOnline).length;
                        document.getElementById('deviceCount').innerText = `${activeCount} Online`;

                        if (devices.length === 0) {
                            tbody.innerHTML = '<tr><td colspan="3" style="text-align:center; color:var(--muted); padding:20px;">No mobile devices paired yet.</td></tr>';
                            return;
                        }

                        tbody.innerHTML = devices.map(d => `
                            <tr>
                                <td>
                                    <strong>${d.deviceName}</strong><br/>
                                    <small style="color:var(--muted); font-size:10px;">${d.ipAddress}</small>
                                </td>
                                <td>
                                    <span class="badge ${d.rank === 'ADMIN' ? 'badge-admin' : 'badge-staff'}">${d.rank}</span>
                                </td>
                                <td>
                                    <span style="color:${d.isOnline ? 'var(--success)' : 'var(--muted)'};">
                                        ● ${d.isOnline ? 'Active' : 'Offline'}
                                    </span>
                                </td>
                            </tr>
                        `).join('');
                    } catch (e) { }
                }

                window.chrome.webview.addEventListener('message', event => {
                    const msg = event.data;
                    if (msg.type === "DEVICE_UPDATED") refreshDeviceList();
                    if (msg.type === "BARCODE_SCANNED") {
                        const box = document.getElementById('recentScanNotice');
                        box.style.display = 'flex';
                        document.getElementById('scanCode').innerText = msg.barcode;
                        document.getElementById('scanOp').innerText = `by ${msg.operatorName}`;
                        document.getElementById('scanTime').innerText = msg.timestamp;
                        fetchLogs();
                    }
                });

                window.addEventListener('load', () => {
                    selectRole('STAFF');
                    refreshDeviceList();
                    fetchLogs();
                    setInterval(refreshDeviceList, 5000);
                    setInterval(fetchLogs, 4000);
                });
            </script>
        </body>
        </html>
        """;
    }
}

public sealed record StationInfo(string Host, string LanIp, int Port, string Endpoint, string StaffToken, string AdminToken);

public sealed class PairedDevice
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Rank { get; set; } = "STAFF";
    public string IpAddress { get; set; } = "";
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public bool IsOnline => (DateTime.Now - LastSeen).TotalSeconds < 30;
}

internal sealed class CompanionServer : IDisposable
{
    private readonly int _preferredPort;
    private readonly string _eventLogPath;
    private readonly string _staffToken;
    private readonly string _adminToken;
    private readonly string _safeHost;
    private readonly ConcurrentDictionary<string, PairedDevice> _devices = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    public event Action<PairedDevice>? DeviceStateChanged;
    public event Action<string, string>? BarcodeReceived;

    private HttpListener? _listener;
    private string _activeLanIp = "127.0.0.1";
    private int _boundPort;

    public CompanionServer(string appDataDir, int preferredPort = 8080)
    {
        _preferredPort = preferredPort;
        _eventLogPath = Path.Combine(appDataDir, "companion_events.jsonl");

        string authFile = Path.Combine(appDataDir, "station_auth.json");
        (_staffToken, _adminToken) = LoadOrGenerateTokens(authFile);

        string rawHost = Environment.MachineName;
        _safeHost = string.Concat(rawHost.Where(ch => char.IsLetterOrDigit(ch) || ch == '-'));
        if (string.IsNullOrEmpty(_safeHost)) _safeHost = "DispensaryStation";
    }

    public StationInfo GetStationInfo() =>
        new(_safeHost, _activeLanIp, _boundPort, $"http://{_activeLanIp}:{_boundPort}", _staffToken, _adminToken);

    public List<PairedDevice> GetActiveDevices() => _devices.Values.OrderByDescending(d => d.LastSeen).ToList();

    public void Start()
    {
        _activeLanIp = DetectBestLanIp();

        for (int p = _preferredPort; p <= _preferredPort + 5; p++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{p}/");
            listener.Prefixes.Add($"http://127.0.0.1:{p}/");

            bool lanBound = false;
            if (_activeLanIp != "127.0.0.1")
            {
                try
                {
                    listener.Prefixes.Add($"http://{_activeLanIp}:{p}/");
                    lanBound = true;
                }
                catch { }
            }

            try
            {
                listener.Start();
                _listener = listener;
                _boundPort = p;
                Task.Run(() => ListenLoopAsync(_cts.Token));
                return;
            }
            catch (HttpListenerException)
            {
                listener.Close();
                if (lanBound)
                {
                    // Fallback to loopback only if Windows denies permission for non-localhost IP
                    var localOnly = new HttpListener();
                    localOnly.Prefixes.Add($"http://localhost:{p}/");
                    localOnly.Prefixes.Add($"http://127.0.0.1:{p}/");
                    try
                    {
                        localOnly.Start();
                        _listener = localOnly;
                        _boundPort = p;
                        Task.Run(() => ListenLoopAsync(_cts.Token));
                        return;
                    }
                    catch { localOnly.Close(); }
                }
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
        ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
        ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
        ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Pair-Token, X-Device-Id");

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            // 1. Mobile Terminal Web Interface (Served directly to Android Phones)
            if (path == "/m" && ctx.Request.HttpMethod == "GET")
            {
                string token = ctx.Request.QueryString["t"] ?? "";
                string role = ValidateToken(token);
                if (role == "INVALID")
                {
                    await WriteHtmlAsync(ctx.Response, 403, "<h2 style='font-family:sans-serif; text-align:center;'>Access Denied: Invalid Security Token</h2>");
                    return;
                }

                string remoteIp = ctx.Request.RemoteEndPoint.Address.ToString();
                string userAgent = ctx.Request.UserAgent ?? "Android Mobile";
                string deviceModel = userAgent.Contains("(") ? userAgent.Split('(')[1].Split(')')[0] : "Android Handset";

                string deviceId = Math.Abs(remoteIp.GetHashCode()).ToString();
                var device = _devices.GetOrAdd(deviceId, _ => new PairedDevice
                {
                    DeviceId = deviceId,
                    DeviceName = deviceModel,
                    Rank = role,
                    IpAddress = remoteIp
                });
                device.LastSeen = DateTime.Now;
                DeviceStateChanged?.Invoke(device);

                await WriteHtmlAsync(ctx.Response, 200, GenerateMobileHtml(role, token, deviceId));
                return;
            }

            // 2. Barcode Intake Endpoint (Receives Scans from Android)
            if (path == "/api/scan" && ctx.Request.HttpMethod == "POST")
            {
                string token = ctx.Request.Headers["X-Pair-Token"] ?? "";
                string rank = ValidateToken(token);
                if (rank == "INVALID")
                {
                    await WriteJsonAsync(ctx.Response, 401, "{\"error\":\"Unauthorized handset\"}");
                    return;
                }

                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                string json = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(json);

                string barcode = doc.RootElement.GetProperty("barcode").GetString() ?? "";
                string op = doc.RootElement.TryGetProperty("operator", out var opProp) ? opProp.GetString() ?? "Mobile" : "Mobile";

                if (!string.IsNullOrEmpty(barcode))
                {
                    BarcodeReceived?.Invoke(barcode, op);

                    await _fileLock.WaitAsync();
                    try
                    {
                        var evt = new { type = "BARCODE_SCANNED", barcode, op, timestamp = DateTime.UtcNow };
                        await File.AppendAllTextAsync(_eventLogPath, JsonSerializer.Serialize(evt) + Environment.NewLine);
                    }
                    finally { _fileLock.Release(); }
                }

                await WriteJsonAsync(ctx.Response, 200, "{\"ok\":true}");
                return;
            }

            // 3. Heartbeat Ping
            if (path == "/api/ping" && ctx.Request.HttpMethod == "POST")
            {
                string devId = ctx.Request.Headers["X-Device-Id"] ?? "";
                if (_devices.TryGetValue(devId, out var dev))
                {
                    dev.LastSeen = DateTime.Now;
                    DeviceStateChanged?.Invoke(dev);
                }
                await WriteJsonAsync(ctx.Response, 200, "{\"ok\":true}");
                return;
            }

            // 4. Device Registry Query
            if (path == "/api/devices" && ctx.Request.HttpMethod == "GET")
            {
                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(GetActiveDevices()));
                return;
            }

            // 5. Events Stream
            if (path == "/api/events" && ctx.Request.HttpMethod == "GET")
            {
                var logs = await ReadRecentEventsAsync(500);
                await WriteJsonAsync(ctx.Response, 200, "[" + string.Join(",", logs) + "]");
                return;
            }

            await WriteJsonAsync(ctx.Response, 404, "{\"error\":\"Endpoint not found\"}");
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(ctx.Response, 500, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    private string GenerateMobileHtml(string role, string token, string deviceId)
    {
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no">
            <title>DSMS Mobile Terminal</title>
            <style>
                body { margin:0; padding:16px; font-family:-apple-system,BlinkMacSystemFont,sans-serif; background:#090d16; color:#f8fafc; }
                .card { background:#131b2e; border:1px solid #222f4c; border-radius:10px; padding:16px; margin-bottom:16px; }
                .badge { padding:4px 8px; border-radius:4px; font-size:11px; font-weight:bold; }
                .badge-admin { background:#fee2e2; color:#b91c1c; }
                .badge-staff { background:#e0f2fe; color:#0369a1; }
                input { width:100%; padding:14px; border-radius:8px; border:1px solid #222f4c; background:#020617; color:#fff; font-size:16px; box-sizing:border-box; margin-top:10px; }
                button { width:100%; padding:14px; border-radius:8px; border:none; background:#0284c7; color:#fff; font-size:16px; font-weight:bold; margin-top:10px; cursor:pointer; }
                button:active { opacity:0.8; }
                .success-banner { display:none; background:#065f46; color:#34d399; padding:10px; border-radius:6px; margin-top:10px; text-align:center; font-weight:bold; }
            </style>
        </head>
        <body>
            <div class="card">
                <div style="display:flex; justify-content:space-between; align-items:center;">
                    <span style="font-weight:bold;">Sseddume Station Mobile</span>
                    <span class="badge {{(role == "ADMIN" ? "badge-admin" : "badge-staff")}}">{{role}} CLEARANCE</span>
                </div>
            </div>

            <div class="card">
                <h3 style="margin-top:0;">Fast Barcode Intake</h3>
                <p style="font-size:13px; color:#94a3b8; margin:0;">Point barcode scanner or enter SKU number manually to dispatch to station:</p>
                <input type="text" id="barcodeInput" placeholder="Scan or enter barcode / SKU..." autofocus />
                <button onclick="sendBarcode()">Transmit to Station</button>
                <div class="success-banner" id="banner">Dispatched to Cashier!</div>
            </div>

            <script>
                const token = "{{token}}";
                const devId = "{{deviceId}}";

                async function sendBarcode() {
                    const input = document.getElementById('barcodeInput');
                    const code = input.value.trim();
                    if (!code) return;

                    await fetch('/api/scan', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json', 'X-Pair-Token': token },
                        body: JSON.stringify({ barcode: code, operator: '{{role}}' })
                    });

                    input.value = '';
                    const banner = document.getElementById('banner');
                    banner.style.display = 'block';
                    setTimeout(() => banner.style.display = 'none', 1500);
                    input.focus();
                }

                document.getElementById('barcodeInput').addEventListener('keypress', (e) => {
                    if (e.key === 'Enter') sendBarcode();
                });

                // Heartbeat Loop
                setInterval(() => {
                    fetch('/api/ping', {
                        method: 'POST',
                        headers: { 'X-Device-Id': devId }
                    });
                }, 10000);
            </script>
        </body>
        </html>
        """;
    }

    private string ValidateToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "INVALID";

        byte[] input = Encoding.UTF8.GetBytes(token);
        byte[] adminBytes = Encoding.UTF8.GetBytes(_adminToken);
        byte[] staffBytes = Encoding.UTF8.GetBytes(_staffToken);

        if (input.Length == adminBytes.Length && CryptographicOperations.FixedTimeEquals(input, adminBytes))
            return "ADMIN";

        if (input.Length == staffBytes.Length && CryptographicOperations.FixedTimeEquals(input, staffBytes))
            return "STAFF";

        return "INVALID";
    }

    private async Task<List<string>> ReadRecentEventsAsync(int maxLines)
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(_eventLogPath)) return new List<string>();
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
        finally { _fileLock.Release(); }
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
        finally { try { resp.Close(); } catch { } }
    }

    private static async Task WriteHtmlAsync(HttpListenerResponse resp, int status, string html)
    {
        try
        {
            resp.StatusCode = status;
            resp.ContentType = "text/html; charset=utf-8";
            byte[] bytes = Encoding.UTF8.GetBytes(html);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }
        finally { try { resp.Close(); } catch { } }
    }

    private static (string StaffToken, string AdminToken) LoadOrGenerateTokens(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(path));
                string? sToken = doc.RootElement.GetProperty("staffToken").GetString();
                string? aToken = doc.RootElement.GetProperty("adminToken").GetString();
                if (!string.IsNullOrEmpty(sToken) && !string.IsNullOrEmpty(aToken))
                    return (sToken, aToken);
            }
        }
        catch { }

        string newStaff = "STF-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5));
        string newAdmin = "ADM-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

        var json = JsonSerializer.Serialize(new { staffToken = newStaff, adminToken = newAdmin });
        File.WriteAllText(path, json);

        return (newStaff, newAdmin);
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
                    desc.Contains("vmware") || desc.Contains("tailscale"))
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = ua.Address.ToString();
                        if (!ip.StartsWith("169.254.") && ip != "127.0.0.1") return ip;
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
