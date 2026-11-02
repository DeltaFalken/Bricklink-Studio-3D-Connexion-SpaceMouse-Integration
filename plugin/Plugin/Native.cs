using System;
using System.Runtime.InteropServices;

namespace SpaceMouseStudio
{
    /// <summary>
    /// Win32 declarations used by <see cref="SpaceMouseReader"/>: a window class and message loop for
    /// the reader thread, and the Raw Input API.
    /// </summary>
    internal static class Native
    {
        // window messages
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_INPUT_DEVICE_CHANGE = 0x00FE;
        public const uint WM_INPUT = 0x00FF;
        public const uint WM_TIMER = 0x0113;

        // raw input
        public const uint RIDEV_INPUTSINK = 0x00000100;  // receive input even when not in the foreground
        public const uint RIDEV_DEVNOTIFY = 0x00002000;  // receive WM_INPUT_DEVICE_CHANGE
        public const uint RID_INPUT = 0x10000003;
        public const uint RIDI_DEVICEINFO = 0x2000000B;
        public const uint RIM_TYPEHID = 2;
        public const int GIDC_ARRIVAL = 1;
        public const int GIDC_REMOVAL = 2;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
        public static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "GetMessageW")]
        public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint filterMin, uint filterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        public static extern IntPtr DispatchMessage(ref MSG msg);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetRegisteredRawInputDevices([Out] RAWINPUTDEVICE[] devices, ref uint count, uint size);

        [DllImport("user32.dll")]
        public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint elapseMs, IntPtr timerProc);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
        public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, IntPtr data, ref uint size);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string moduleName);
    }
}
