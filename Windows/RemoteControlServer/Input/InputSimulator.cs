using System;
using System.Runtime.InteropServices;
using RemoteControlServer.Core;
using RemoteControlServer.Emulators;

namespace RemoteControlServer.Input;

/// <summary>
/// Fallback control path used only when ADB is not available for a given emulator instance.
/// Simulates mouse/touch/keyboard input directly against the emulator's window using the Win32
/// SendInput / PostMessage APIs. This is less precise than ADB (it depends on window position
/// and DPI scaling) but works for any window-based emulator, including ones without an ADB
/// bridge exposed.
///
/// Touch is simulated via WM_POINTER / mouse-event injection scaled to client-area coordinates;
/// true multi-touch (pinch) uses SendInput's INPUT_MOUSE won't do two independent contacts,
/// so multi-touch specifically uses the Windows Pointer Input API (InjectTouchInput) which
/// supports up to 10 simultaneous contacts and both LDPlayer and BlueStacks correctly translate
/// injected touch pointers into Android multi-touch events.
/// </summary>
public class InputSimulator
{
    #region Win32 interop

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, int dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public int pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocation;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int inputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    private const uint PT_TOUCH = 0x00000002;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;
    private const uint POINTER_FLAG_UP = 0x00040000;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint TOUCH_MASK_CONTACTAREA = 0x00000001;
    private const uint TOUCH_FEEDBACK_DEFAULT = 0x1;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;

    #endregion

    private bool _touchInjectionInitialized;

    public InputSimulator()
    {
        _touchInjectionInitialized = InitializeTouchInjection(10, TOUCH_FEEDBACK_DEFAULT);
    }

    public void SendTouch(EmulatorInstance emulator, TouchEventPayload touch)
    {
        var hwnd = emulator.WindowHandle;
        if (hwnd == null || hwnd == IntPtr.Zero) return;

        var (screenX, screenY) = NormalizedToScreen(hwnd.Value, touch.X, touch.Y);

        switch (touch.Action)
        {
            case "tap":
            case "down":
                SetCursorPos(screenX, screenY);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                break;
            case "move":
                SetCursorPos(screenX, screenY);
                break;
            case "doubletap":
                for (int i = 0; i < 2; i++)
                {
                    SetCursorPos(screenX, screenY);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                    Thread.Sleep(60);
                }
                break;
            case "longpress":
                SetCursorPos(screenX, screenY);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                Thread.Sleep(550);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                break;
        }
    }

    /// <summary>True multi-touch (pinch/zoom, multi-finger swipe) via Windows Pointer Injection.</summary>
    public void SendMultiTouch(EmulatorInstance emulator, MultiTouchPayload payload)
    {
        var hwnd = emulator.WindowHandle;
        if (hwnd == null || hwnd == IntPtr.Zero || !_touchInjectionInitialized) return;

        var contacts = new POINTER_TOUCH_INFO[payload.Points.Count];
        for (int i = 0; i < payload.Points.Count; i++)
        {
            var point = payload.Points[i];
            var (screenX, screenY) = NormalizedToScreen(hwnd.Value, point.X, point.Y);

            uint flags = point.Phase switch
            {
                "began" => POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,
                "moved" => POINTER_FLAG_UPDATE | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT,
                "ended" => POINTER_FLAG_UP,
                "cancelled" => POINTER_FLAG_UP,
                _ => POINTER_FLAG_UPDATE,
            };

            contacts[i] = new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO
                {
                    pointerType = (int)PT_TOUCH,
                    pointerId = (uint)point.Id,
                    ptPixelLocation = new POINT { X = screenX, Y = screenY },
                    pointerFlags = flags,
                    hwndTarget = hwnd.Value,
                },
                touchFlags = 0,
                touchMask = TOUCH_MASK_CONTACTAREA,
                rcContact = new RECT { Left = screenX - 5, Top = screenY - 5, Right = screenX + 5, Bottom = screenY + 5 },
                orientation = 90,
                pressure = 32000,
            };
        }

        InjectTouchInput((uint)contacts.Length, contacts);
    }

    public void SendKey(EmulatorInstance emulator, KeyEventPayload key)
    {
        var hwnd = emulator.WindowHandle;
        if (hwnd == null || hwnd == IntPtr.Zero) return;

        uint msg = key.Action == "down" ? WM_KEYDOWN : WM_KEYUP;
        PostMessage(hwnd.Value, msg, new IntPtr(AndroidKeyCodeToVirtualKey(key.KeyCode)), IntPtr.Zero);
    }

    public void SendText(EmulatorInstance emulator, string text)
    {
        var hwnd = emulator.WindowHandle;
        if (hwnd == null || hwnd == IntPtr.Zero) return;

        foreach (char c in text)
            PostMessage(hwnd.Value, WM_CHAR, new IntPtr(c), IntPtr.Zero);
    }

    public void SendHardwareButton(EmulatorInstance emulator, string button)
    {
        var hwnd = emulator.WindowHandle;
        if (hwnd == null || hwnd == IntPtr.Zero) return;

        // Most emulators map these to F-keys or Esc/Ctrl combos in their own window by default;
        // exact bindings are configurable per-emulator in their settings UI, mirrored here as
        // sensible defaults. This is a best-effort fallback - ADB keyevents are far more reliable.
        int vk = button.ToLowerInvariant() switch
        {
            "back" => 0x1B,   // VK_ESCAPE
            "home" => 0x24,   // VK_HOME
            "menu" => 0x5D,   // VK_APPS (context menu key)
            "recents" => 0x09, // Tab (commonly bound to recents/app-switch)
            "volumeup" => 0xAF,
            "volumedown" => 0xAE,
            _ => 0,
        };

        if (vk != 0)
        {
            PostMessage(hwnd.Value, WM_KEYDOWN, new IntPtr(vk), IntPtr.Zero);
            PostMessage(hwnd.Value, WM_KEYUP, new IntPtr(vk), IntPtr.Zero);
        }
    }

    private static (int x, int y) NormalizedToScreen(IntPtr hwnd, double nx, double ny)
    {
        GetClientRect(hwnd, out var rect);
        var origin = new POINT { X = 0, Y = 0 };
        ClientToScreen(hwnd, ref origin);

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;

        return (origin.X + (int)(nx * width), origin.Y + (int)(ny * height));
    }

    /// <summary>Maps a small, common subset of Android keycodes to Windows virtual-key codes for the fallback path.</summary>
    private static int AndroidKeyCodeToVirtualKey(int androidKeyCode) => androidKeyCode switch
    {
        4 => 0x1B,  // KEYCODE_BACK -> Escape
        3 => 0x24,  // KEYCODE_HOME -> Home
        66 => 0x0D, // KEYCODE_ENTER -> Enter
        67 => 0x08, // KEYCODE_DEL -> Backspace
        _ => androidKeyCode,
    };
}
