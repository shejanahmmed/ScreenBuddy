using System;
using System.Runtime.InteropServices;

namespace ScreenBuddyCapture;

/// <summary>
/// Simulates keyboard and mouse inputs on Windows by calling the native Win32 SendInput API.
/// Maps touch coordinates received from the Android client to absolute screen coordinates.
/// </summary>
public static class InputSimulator
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUT_UNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const int INPUT_MOUSE = 0;

    // Native mouse input flags (winuser.h)
    private const uint MOUSEEVENTF_MOVE     = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // Action types sent from client (matches Android MotionEvent)
    public const int ACTION_DOWN = 0;
    public const int ACTION_MOVE = 1;
    public const int ACTION_UP   = 2;

    /// <summary>
    /// Simulates a local mouse event based on normalized coordinates (0.0 to 1.0).
    /// </summary>
    public static void SimulateTouchEvent(int action, float normX, float normY)
    {
        // Win32 MOUSEEVENTF_ABSOLUTE maps the screen from (0,0) to (65535,65535)
        int absoluteX = (int)Math.Clamp(normX * 65535f, 0f, 65535f);
        int absoluteY = (int)Math.Clamp(normY * 65535f, 0f, 65535f);

        var mouseInput = new MOUSEINPUT
        {
            dx = absoluteX,
            dy = absoluteY,
            mouseData = 0,
            time = 0,
            dwExtraInfo = IntPtr.Zero
        };

        // Assemble dwFlags depending on finger gesture action
        uint flags = MOUSEEVENTF_ABSOLUTE;
        switch (action)
        {
            case ACTION_DOWN:
                // Move to position, then click left mouse down
                flags |= MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN;
                break;
            case ACTION_MOVE:
                // Move/drag cursor
                flags |= MOUSEEVENTF_MOVE;
                break;
            case ACTION_UP:
                // Move to position, then release left mouse click
                flags |= MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP;
                break;
            default:
                return;
        }

        mouseInput.dwFlags = flags;

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUT_UNION { mi = mouseInput }
        };

        var inputs = new[] { input };
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
}
