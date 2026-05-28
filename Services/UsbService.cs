using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Hoho.Android.UsbSerial.Driver;

namespace UCNLLauncher.Services;

public class UsbPortInfo
{
    public IUsbSerialPort Port { get; set; } = null!;
    public UsbDeviceConnection Connection { get; set; } = null!;
    public UsbDevice Device { get; set; } = null!;
    public bool IsOpen { get; set; }
}

public class UsbService
{
    private readonly UsbManager _usbManager;
    private readonly Dictionary<int, UsbPortInfo> _ports = new();

    public UsbService()
    {
        var context = Android.App.Application.Context;
        _usbManager = context.GetSystemService(Context.UsbService) as UsbManager
            ?? throw new Exception("USB Service not available");
    }

    public bool IsPortOpen(int portId) =>
        _ports.TryGetValue(portId, out var p) && p.IsOpen && p.Port != null && p.Connection != null;

    public bool IsAnyPortOpen => _ports.Values.Any(p => p.IsOpen);

    public async Task<bool> TryConnectAsync(int portId = 0, int baudRate = 9600)
    {
        try
        {
            ClosePort(portId);

            foreach (var device in _usbManager.DeviceList!.Values!)
            {
                if (_ports.Values.Any(p => p.IsOpen && p.Device.DeviceId == device.DeviceId))
                    continue;

                var prober = UsbSerialProber.DefaultProber;
                var driver = prober.ProbeDevice(device);

                if (driver == null) continue;

                if (!_usbManager.HasPermission(device))
                {
                    var granted = await RequestPermissionAsync(device);
                    if (!granted) continue;
                }

                var connection = _usbManager.OpenDevice(device);
                if (connection == null) continue;

                var portsProperty = driver.GetType().GetProperty("Ports");
                var ports = portsProperty?.GetValue(driver) as System.Collections.IList;
                if (ports == null || ports.Count == 0) continue;

                var port = ports[0] as IUsbSerialPort;
                if (port == null) continue;

                port.Open(connection);
                port.SetParameters(baudRate, 8, StopBits.One, Parity.None);

                _ports[portId] = new UsbPortInfo
                {
                    Port = port,
                    Connection = connection,
                    Device = device,
                    IsOpen = true
                };

                System.Diagnostics.Debug.WriteLine($"Connected port {portId}: VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4} @ {baudRate}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"USB error (port {portId}): {ex.Message}");
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

    public async Task<string?> ReadAsync(int portId, int timeoutMs = 1000)
    {
        if (!_ports.TryGetValue(portId, out var p) || !p.IsOpen || p.Port == null || p.Connection == null)
            return null;

        try
        {
            var buffer = new byte[1024];
            var result = await Task.Run(() => p.Port.Read(buffer, timeoutMs));
            if (result > 0)
                return System.Text.Encoding.ASCII.GetString(buffer, 0, result);
        }
        catch (Java.IO.IOException)
        {
            p.IsOpen = false;
        }
        catch (ObjectDisposedException)
        {
            p.IsOpen = false;
        }
        catch (NullReferenceException)
        {
            p.IsOpen = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Read error (port {portId}): {ex.Message}");
        }
        return null;
    }

    public async Task<bool> WriteAsync(int portId, string data)
    {
        if (!_ports.TryGetValue(portId, out var p) || !p.IsOpen || p.Port == null || p.Connection == null)
            return false;

        try
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(data);
            await Task.Run(() => p.Port.Write(bytes, 1000));
            return true;
        }
        catch (Java.IO.IOException)
        {
            p.IsOpen = false;
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Write error (port {portId}): {ex.Message}");
            return false;
        }
    }

    public void ClosePort(int portId)
    {
        if (!_ports.TryGetValue(portId, out var p)) return;

        try
        {
            p.Port?.Close();
            try { (p.Port as IDisposable)?.Dispose(); } catch { }
        }
        catch { }

        try { p.Connection?.Close(); } catch { }

        _ports.Remove(portId);
    }

    public void CloseAll()
    {
        foreach (var portId in _ports.Keys.ToList())
            ClosePort(portId);
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