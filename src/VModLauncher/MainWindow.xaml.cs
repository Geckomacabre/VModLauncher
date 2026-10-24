using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using VModLauncher.Host;

namespace VModLauncher;

public partial class MainWindow : Window
{
    Bridge? bridge;

    public MainWindow()
    {
        InitializeComponent();
        Title = App.ProductName;
        SourceInitialized += (_, _) => DarkTitleBar();
        Loaded += async (_, _) => await StartAsync();
        Closing += (_, e) =>
        {
            // closing mid-install would leave the game half-modded: finish first
            if (bridge?.IsBusy == true)
            {
                e.Cancel = true;
                MessageBox.Show(this, "The launcher is changing your game files right now. Wait for it to finish, then close it.",
                    App.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            bridge?.Dispose();
        };
    }

    async Task StartAsync()
    {
        try
        {
            var env = await Web.GetEnvironmentAsync();
            await Ui.EnsureCoreWebView2Async(env);
            var core = Ui.CoreWebView2;

            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.vml", root, CoreWebView2HostResourceAccessKind.DenyCors);
            // artwork extracted from the player's own game (ArtService)
            core.SetVirtualHostNameToFolderMapping("art.vml", App.Art.Folder, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = System.Diagnostics.Debugger.IsAttached || Environment.GetEnvironmentVariable("VML_DEVTOOLS") == "1";
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = core.Settings.AreDevToolsEnabled;
            core.Settings.IsZoomControlEnabled = false;

            // the launcher page never navigates away: links go to the player's own browser
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("https://app.vml/", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Web.OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Web.OpenExternal(e.Uri);
            };

            bridge = new Bridge(this, core);
            core.Navigate("https://app.vml/index.html");
            Loading.Visibility = Visibility.Collapsed;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Loading.Text = "This launcher needs the Microsoft Edge WebView2 Runtime.\nGet it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and start the launcher again.";
        }
        catch (Exception ex)
        {
            App.Log("start", ex.ToString());
            Loading.Text = "The launcher couldn't start: " + ex.Message;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void DarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
        int caption = 0x00000000;                                      // black title bar to match the pause-menu look
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR (Windows 11)
    }
}
