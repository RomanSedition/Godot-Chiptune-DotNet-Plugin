#if TOOLS
using Godot;

namespace ChiptrackerNet.UI
{
    // Small reusable per-row click/rename handler for ChiptrackerDock's
    // dynamically-built channel/instrument/pattern/order rows.
    //
    // Godot's C# Callable has no GDScript-style Bind(args) to close a
    // signal connection over a row index -- so this wraps that closure in
    // a real GodotObject instance instead, letting the *connection*
    // itself stay a plain object+method Callable (new Callable(handler,
    // MethodName.OnX)), never a raw C# delegate handed straight to
    // Connect(). That's what avoids the delegate_handle.value == nullptr
    // hot-reload flood (see addons/godot_mcp/Editor/UI/DockStyle.cs for
    // the same rule) -- the C# closures below live inside this object,
    // not on the wire to Godot's signal system.
    //
    // RefCounted (not a bare GodotObject) so storing one via
    // Control.SetMeta() on the row it belongs to keeps it alive for
    // exactly as long as that row exists, with no separate bookkeeping.
    [Tool]
    public partial class DockRowHandler : RefCounted
    {
        public System.Action Clicked;
        public System.Action<InputEvent> GuiInputReceived;
        public System.Action<bool> Toggled;
        public System.Action<string> TextSubmitted;
        public System.Action FocusExited;

        public void OnPressedNotify() => Clicked?.Invoke();
        public void OnGuiInputNotify(InputEvent @event) => GuiInputReceived?.Invoke(@event);
        public void OnToggledNotify(bool pressed) => Toggled?.Invoke(pressed);
        public void OnTextSubmittedNotify(string text) => TextSubmitted?.Invoke(text);
        public void OnFocusExitedNotify() => FocusExited?.Invoke();
    }
}
#endif
