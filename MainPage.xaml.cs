using UCNLLauncher.Services;
using System.Net.Http;

namespace UCNLLauncher;

public partial class MainPage : ContentPage
{
    internal readonly UsbService _usbService;
    private bool _isLauncherLoaded;
    private bool _isAppLoaded;
    private CancellationTokenSource? _readLoopCts;
    private readonly HttpClient _httpClient;
    private string _currentAppName = "";

    private readonly Dictionary<string, string> _appUrls = new()
    {
        { "uWaver", "https://docs.unavlab.com/uWaver/" },
        { "RedPhoneDXConfig", "https://docs.unavlab.com/RedPhoneDXConfig-Web/" },
        { "uConsole", "https://docs.unavlab.com/uConsole/" },
        { "AzimuthWebSuite", "https://docs.unavlab.com/AzimuthWebSuite/" }
    };

    public MainPage()
    {
        InitializeComponent();
        _usbService = new UsbService();
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        MainWebView.Navigating += OnNavigating;
        MainWebView.Navigated += OnNavigated;       

        LoadLauncher();

        ConfigureWebViewCache();
    }

    private void ConfigureWebViewCache()
    {
#if ANDROID
        MainWebView.HandlerChanged += (s, e) =>
        {
            if (MainWebView.Handler?.PlatformView is Android.Webkit.WebView androidWebView)
            {
                androidWebView.Settings.CacheMode = Android.Webkit.CacheModes.CacheElseNetwork;
            }
        };
#endif
    }

    private void LoadLauncher()
    {
        _isLauncherLoaded = false;
        _isAppLoaded = false;
        _currentAppName = "";
        Toolbar.IsVisible = false;
        MainWebView.Source = "launcher.html";
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        _readLoopCts?.Cancel();
        LoadLauncher();
    }

