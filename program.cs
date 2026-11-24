using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
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

    public MainForm()
    {
        Text = "SSEDDUME DRUG SHOP MANAGEMENT SYSTEM — Windows Desktop";
        Width = 1366;
        Height = 840;
        MinimumSize = new Size(1024, 680);
        StartPosition = FormStartPosition.CenterScreen;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(_webView);

        Load += async (_, _) => await InitWebViewAsync();
    }

    private async System.Threading.Tasks.Task InitWebViewAsync()
    {
        try
        {
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SseddumeDrugShop"
            );
            string userDataFolder = Path.Combine(appData, "WebView2Data");
            string webRoot = Path.Combine(appData, "www");
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
                $"Failed to initialize Windows desktop container:\n{ex.Message}\n\nEnsure Microsoft Edge WebView2 Runtime is installed.",
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
