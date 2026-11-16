using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace CodeView.Presentation;

internal sealed partial class GuiPerformanceProbe
{
    private bool _dispatchingAppKitFixture;

    private bool CheckAppKitOption()
    {
        const uint FlagsChanged = 12;
        const uint Option = 1 << 19;
        const ushort LeftOption = 0x3A;
        var menu = _window.FindControl<Menu>("MainMenu")!;
        _editor.TextArea.Focus();
        var app = AppKit.Get(AppKit.Class("NSApplication"), AppKit.Selector("sharedApplication"));
        if (_window.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } window)
            throw new InvalidOperationException("No AppKit window for Option validation.");
        var number = AppKit.Get(window.Handle, AppKit.Selector("windowNumber"));
        var empty = AppKit.Get(AppKit.Class("NSString"), AppKit.Selector("string"));
        var eventClass = AppKit.Class("NSEvent");
        var createEvent = AppKit.Selector("keyEventWithType:location:modifierFlags:timestamp:windowNumber:context:characters:charactersIgnoringModifiers:isARepeat:keyCode:");
        var sendEvent = AppKit.Selector("sendEvent:");
        _dispatchingAppKitFixture = true;
        try
        {
            Send(Option);
            if (menu.IsOpen) throw new InvalidOperationException("Option opened the menu before release.");
            Send(0);
            if (!menu.IsOpen || menu.Items[0] is not MenuItem { IsFocused: true })
                throw new InvalidOperationException("AppKit Option release did not focus the menu.");
            menu.Close();
            if (!_editor.TextArea.IsFocused) throw new InvalidOperationException("Closing the menu lost editor focus.");
            return true;
        }
        finally { _dispatchingAppKitFixture = false; }

        void Send(uint modifiers)
        {
            var nativeEvent = AppKit.CreateKeyEvent(eventClass, createEvent, FlagsChanged, default,
                modifiers, 0, number, 0, empty, empty, 0, LeftOption);
            if (nativeEvent == 0) throw new InvalidOperationException("Could not create AppKit Option event.");
            AppKit.SendEvent(app, sendEvent, nativeEvent);
        }
    }

    private static class AppKit
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [StructLayout(LayoutKind.Sequential)]
        internal readonly record struct Point(double X, double Y);

        [DllImport(ObjC, EntryPoint = "objc_getClass")]
        internal static extern nint Class(string name);

        [DllImport(ObjC, EntryPoint = "sel_registerName")]
        internal static extern nint Selector(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern nint Get(nint receiver, nint selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern void SendEvent(nint receiver, nint selector, nint nativeEvent);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        internal static extern nint CreateKeyEvent(nint receiver, nint selector, nuint type, Point location,
            nuint modifiers, double timestamp, nint windowNumber, nint context, nint characters,
            nint charactersIgnoringModifiers, byte isRepeat, ushort keyCode);
    }
}
