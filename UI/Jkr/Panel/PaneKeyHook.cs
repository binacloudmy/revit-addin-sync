using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using RevitWebAppSync.UI.Jkr.Panel.Core;

namespace RevitWebAppSync.UI.Jkr.Panel
{
    /// <summary>
    /// Delivers the pane's letter shortcuts (J / K / F / A and "/") inside Revit.
    ///
    /// Revit consumes plain letter keys for its own keyboard shortcuts before any
    /// WPF element sees them (found in the Revit UI test: J/K/F/A did nothing in
    /// the Results list and the issue detail; arrows, Enter and Esc arrived). A
    /// thread hook (WH_KEYBOARD) on Revit's UI thread did not see them either, so
    /// they are taken earlier than the message queue. This uses a low-level
    /// keyboard hook (WH_KEYBOARD_LL), which Windows calls before any window or
    /// later stage sees the key: when the key is one of ours and our detail
    /// window is active or the Results list has the keyboard focus, it is handled
    /// and swallowed; every other key goes on untouched. The callback runs on the
    /// pane's UI thread (the thread that installed it) and returns at once.
    ///
    /// With BINA_DEV_UNLOCK=1 each decision on one of these five keys is logged to
    /// %TEMP%\bina-keyhook.log (no other key is ever logged).
    ///
    /// Note for testing tools: a tool that types characters (VK_PACKET) does not
    /// press the J key; it gives WPF text input only. Use a real key press.
    /// </summary>
    internal sealed class PaneKeyHook : IDisposable
    {
        private const int WhKeyboardLl = 13;
        private const int HcAction = 0;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;

        [StructLayout(LayoutKind.Sequential)]
        private struct KbdLlHookStruct
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);


        private readonly Func<PanelKey, bool> _handle;
        private readonly Func<bool> _paneHasFocus;
        private int _swallowUndoUp;
        private readonly HookProc _proc;   // kept alive while the hook is installed
        private IntPtr _hook;
        private PanelKey _swallowUp = PanelKey.Other;
        private static readonly bool Log = Environment.GetEnvironmentVariable("BINA_DEV_UNLOCK") == "1";

        /// <param name="handle">Called on the UI thread for one of our keys; true =
        /// handled (the key is swallowed, Revit never sees it). It decides whether
        /// our pane or detail has the focus.</param>
        /// <param name="paneHasFocus">True when the pane (or its detail window) has the
        /// keyboard focus; Ctrl+Z / Ctrl+Y are forwarded to Revit's Undo / Redo only then (AI-441).</param>
        public PaneKeyHook(Func<PanelKey, bool> handle, Func<bool> paneHasFocus = null)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _paneHasFocus = paneHasFocus ?? (() => false);
            _proc = Proc;
        }

        public void Install()
        {
            if (_hook != IntPtr.Zero) return;
            var module = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _hook = SetWindowsHookEx(WhKeyboardLl, _proc, module, 0);
            Trace($"install hook={_hook} err={Marshal.GetLastWin32Error()}");
        }

        public void Dispose()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            Trace("uninstall");
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
                case 0xBF:                        // VK_OEM_2: "/" on US layouts
                case 0x6F: return PanelKey.Slash; // VK_DIVIDE
                default: return PanelKey.Other;
            }
        }

        private IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code == HcAction)
                {
                    var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    var msg = wParam.ToInt32();
                    var key = Map((int)info.vkCode);
                    if (OurProcessInFront() && TryForwardUndo(msg, (int)info.vkCode)) return (IntPtr)1;
                    if (key != PanelKey.Other && OurProcessInFront())
                    {
                        if (msg == WmKeyUp && _swallowUp == key)
                        {
                            _swallowUp = PanelKey.Other;
                            return (IntPtr)1;
                        }
                        if (msg == WmKeyDown || msg == WmSysKeyDown)
                        {
                            var typing = Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                                         || Keyboard.FocusedElement is System.Windows.Controls.ComboBox
                                         || Keyboard.FocusedElement is System.Windows.Controls.ComboBoxItem;
                            var mods = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows);
                            var handled = !typing && mods == ModifierKeys.None && _handle(key);
                            Trace($"key={key} focus={Keyboard.FocusedElement?.GetType().Name ?? "none"} typing={typing} handled={handled}");
                            if (handled)
                            {
                                _swallowUp = key;
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace("error " + ex.Message);
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        /// <summary>Ctrl+Z / Ctrl+Y with the focus in the pane: Revit never sees them, so ask it
        /// to undo / redo. The key-up of a swallowed chord is swallowed too.</summary>
        private bool TryForwardUndo(int msg, int vk)
        {
            if (msg == WmKeyUp && _swallowUndoUp == vk) { _swallowUndoUp = 0; return true; }
            if (msg != WmKeyDown) return false;
            if (vk != 0x5A && vk != 0x59) return false;
            var f = Keyboard.FocusedElement;
            var typing = f is System.Windows.Controls.Primitives.TextBoxBase
                         || f is System.Windows.Controls.ComboBox
                         || f is System.Windows.Controls.ComboBoxItem;
            var m = Keyboard.Modifiers;
            var action = PaneUndoKey.Decide(vk, (m & ModifierKeys.Control) != 0, (m & ModifierKeys.Shift) != 0,
                (m & ModifierKeys.Alt) != 0, typing, f != null && _paneHasFocus());
            if (action == PaneUndoAction.None) return false;
            var posted = App.PostUndoRedo(action == PaneUndoAction.Redo);
            Trace($"ctrl-{(action == PaneUndoAction.Redo ? "redo" : "undo")} posted={posted}");
            if (!posted) return false;   // Revit refused (nothing to undo / modal): let the key go on
            _swallowUndoUp = vk;
            return true;
        }

        /// <summary>The foreground window is Revit's or one of ours (same process).</summary>
        private static bool OurProcessInFront()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        private static void Trace(string line)
        {
            if (!Log) return;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "bina-keyhook.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine); }
            catch { }
        }
    }
}
