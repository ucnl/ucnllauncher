using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Hoho.Android.UsbSerial.Driver;

namespace UCNLLauncher.Services;

public class UsbService
{
    private readonly UsbManager _usbManager;
    private IUsbSerialPort? _port;
    private UsbDeviceConnection? _connection;
    private bool _isOpen;

    public UsbService()
    {
        var context = Android.App.Application.Context;
        _usbManager = context.GetSystemService(Context.UsbService) as UsbManager
            ?? throw new Exception("USB Service not available");
    }

    public bool IsDeviceConnected => _isOpen && _port != null && _connection != null;

    public async Task<bool> TryConnectAsync()
    {
        try
        {
            Close();

            foreach (var device in _usbManager.DeviceList!.Values!)
            {
                var prober = UsbSerialProber.DefaultProber;
                var driver = prober.ProbeDevice(device);

                if (driver != null)
                {
                    if (!_usbManager.HasPermission(device))
                    {
                        var granted = await RequestPermissionAsync(device);
                        if (!granted) continue;
                    }

                    _connection = _usbManager.OpenDevice(device);
                    if (_connection == null) continue;

                    var portsProperty = driver.GetType().GetProperty("Ports");
                    var ports = portsProperty?.GetValue(driver) as System.Collections.IList;
                    if (ports == null || ports.Count == 0) continue;

                    _port = ports[0] as IUsbSerialPort;
                    if (_port == null) continue;

                    _port.Open(_connection);
                    _port.SetParameters(9600, 8, StopBits.One, Parity.None);
                    _isOpen = true;

                    System.Diagnostics.Debug.WriteLine($"Connected: {device.VendorId:X}:{device.ProductId:X}");
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"USB error: {ex.Message}");
            return false;
        }
    }

    private Task<bool> RequestPermissionAsync(UsbDevice device)
    {
        var tcs = new TaskCompletionSource<bool>();
        var receiver = new UsbPermissionReceiver(granted => tcs.SetResult(granted));
        var intent = PendingIntent.GetBroadcast(
            Android.App.Application.Context, 0,
            new Intent("com.ucnl.USB_PERMISSION"),
            PendingIntentFlags.UpdateCurrent
        );
        _usbManager.RequestPermission(device, intent);
        return Task.WhenAny(tcs.Task, Task.Delay(5000)).ContinueWith(t =>
            t.Result is Task<bool> bt && bt.Result);
    }

    public async Task<string?> ReadAsync(int timeoutMs = 1000)
    {
        if (!_isOpen || _port == null || _connection == null)
            return null;

        try
        {
            var buffer = new byte[1024];
            var result = await Task.Run(() => _port.Read(buffer, timeoutMs));
            if (result > 0)
                return System.Text.Encoding.ASCII.GetString(buffer, 0, result);
        }
        catch (Java.IO.IOException)
        {
            // Устройство отключено — нормально
            _isOpen = false;
        }
        catch (ObjectDisposedException)
        {
            _isOpen = false;
        }
        catch (NullReferenceException)
        {
            _isOpen = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Read error: {ex.Message}");
        }
        return null;
    }

    public async Task<bool> WriteAsync(string data)
    {
        if (!_isOpen || _port == null || _connection == null)
            return false;

        try
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(data);
            await Task.Run(() => _port.Write(bytes, 1000));
            return true;
        }
        catch (Java.IO.IOException)
        {
            _isOpen = false;
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Write error: {ex.Message}");
            return false;
        }
    }

    public void Close()
    {
        try
        {
            _port?.Close();
            try { (_port as IDisposable)?.Dispose(); } catch { }
        }
        catch { }

        try { _connection?.Close(); } catch { }

        _port = null;
        _connection = null;
        _isOpen = false;
    }
}

public class UsbPermissionReceiver : Android.Content.BroadcastReceiver
{
    private readonly Action<bool> _callback;
    public UsbPermissionReceiver(Action<bool> callback) => _callback = callback;

    public override void OnReceive(Context? context, Intent? intent)
    {
        var granted = intent?.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false) ?? false;
        _callback(granted);
    }
}