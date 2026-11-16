using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// Delivers the pane's letter shortcuts (J / K / F / A and "/") inside Revit.
    ///
    /// Revit treats plain letter keys as its own keyboard shortcuts and consumes
    /// them in its message loop before WPF sees them, so a PreviewKeyDown handler
    /// in the pane or the issue detail never got J, K, F, A or "/" (arrows, Enter
    /// and Esc are not Revit shortcuts and always arrived). A thread keyboard hook
    /// (WH_KEYBOARD) runs when the message is taken from the queue, before Revit's
    /// handling: when our pane or detail has the focus and the key is one of ours,
    /// the hook handles it and swallows it; every other key goes on to Revit
    /// untouched. Text boxes (search) keep their letters.
    /// </summary>
    internal sealed class PaneKeyHook : IDisposable
    {
        private const int WhKeyboard = 2;
        private const int HcAction = 0;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private readonly Func<PanelKey, bool> _handle;
        private readonly HookProc _proc;   // kept alive while the hook is installed
        private IntPtr _hook;

        /// <param name="handle">Called on the UI thread for one of our keys while the
        /// pane or the detail has the focus; true = handled (Revit never sees it).</param>
        public PaneKeyHook(Func<PanelKey, bool> handle)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _proc = Proc;
        }

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _hook = SetWindowsHookEx(WhKeyboard, _proc, IntPtr.Zero, GetCurrentThreadId());
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        /// <summary>Our letter shortcuts by virtual-key code; Other for anything else.</summary>
        public static PanelKey Map(int vk)
        {
            switch (vk)
            {
                case 0x4A: return PanelKey.J;
                case 0x4B: return PanelKey.K;
                case 0x46: return PanelKey.F;
                case 0x41: return PanelKey.A;
                case 0xBF:                       // VK_OEM_2: "/" on US layouts
                case 0x6F: return PanelKey.Slash; // VK_DIVIDE
                default: return PanelKey.Other;
            }
        }

        private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                // lParam bit 31 = key-up; only a key-down is acted on (its key-up and
                // auto-repeat of a handled key are swallowed with it).
                var keyUp = (lParam.ToInt64() & 0x80000000L) != 0;
                var key = code == HcAction ? Map(wParam.ToInt32()) : PanelKey.Other;
                if (key != PanelKey.Other
                    && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0
                    && !(Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
                    && !(Keyboard.FocusedElement is System.Windows.Controls.ComboBox)
                    && !(Keyboard.FocusedElement is System.Windows.Controls.ComboBoxItem))
                {
                    if (keyUp) return _lastHandled == key ? (IntPtr)1 : CallNextHookEx(_hook, code, wParam, lParam);
                    if (_handle(key)) { _lastHandled = key; return (IntPtr)1; }
                }
                _lastHandled = PanelKey.Other;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BINA] pane key hook: {ex.Message}");
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        private PanelKey _lastHandled = PanelKey.Other;
    }
}
