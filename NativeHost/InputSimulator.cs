using System.Runtime.InteropServices;

namespace SSLExceptionManager;

internal static class InputSimulator
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002, KeyEventUnicode = 0x0004;
    private const ushort VkShift = 0x10, VkControl = 0x11, VkAlt = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
    private const string Phrase = "thisisunsafe";

    public static InputResult SendUnsafePhrase()
    {
        foreach (ushort modifier in new[] { VkShift, VkControl, VkAlt, VkLWin, VkRWin })
            if ((GetAsyncKeyState(modifier) & 0x8000) != 0)
                return new(false, 0, "A modifier key is held.");

        INPUT[] events = new INPUT[Phrase.Length * 2];
        for (int i = 0; i < Phrase.Length; i++)
        {
            ushort character = Phrase[i];
            events[i * 2] = Key(character, KeyEventUnicode);
            events[i * 2 + 1] = Key(character, KeyEventUnicode | KeyEventKeyUp);
        }
        uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf<INPUT>());
        if (sent != events.Length)
        {
            INPUT[] release = Phrase.Select(ch => Key(ch, KeyEventUnicode | KeyEventKeyUp)).ToArray();
            _ = SendInput((uint)release.Length, release, Marshal.SizeOf<INPUT>());
        }
        return sent == events.Length
            ? new(true, sent, "Operating system accepted all key events; Chrome completion is not verified.")
            : new(false, sent, $"SendInput accepted {sent} of {events.Length} key events.");
    }

    private static INPUT Key(ushort character, uint flags) => new()
    {
        type = InputKeyboard,
        data = new INPUTUNION { ki = new KEYBDINPUT { wScan = character, dwFlags = flags } }
    };

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nuint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nuint dwExtraInfo; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    internal sealed record InputResult(bool Accepted, uint EventsAccepted, string Message);
}