    private void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url.StartsWith("app://"))
        {
            e.Cancel = true;
            var appName = e.Url.Replace("app://", "");
            if (appName == "clear_cache")
                ClearAllCache();
            else
                LaunchApp(appName);
        }
        else if (e.Url.StartsWith("uart://write?"))
        {
            e.Cancel = true;
            var raw = Uri.UnescapeDataString(e.Url.Replace("uart://write?", ""));
            var parts = raw.Split('|', 2);
            int portId = parts.Length > 1 && int.TryParse(parts[0], out var id) ? id : 0;
            var data = parts.Length > 1 ? parts[1] : raw;
            _ = Task.Run(() => _usbService.WriteAsync(portId, data));
        }
    }

    private async void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        LoadingIndicator.IsVisible = false;

        if (!_isLauncherLoaded)
        {
            _isLauncherLoaded = true;
            Toolbar.IsVisible = false;
            await Task.Delay(500);
            await InjectLauncherScript();
        }
        else if (!_isAppLoaded && e.Url.Contains("native=1"))
        {
            _isAppLoaded = true;
            Toolbar.IsVisible = true;
            await InjectDeviceAdapter();

            foreach (var kvp in _appUrls)
            {
                if (e.Url.Contains(kvp.Value))
                {
                    Preferences.Set(kvp.Key + "_cache", e.Url);
                    break;
                }
            }
        }
    }

    private async void ClearAllCache()
    {
        foreach (var key in _appUrls.Keys)
            Preferences.Remove(key + "_cache");

#if ANDROID
        if (MainWebView.Handler?.PlatformView is Android.Webkit.WebView androidWebView)
        {
            androidWebView.ClearCache(true);
        }
#endif

        await DisplayAlert("Кэш", "Кэш всех приложений очищен.", "OK");
    }

    private async Task InjectLauncherScript()
    {
        string script = @"
        window.launchApp = function(appName) { window.location.href = 'app://' + appName; };
        window.updateUsbStatus = function(connected) {
            var dot = document.getElementById('statusDot');
            var text = document.getElementById('statusText');
            if (dot && text) {
                dot.className = connected ? 'status-dot connected' : 'status-dot';
                text.textContent = connected ? 'USB подключено' : 'USB не найдено';
            }
        };
    ";
        await MainWebView.EvaluateJavaScriptAsync(script);
        bool hasDevice = await _usbService.TryConnectAsync(0, 9600);
        await MainWebView.EvaluateJavaScriptAsync($"updateUsbStatus({hasDevice.ToString().ToLower()})");
        StartUsbWatcher();
    }

    private void StartUsbWatcher()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await Task.Delay(3000);

                    bool wasConnected = _usbService.IsAnyPortOpen;
                    bool nowConnected = _usbService.IsAnyPortOpen;

                    if (!nowConnected && wasConnected)
                    {
                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            await MainWebView.EvaluateJavaScriptAsync("updateUsbStatus(false)");
                        });
                    }
                    else if (!nowConnected)
                    {
                        bool connected = await _usbService.TryConnectAsync(0, 9600);
                        MainThread.BeginInvokeOnMainThread(async () =>
                        {
                            await MainWebView.EvaluateJavaScriptAsync($"updateUsbStatus({connected.ToString().ToLower()})");
                        });
                    }
                }
                catch { break; }
            }
        });
    }

    private async Task InjectDeviceAdapter()
    {
        try
        {
            bool isAzimuthSuite = _currentAppName == "AzimuthWebSuite";

            string serialApi = isAzimuthSuite ? @"
            navigator.serial = {
                _port: null,
                _portId: 'azm',
                _getPort: function(id) {
                    if (!this._port || this._portId !== id) {
                        this._portId = id;
                        this._port = {
                            id: id,
                            readable: new ReadableStream({ start: c => window._stubController = c }),
                            writable: new WritableStream({ write: chunk => {
                                var t = typeof chunk === 'string' ? chunk : new TextDecoder().decode(chunk);
                                var iframe = document.createElement('iframe');
                                iframe.style.display = 'none';
                                iframe.src = 'uart://write?' + encodeURIComponent(id === 'gnss' ? '1|' + t : '0|' + t);
                                document.body.appendChild(iframe);
                                setTimeout(function() { document.body.removeChild(iframe); }, 100);
                            }}),
                            open: () => Promise.resolve(),
                            close: () => Promise.resolve()
                        };
                    }
                    return this._port;
                },
                requestPort: function() { return Promise.resolve(navigator.serial._getPort('azm')); },
                getPorts: function() { return Promise.resolve([navigator.serial._getPort('azm')]); }
            };
            navigator.serial2 = {
                requestPort: function() { return Promise.resolve(navigator.serial._getPort('gnss')); },
                getPorts: function() { return Promise.resolve([navigator.serial._getPort('gnss')]); }
            };
            window._uartDataReceived = function(d) {};
        " : @"
            navigator.serial = {
                _port: null,
                _getPort: function() {
                    if (!this._port) {
                        this._port = {
                            readable: new ReadableStream({ start: c => window._stubController = c }),
                            writable: new WritableStream({ write: chunk => {
                                var t = typeof chunk === 'string' ? chunk : new TextDecoder().decode(chunk);
                                var iframe = document.createElement('iframe');
                                iframe.style.display = 'none';
                                iframe.src = 'uart://write?' + encodeURIComponent(t);
                                document.body.appendChild(iframe);
                                setTimeout(function() { document.body.removeChild(iframe); }, 100);
                            }}),
                            open: () => Promise.resolve(),
                            close: () => Promise.resolve()
                        };
                    }
                    return this._port;
                },
                requestPort: function() { return Promise.resolve(navigator.serial._getPort()); },
                getPorts: function() { return Promise.resolve([navigator.serial._getPort()]); }
            };
            window._uartDataReceived = function(d) {};
        ";

            await MainWebView.EvaluateJavaScriptAsync(serialApi);

            using var stream = await FileSystem.OpenAppPackageFileAsync("device-adapter.js");
            using var reader = new StreamReader(stream);
            await MainWebView.EvaluateJavaScriptAsync(await reader.ReadToEndAsync());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Adapter] {ex.Message}");
            return;
        }
        StartUsbPolling();
    }

    private void StartUsbPolling()
    {
        _readLoopCts?.Cancel();
        _readLoopCts = new CancellationTokenSource();
        var token = _readLoopCts.Token;

        Task.Run(() => PollPort(token, 0), token);

        if (_currentAppName == "AzimuthWebSuite")
            Task.Run(() => PollPort(token, 1), token);
    }

    private async Task PollPort(CancellationToken token, int portId)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_usbService.IsPortOpen(portId))
                {
                    await Task.Delay(500, token);
                    continue;
                }

                string? data = null;
                try
                {
                    data = await _usbService.ReadAsync(portId, 100);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[USB] Read error (port {portId}): {ex.Message}");
                    await Task.Delay(500, token);
                    continue;
                }

                if (!string.IsNullOrEmpty(data))
                {
                    string escaped = data.Replace("\\", "\\\\").Replace("'", "\\'")
                        .Replace("\n", "\\n").Replace("\r", "");

                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        try
                        {
                            if (MainWebView.IsLoaded && MainWebView.Handler?.PlatformView != null)
                            {
                                MainWebView.EvaluateJavaScriptAsync(
                                    $"if(window._stubController) window._stubController.enqueue(new TextEncoder().encode('{escaped}'))");
                            }
                        }
                        catch { }
                    });
                }
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[USB] Poll error (port {portId}): {ex.Message}");
                await Task.Delay(1000, token);
            }
        }
    }

    private async void LaunchApp(string appName)
    {
        if (!_appUrls.ContainsKey(appName))
        {
            await DisplayAlert("Ошибка", "Приложение не найдено", "OK");
            return;
        }

        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }
        }
        catch { /* игнорируем если не вышло */ }

        _currentAppName = appName;
        LoadingIndicator.IsVisible = true;

        if (appName == "AzimuthWebSuite")
        {
            if (!_usbService.IsAnyPortOpen)
            {
                if (!await _usbService.TryConnectAsync(0, 9600))
                {
                    LoadingIndicator.IsVisible = false;
                    await DisplayAlert("USB", "Устройство AZM не найдено", "OK");
                    return;
                }
            }
            _ = _usbService.TryConnectAsync(1, 38400);
        }
        else
        {
            if (!_usbService.IsAnyPortOpen)
            {
                if (!await _usbService.TryConnectAsync(0, 9600))
                {
                    LoadingIndicator.IsVisible = false;
                    await DisplayAlert("USB", "Устройство не найдено", "OK");
                    return;
                }
            }
        }

        string appUrl = _appUrls[appName];
        string fullUrl = appUrl.Contains("?")
            ? $"{appUrl}&native=1"
            : $"{appUrl}?native=1";

        _isAppLoaded = false;

        try
        {
            var response = await _httpClient.GetAsync(appUrl);
            if (response.IsSuccessStatusCode)
            {
                MainWebView.Source = fullUrl;
                return;
            }
        }
        catch { }

        string? cachedUrl = Preferences.Get(appName + "_cache", null);
        if (cachedUrl != null)
        {
            MainWebView.Source = cachedUrl;
        }
        else
        {
            LoadingIndicator.IsVisible = false;
            await DisplayAlert("Нет сети", "Приложение недоступно офлайн", "OK");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _readLoopCts?.Cancel();
        _usbService.CloseAll();
    }
}