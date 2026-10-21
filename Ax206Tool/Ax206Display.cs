using System.Runtime.InteropServices;

namespace Ax206Tool;

/// <summary>
/// An AX206-based USB display (VID 1908, PID 0102, dpf-ax firmware) driven through the libusb-win32 driver.
/// Protocol as implemented by the open-source dpf-ax / lcd4linux driver: every command is a 31-byte USB
/// mass-storage "command block wrapper" carrying a vendor SCSI command 0xCD, optionally followed by a data
/// transfer, and always answered by a 13-byte "USBS" status block.
/// </summary>
public sealed class Ax206Display : IDisposable
{
    public const ushort VendorId = 0x1908;
    public const ushort ProductId = 0x0102;

    private const int EndpointOut = 0x01;
    private const int EndpointIn = 0x81;

    private readonly IntPtr _handle;

    public int Width { get; }
    public int Height { get; }

    private Ax206Display(IntPtr handle)
    {
        _handle = handle;

        // Sub-command 2: read LCD parameters. The reply starts with width and height (16-bit little-endian).
        var reply = new byte[5];
        Command(Scsi(sub: 2), receive: reply);
        Width = reply[0] | reply[1] << 8;
        Height = reply[2] | reply[3] << 8;
    }

    public static Ax206Display Open()
    {
        LibUsb0.usb_init();
        LibUsb0.usb_find_busses();
        LibUsb0.usb_find_devices();

        IntPtr device = FindDevice() ?? throw new InvalidOperationException(
            "No AX206 display (USB 1908:0102) found. Is it plugged in and using the libusb-win32 driver?");
        IntPtr handle = LibUsb0.usb_open(device);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Couldn't open the display: " + LibUsb0.LastError);

        LibUsb0.usb_set_configuration(handle, 1); // required by libusb-win32; harmless if already set
        if (LibUsb0.usb_claim_interface(handle, 0) < 0)
        {
            string error = LibUsb0.LastError;
            LibUsb0.usb_close(handle);
            throw new InvalidOperationException("Couldn't claim the display (is another program using it?): " + error);
        }

        try { return new Ax206Display(handle); }
        catch
        {
            LibUsb0.usb_release_interface(handle, 0);
            LibUsb0.usb_close(handle);
            throw;
        }
    }

    /// <summary>Backlight from 0 (off) to 7 (brightest).</summary>
    public void SetBacklight(int level)
    {
        level = Math.Clamp(level, 0, 7);
        var cmd = Scsi(sub: 6);
        cmd[6] = 0x01; // set property
        cmd[7] = 0x01; // property: brightness
        cmd[9] = (byte)level;
        Command(cmd);
    }

    /// <summary>
    /// Draws a rectangle of pixels. <paramref name="rgb565"/> holds width × height pixels, row by row, two bytes
    /// each, high byte first (RRRRRGGG GGGBBBBB).
    /// </summary>
    public void Blit(int x, int y, int width, int height, byte[] rgb565)
    {
        if (rgb565.Length != width * height * 2) throw new ArgumentException("Pixel data doesn't match the rectangle size.");
        var cmd = Scsi(sub: 6);
        cmd[6] = 0x12; // blit
        Put16(cmd, 7, x);
        Put16(cmd, 9, y);
        Put16(cmd, 11, x + width - 1);
        Put16(cmd, 13, y + height - 1);
        Command(cmd, send: rgb565);
    }

    private static byte[] Scsi(byte sub)
    {
        var cmd = new byte[16];
        cmd[0] = 0xCD;
        cmd[5] = sub;
        return cmd;
    }

    private static void Put16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private void Command(byte[] scsi, byte[]? send = null, byte[]? receive = null)
    {
        int dataLength = send?.Length ?? receive?.Length ?? 0;

        // Command block wrapper. The flags byte stays 0 for both directions, as in the reference driver.
        var cbw = new byte[31];
        "USBC"u8.CopyTo(cbw);
        BitConverter.GetBytes(0xEFBEADDEu).CopyTo(cbw, 4); // tag
        BitConverter.GetBytes(dataLength).CopyTo(cbw, 8);
        cbw[14] = (byte)scsi.Length;
        scsi.CopyTo(cbw, 15);
        Write(cbw, 1000);

        if (send != null) Write(send, 3000);
        if (receive != null) Read(receive, 4000);

        // Command status wrapper: "USBS" ... status in the last byte.
        var csw = new byte[13];
        Read(csw, 5000);
        if (csw[0] != 'U' || csw[1] != 'S' || csw[2] != 'B' || csw[3] != 'S')
            throw new IOException("The display sent an invalid reply.");
        if (csw[12] != 0) throw new IOException($"The display reported error {csw[12]}.");
    }

