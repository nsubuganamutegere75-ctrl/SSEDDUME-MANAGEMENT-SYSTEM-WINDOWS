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
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SseddumeDrugShop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
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
    private readonly string _pairToken;
    private readonly int _port = 8080;
    private readonly object _fileLock = new();

    public MainForm()
    {
        Text = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM — Windows Companion Station";
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
        _pairToken = LoadOrCreatePairToken();

        _webView = new WebView2 { Dock = DockStyle.Fill };
        Controls.Add(_webView);

        Load += async (_, _) =>
        {
            StartLocalCompanionServer();
            await InitWebViewAsync();
        };
        FormClosing += (_, _) =>
        {
            try { _listener?.Stop(); } catch { }
        };
    }

    private string LoadOrCreatePairToken()
    {
        string tokenFile = Path.Combine(_appDataDir, "pair_token.txt");
        if (File.Exists(tokenFile))
        {
            string existing = File.ReadAllText(tokenFile).Trim();
            if (!string.IsNullOrEmpty(existing)) return existing;
        }
        string created = "PAIR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        File.WriteAllText(tokenFile, created);
        return created;
    }

    private static string GetLocalLanIp()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    string ip = ua.Address.ToString();
                    if (!ip.StartsWith("169.254.")) return ip;
                }
            }
        }
        return "127.0.0.1";
    }

    private void StartLocalCompanionServer()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
            _listener.Start();
            _ = Task.Run(ListenLoopAsync);
        }
        catch
        {
            // Fallback to localhost if Windows URLACL blocks wildcard port binding
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
                _ = Task.Run(ListenLoopAsync);
            }
            catch { }
        }
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
        string ip = GetLocalLanIp();
        string endpoint = $"http://{ip}:{_port}";
        string qrPayload = $"SSEDDUME-WIN|{Environment.MachineName}|{endpoint}|{_pairToken}";

        try
        {
            if (path == "/api/pair-info" && ctx.Request.HttpMethod == "GET")
            {
                var info = new
                {
                    ok = true,
                    host = Environment.MachineName,
                    endpoint,
                    token = _pairToken,
                    qrPayload
                };
                await WriteJsonAsync(ctx.Response, 200, JsonSerializer.Serialize(info));
                return;
            }

            if (path == "/api/events" && ctx.Request.HttpMethod == "POST")
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

            if (path == "/api/events" && ctx.Request.HttpMethod == "GET")
            {
                string[] lines;
                lock (_fileLock)
                {
                    lines = File.Exists(_eventLogPath)
                        ? File.ReadAllLines(_eventLogPath, Encoding.UTF8)
                        : Array.Empty<string>();
                }
                string jsonArray = "[" + string.Join(",", lines.Where(l => !string.IsNullOrWhiteSpace(l))) + "]";
                await WriteJsonAsync(ctx.Response, 200, jsonArray);
                return;
            }

            await WriteJsonAsync(ctx.Response, 404, "{\"error\":\"Not found\"}");
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
                $"Failed to initialize Windows desktop container:\n{ex.Message}",
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
