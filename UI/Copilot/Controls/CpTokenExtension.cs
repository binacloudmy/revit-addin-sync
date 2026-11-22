using System;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    /// <summary>
    /// {cp:CpToken Cp.Overlay.Pill}: a colour token read straight from the table
    /// (ThemeTokens via CopilotColors), for XAML that is not under the pane's merged
    /// dictionaries, such as the in-canvas overlay window. Gives a Color where the
    /// property is a Color (DropShadowEffect.Color, GradientStop.Color), else a Brush.
    /// Resolved once at load, so use {DynamicResource} instead where the colour must
    /// follow a theme switch. Never throws: an unknown key draws transparent.
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class CpTokenExtension : MarkupExtension
    {
        public CpTokenExtension() { }
        public CpTokenExtension(string key) { Key = key; }

        [ConstructorArgument("key")]
        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            Type type = null;
            try
            {
                var target = serviceProvider?.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;
                type = (target?.TargetProperty as DependencyProperty)?.PropertyType
                       ?? (target?.TargetProperty as PropertyInfo)?.PropertyType;
            }
            catch { }
            if (type == typeof(Color)) return CopilotColors.TokenColor(Key);
            return CopilotColors.Token(Key);
        }
    }
}