    private void Write(byte[] data, int timeoutMs)
    {
        int written = LibUsb0.usb_bulk_write(_handle, EndpointOut, data, data.Length, timeoutMs);
        if (written != data.Length) throw new IOException($"USB write failed ({written}): {LibUsb0.LastError}");
    }

    private void Read(byte[] data, int timeoutMs)
    {
        int read = LibUsb0.usb_bulk_read(_handle, EndpointIn, data, data.Length, timeoutMs);
        if (read != data.Length) throw new IOException($"USB read failed ({read}): {LibUsb0.LastError}");
    }

    private static IntPtr? FindDevice()
    {
        // Walk libusb-win32's bus and device lists (struct layouts from its lusb0_usb.h, 64-bit).
        for (IntPtr bus = LibUsb0.usb_get_busses(); bus != IntPtr.Zero; bus = Marshal.ReadIntPtr(bus, LibUsb0.BusNext))
        {
            for (IntPtr dev = Marshal.ReadIntPtr(bus, LibUsb0.BusDevices); dev != IntPtr.Zero; dev = Marshal.ReadIntPtr(dev, LibUsb0.DeviceNext))
            {
                if (Marshal.ReadByte(dev, LibUsb0.DeviceDescriptor) != 18 || Marshal.ReadByte(dev, LibUsb0.DeviceDescriptor + 1) != 1)
                    throw new InvalidOperationException("Unexpected libusb-win32 structure layout.");
                ushort vid = (ushort)Marshal.ReadInt16(dev, LibUsb0.DeviceDescriptor + 8);
                ushort pid = (ushort)Marshal.ReadInt16(dev, LibUsb0.DeviceDescriptor + 10);
                if (vid == VendorId && pid == ProductId) return dev;
            }
        }
        return null;
    }

    public void Dispose()
    {
        LibUsb0.usb_release_interface(_handle, 0);
        LibUsb0.usb_close(_handle);
    }
}

/// <summary>libusb-win32 (libusb 0.1 API), installed in System32 with its driver.</summary>
internal static class LibUsb0
{
    private const string Dll = "libusb0.dll";
    private const int PathMax = 512;

    // struct usb_bus { usb_bus *next, *prev; char dirname[512]; usb_device *devices; ... }
    public const int BusNext = 0;
    public const int BusDevices = 16 + PathMax;

    // struct usb_device { usb_device *next, *prev; char filename[512]; usb_bus *bus; usb_device_descriptor descriptor; ... }
    public const int DeviceNext = 0;
    public const int DeviceDescriptor = 16 + PathMax + 8;

    [DllImport(Dll)] public static extern void usb_init();
    [DllImport(Dll)] public static extern int usb_find_busses();
    [DllImport(Dll)] public static extern int usb_find_devices();
    [DllImport(Dll)] public static extern IntPtr usb_get_busses();
    [DllImport(Dll)] public static extern IntPtr usb_open(IntPtr device);
    [DllImport(Dll)] public static extern int usb_close(IntPtr handle);
    [DllImport(Dll)] public static extern int usb_set_configuration(IntPtr handle, int configuration);
    [DllImport(Dll)] public static extern int usb_claim_interface(IntPtr handle, int iface);
    [DllImport(Dll)] public static extern int usb_release_interface(IntPtr handle, int iface);
    [DllImport(Dll)] public static extern int usb_bulk_write(IntPtr handle, int endpoint, byte[] bytes, int size, int timeout);
    [DllImport(Dll)] public static extern int usb_bulk_read(IntPtr handle, int endpoint, byte[] bytes, int size, int timeout);
    [DllImport(Dll)] private static extern IntPtr usb_strerror();

    public static string LastError => Marshal.PtrToStringAnsi(usb_strerror()) ?? "";
}
