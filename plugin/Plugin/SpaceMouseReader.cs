using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SpaceMouseStudio
{
    /// <summary>
    /// Reads 3Dconnexion devices through the Windows Raw Input API.
    ///
    /// Raw input delivers the device's HID reports directly, independent of the 3DxWare driver,
    /// which keeps running and keeps working in other applications. The reader runs on its own
    /// thread with an invisible window that receives the WM_INPUT messages, so input is never
    /// delayed by Studio's frame loop. The main thread picks up the latest values with <see cref="ReadAxes"/> once per frame.
    ///
    /// HID report layout of 3Dconnexion devices (all values little-endian 16-bit, about -350..350):
    ///   report 1: tx ty tz (older devices) or tx ty tz rx ry rz (newer devices)
    ///   report 2: rx ry rz (older devices only)
    ///   report 3: button bit mask
    /// </summary>
    internal sealed class SpaceMouseReader
    {
        // HID usage "Generic Desktop / Multi-axis Controller", which all 3Dconnexion devices report
        private const ushort UsagePageGeneric = 0x01;
        private const ushort UsageMultiAxis = 0x08;

        // _sync protects everything that is shared between the reader thread and the main thread
        private readonly object _sync = new object();
        private readonly short[] _axes = new short[6];
        private readonly Queue<uint> _buttonEvents = new Queue<uint>();
        private readonly Dictionary<IntPtr, bool> _devices = new Dictionary<IntPtr, bool>(); // device handle -> is a SpaceMouse
        private uint _buttons;

        // Diagnostics: every report with a timestamp (Stopwatch ticks)
        private const int RecordCapacity = 20000;
        private long[] _recTime;
        private byte[] _recId;
        private short[] _recValues;
        private int _recCount;
        private bool _recording;

        private Thread _thread;
        private IntPtr _hwnd;
        private Native.WndProc _wndProc; // keep a reference, otherwise the GC collects the native callback
        private string _className;

        /// <summary>Set if the reader thread could not start or stopped with an error.</summary>
        public string Error { get; private set; }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "SpaceMouseReader" };
            _thread.Start();
        }

        /// <summary>Asks the reader thread to close its window and end its message loop.</summary>
        public void Stop()
        {
            IntPtr hwnd = _hwnd;
            if (hwnd != IntPtr.Zero)
                Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Number of connected devices that were recognized as a SpaceMouse.</summary>
        public int DeviceCount
        {
            get
            {
                lock (_sync)
                {
                    int n = 0;
                    foreach (bool isSpaceMouse in _devices.Values)
                        if (isSpaceMouse)
                            n++;
                    return n;
                }
            }
        }

        /// <summary>Copies the latest raw values (tx, ty, tz, rx, ry, rz) into <paramref name="target"/>.</summary>
        public void ReadAxes(short[] target)
        {
            lock (_sync)
                Array.Copy(_axes, target, 6);
        }

        /// <summary>Starts recording every incoming report (for diagnostics).</summary>
        public void StartRecording()
        {
            lock (_sync)
            {
                if (_recTime == null)
                {
                    _recTime = new long[RecordCapacity];
                    _recId = new byte[RecordCapacity];
                    _recValues = new short[RecordCapacity * 6];
                }
                _recCount = 0;
                _recording = true;
            }
        }

        /// <summary>Stops recording. Returns the number of reports and their time, report ID and six values each.</summary>
        public int StopRecording(out long[] time, out byte[] ids, out short[] values)
        {
            lock (_sync)
            {
                _recording = false;
                time = _recTime;
                ids = _recId;
                values = _recValues;
                return _recCount;
            }
        }

        /// <summary>Returns the next change of the button state (bit mask, bit 0 = button 1), if any.</summary>
        public bool TryGetButtonEvent(out uint mask)
        {
            lock (_sync)
            {
                if (_buttonEvents.Count == 0)
                {
                    mask = 0;
                    return false;
                }
                mask = _buttonEvents.Dequeue();
                return true;
            }
        }

        /// <summary>Reader thread: creates the hidden window, registers for raw input and pumps messages.</summary>
        private void Run()
        {
            try
            {
                IntPtr instance = Native.GetModuleHandle(null);
                _className = "SpaceMouseStudioRawInput";
                _wndProc = WindowProc;
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = instance,
                    lpszClassName = _className,
                };
                if (Native.RegisterClassEx(ref wc) == 0)
                    throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
                _hwnd = Native.CreateWindowEx(0, _className, "SpaceMouseStudio", 0, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero)
                    throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

                // Raw input registrations are per process and per usage. Log what Unity has registered,
                // which helps when another component competes for the same usage.
                foreach (Native.RAWINPUTDEVICE existing in GetRegistrations())
                    Log.Info(string.Format("Existing raw input registration in this process: usage {0:X2}/{1:X2}, flags {2:X}",
                        existing.usUsagePage, existing.usUsage, existing.dwFlags));
                if (!Register())
                    throw new InvalidOperationException("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());

                // Unity could later register multi-axis devices for itself, which would redirect the
                // input to its own window. A timer checks every 2 seconds and registers again if needed.
                Native.SetTimer(_hwnd, new UIntPtr(1), 2000, IntPtr.Zero);
                Log.Info("Raw input ready");

                Native.MSG msg;
                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }
                Native.UnregisterClass(_className, instance);
            }
            catch (Exception e)
            {
                Error = e.Message;
                Log.Info("SpaceMouse reader stopped: " + e);
            }
        }

        /// <summary>
        /// Registers this window for multi-axis devices. INPUTSINK delivers input even when Studio is
        /// not in the foreground (the controller decides whether to use it); DEVNOTIFY reports
        /// devices being plugged in or removed.
        /// </summary>
        private bool Register()
        {
            var rid = new[]
            {
                new Native.RAWINPUTDEVICE
                {
                    usUsagePage = UsagePageGeneric,
                    usUsage = UsageMultiAxis,
                    dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY,
                    hwndTarget = _hwnd,
                },
            };
            return Native.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));
        }

        private static Native.RAWINPUTDEVICE[] GetRegistrations()
        {
            uint size = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE));
            uint count = 0;
            Native.GetRegisteredRawInputDevices(null, ref count, size);
            var list = new Native.RAWINPUTDEVICE[count];
            if (count == 0 || Native.GetRegisteredRawInputDevices(list, ref count, size) < 0)
                return new Native.RAWINPUTDEVICE[0];
            return list;
        }

        /// <summary>Registers again if our registration for multi-axis devices was replaced.</summary>
        private void EnsureRegistered()
        {
            foreach (Native.RAWINPUTDEVICE r in GetRegistrations())
            {
                if (r.usUsagePage == UsagePageGeneric && r.usUsage == UsageMultiAxis && r.hwndTarget == _hwnd)
                    return;
            }
            Log.Info("Raw input registration was lost, registering again: " + (Register() ? "ok" : "failed"));
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                switch (msg)
                {
                    case Native.WM_INPUT:
                        OnInput(lParam);
                        break; // DefWindowProc must still be called for WM_INPUT
                    case Native.WM_TIMER:
                        EnsureRegistered();
                        return IntPtr.Zero;
                    case Native.WM_INPUT_DEVICE_CHANGE:
                        OnDeviceChange(wParam.ToInt32(), lParam);
                        break;
                    case Native.WM_CLOSE:
                        Native.DestroyWindow(hWnd);
                        return IntPtr.Zero;
                    case Native.WM_DESTROY:
                        _hwnd = IntPtr.Zero;
                        Native.PostQuitMessage(0);
                        return IntPtr.Zero;
                }
            }
            catch (Exception e)
            {
                // exceptions must never propagate into the native window procedure
                Log.Info("Error in raw input callback: " + e);
            }
            return Native.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void OnDeviceChange(int change, IntPtr device)
        {
            if (change == Native.GIDC_ARRIVAL)
            {
                bool isSpaceMouse = IsSpaceMouse(device);
                lock (_sync)
                    _devices[device] = isSpaceMouse;
            }
            else if (change == Native.GIDC_REMOVAL)
            {
                lock (_sync)
                {
                    bool wasSpaceMouse;
                    if (_devices.TryGetValue(device, out wasSpaceMouse) && wasSpaceMouse)
                    {
                        // otherwise the camera would keep moving with the last values
                        Array.Clear(_axes, 0, 6);
                        Log.Info("SpaceMouse removed");
                    }
                    _devices.Remove(device);
                }
            }
        }

        /// <summary>Checks vendor ID and HID usage of a device. Other multi-axis devices (e.g. gamepads) are ignored.</summary>
        private static bool IsSpaceMouse(IntPtr device)
        {
            // RID_DEVICE_INFO: cbSize, dwType, then for HID devices: VendorId, ProductId, VersionNumber, UsagePage, Usage
            const int size = 32;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt32(buf, 0, size);
                uint cb = size;
                if ((int)Native.GetRawInputDeviceInfo(device, Native.RIDI_DEVICEINFO, buf, ref cb) <= 0)
                    return false;
                if ((uint)Marshal.ReadInt32(buf, 4) != Native.RIM_TYPEHID)
                    return false;
                uint vendor = (uint)Marshal.ReadInt32(buf, 8);
                uint product = (uint)Marshal.ReadInt32(buf, 12);
                ushort usagePage = (ushort)Marshal.ReadInt16(buf, 20);
                ushort usage = (ushort)Marshal.ReadInt16(buf, 22);
                // 0x046D: Logitech (older 3Dconnexion devices such as the SpaceNavigator), 0x256F: 3Dconnexion
                bool ok = (vendor == 0x046D || vendor == 0x256F) && usagePage == UsagePageGeneric && usage == UsageMultiAxis;
                if (ok)
                    Log.Info(string.Format("SpaceMouse found: VID {0:X4}, PID {1:X4}", vendor, product));
                return ok;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>Handles one WM_INPUT message, which may contain several HID reports.</summary>
        private void OnInput(IntPtr rawInput)
        {
            uint headerSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER));
            uint size = 0;
            Native.GetRawInputData(rawInput, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0)
                return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (Native.GetRawInputData(rawInput, Native.RID_INPUT, buf, ref size, headerSize) != size)
                    return;
                var header = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(Native.RAWINPUTHEADER));
                if (header.dwType != Native.RIM_TYPEHID)
                    return;

                // devices that were connected before we registered are identified on their first report
                bool isSpaceMouse;
                bool known;
                lock (_sync)
                    known = _devices.TryGetValue(header.hDevice, out isSpaceMouse);
                if (!known)
                {
                    isSpaceMouse = IsSpaceMouse(header.hDevice);
                    lock (_sync)
                        _devices[header.hDevice] = isSpaceMouse;
                }
                if (!isSpaceMouse)
                    return;

                // RAWHID: dwSizeHid, dwCount, then dwCount reports of dwSizeHid bytes each
                int sizeHid = Marshal.ReadInt32(buf, (int)headerSize);
                int count = Marshal.ReadInt32(buf, (int)headerSize + 4);
                if (sizeHid <= 0 || count <= 0)
                    return;
                byte[] data = new byte[sizeHid * count];
                Marshal.Copy(new IntPtr(buf.ToInt64() + headerSize + 8), data, 0, data.Length);
                for (int i = 0; i < count; i++)
                    ParseReport(data, i * sizeHid, sizeHid);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>Decodes one HID report. The first byte is the report ID.</summary>
        private void ParseReport(byte[] d, int offset, int length)
        {
            int reportId = d[offset];
            lock (_sync)
            {
                if (_recording && _recCount < RecordCapacity)
                {
                    _recTime[_recCount] = System.Diagnostics.Stopwatch.GetTimestamp();
                    _recId[_recCount] = (byte)reportId;
                    for (int i = 0; i < 6; i++)
                        _recValues[_recCount * 6 + i] = 1 + 2 * i + 1 < length ? BitConverter.ToInt16(d, offset + 1 + 2 * i) : (short)0;
                    _recCount++;
                }
                if (reportId == 1 && length >= 13)
                {
                    // newer devices: translation and rotation in one report
                    for (int i = 0; i < 6; i++)
                        _axes[i] = BitConverter.ToInt16(d, offset + 1 + 2 * i);
                }
                else if (reportId == 1 && length >= 7)
                {
                    // older devices: translation only
                    for (int i = 0; i < 3; i++)
                        _axes[i] = BitConverter.ToInt16(d, offset + 1 + 2 * i);
                }
                else if (reportId == 2 && length >= 7)
                {
                    // older devices: rotation
                    for (int i = 0; i < 3; i++)
                        _axes[3 + i] = BitConverter.ToInt16(d, offset + 1 + 2 * i);
                }
                else if (reportId == 3)
                {
                    // buttons: up to 32 bits, queue only changes
                    uint mask = 0;
                    for (int i = 1; i < length && i <= 4; i++)
                        mask |= (uint)d[offset + i] << (8 * (i - 1));
                    if (mask != _buttons)
                    {
                        _buttons = mask;
                        _buttonEvents.Enqueue(mask);
                    }
                }
            }
        }
    }
}
