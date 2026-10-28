using System.Runtime.InteropServices;
using System.Text;
using FFMoGuang.Core;

namespace FFMoGuang;

public static class NativeIme
{
    public static ImeSnapshot Read()
    {
        var window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out var process);
        if (process != Environment.ProcessId) return new(false, false, "", []);
        var context = ImmGetContext(window);
        if (context == 0) return new(false, false, "", []);
        try
        {
            var available = ImmGetConversionStatus(context, out var conversion, out _);
            var chinese = ImmGetOpenStatus(context) && (conversion & 1) != 0 && (GetKeyState(0x14) & 1) == 0;
            var composition = "";
            var length = ImmGetCompositionStringW(context, 8, null, 0);
            if (length > 0 && length <= 4096 && length % 2 == 0)
            {
                var bytes = new byte[length];
                var copied = ImmGetCompositionStringW(context, 8, bytes, (uint)bytes.Length);
                if (copied > 0 && copied <= bytes.Length && copied % 2 == 0)
                    composition = Encoding.Unicode.GetString(bytes, 0, copied);
            }
            var candidates = Array.Empty<string>();
            var size = ImmGetCandidateListW(context, 0, null, 0);
            if (size is >= 24 and <= 65536)
            {
                var bytes = new byte[size];
                var copied = ImmGetCandidateListW(context, 0, bytes, size);
                if (copied >= 24 && copied <= bytes.Length) candidates = ImeCandidateList.Parse(bytes);
            }
            return new(available, chinese, composition, candidates);
        }
        finally { ImmReleaseContext(window, context); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmGetOpenStatus(nint context);
    [DllImport("imm32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmGetConversionStatus(nint context, out uint conversion, out uint sentence);
    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern int ImmGetCompositionStringW(nint context, uint index, [Out] byte[]? buffer, uint length);
    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern uint ImmGetCandidateListW(nint context, uint index, [Out] byte[]? buffer, uint length);
}
