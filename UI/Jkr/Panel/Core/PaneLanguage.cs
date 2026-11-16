using System;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>
    /// The language picked with the pane's BM / EN switch, for text the add-in
    /// writes outside the screen models: toasts, busy labels, dialogs, fix
    /// errors, how-to-fix steps, Locate messages. The pane shell sets it; every
    /// other piece reads it. BM until the shell says otherwise (the pane's default).
    /// </summary>
    public static class PaneLanguage
    {
        private static string _current = PanelStrings.Ms;

        public static string Current => _current;
        public static bool IsEn => _current == PanelStrings.En;

        /// <summary>Raised after the language changes.</summary>
        public static event Action Changed;

        public static void Set(string lang)
        {
            var l = string.Equals(lang, PanelStrings.En, StringComparison.OrdinalIgnoreCase) ? PanelStrings.En : PanelStrings.Ms;
            if (l == _current) return;
            _current = l;
            Changed?.Invoke();
        }

        /// <summary>The BM or the EN text, by the current language.</summary>
        public static string Pick(string ms, string en) => IsEn ? en : ms;
    }
}
