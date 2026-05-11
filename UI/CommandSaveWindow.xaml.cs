using RevitWebAppSync.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Dialog for creating or editing a saved Copilot command. On OK,
    /// <see cref="Result"/> holds the request body to POST (create) or PUT (edit);
    /// <see cref="EditingTemplateId"/> is non-null when editing an existing command.
    /// </summary>
    public partial class CommandSaveWindow : Window
    {
        private static readonly Regex PlaceholderRe = new Regex(@"\{(\w+)\}", RegexOptions.Compiled);

        private readonly int? _userId;
        private readonly int? _orgId;
        private readonly CommandTemplate _editing;          // null when creating new
        private readonly List<CommandVariable> _existingVars;

        public CommandSaveRequest Result { get; private set; }
        public string EditingTemplateId => _editing?.Id;

        /// <param name="initialPrompt">Prompt text to pre-fill (e.g. the user's last prompt).</param>
        /// <param name="userId">Owner user id.</param>
        /// <param name="orgId">Org id, or null if the user isn't on a team.</param>
        /// <param name="editing">If set, the dialog edits this command instead of creating a new one.</param>
        public CommandSaveWindow(string initialPrompt, int? userId, int? orgId, CommandTemplate editing = null)
        {
            InitializeComponent();
            _userId = userId;
            _orgId = orgId;
            _editing = editing;
            _existingVars = editing?.Variables ?? new List<CommandVariable>();

            if (!_orgId.HasValue)
            {
                ScopeOrg.IsEnabled = false;
                ScopeOrg.ToolTip = "You're not linked to a team.";
            }

            if (editing != null)
            {
                Title = "Edit Command";
                SaveButton.Content = "Save changes";
                NameBox.Text = editing.Name ?? "";
                DescriptionBox.Text = editing.Description ?? "";
                CategoryBox.Text = editing.Category ?? "";
                PromptBox.Text = editing.PromptTemplate ?? "";
                if (string.Equals(editing.Scope, "org", StringComparison.OrdinalIgnoreCase) && _orgId.HasValue)
                    ScopeOrg.IsChecked = true;
            }
            else
            {
                PromptBox.Text = initialPrompt ?? "";
            }

            RefreshVariablesPreview();
        }

        private void AnyField_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            RefreshVariablesPreview();
        }

        private List<string> DetectPlaceholders()
        {
            return PlaceholderRe.Matches(PromptBox.Text ?? "")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RefreshVariablesPreview()
        {
            var names = DetectPlaceholders();
            VariablesPreview.Text = names.Count == 0
                ? "No variables detected. Add {name} placeholders to make this command reusable."
                : "Variables: " + string.Join(", ", names.Select(n => "{" + n + "}"));
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameBox.Text?.Trim();
            var prompt = PromptBox.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { ShowError("Name is required."); return; }
            if (string.IsNullOrEmpty(prompt)) { ShowError("Prompt template is required."); return; }

            // Build variables from detected placeholders, preserving label/type/options
            // from the existing command when editing.
            var vars = new List<CommandVariable>();
            foreach (var n in DetectPlaceholders())
            {
                var prev = _existingVars.FirstOrDefault(v => string.Equals(v.Name, n, StringComparison.OrdinalIgnoreCase));
                vars.Add(prev ?? new CommandVariable { Name = n, Label = n, Type = "text", Default = "" });
            }

            var scope = ScopeOrg.IsChecked == true ? "org" : "user";

            Result = new CommandSaveRequest
            {
                Name = name,
                Description = string.IsNullOrWhiteSpace(DescriptionBox.Text) ? null : DescriptionBox.Text.Trim(),
                Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? null : CategoryBox.Text.Trim(),
                PromptTemplate = prompt,
                Variables = vars,
                Scope = scope,
                UserId = _userId,
                OrgId = scope == "org" ? _orgId : null
            };
            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ShowError(string msg)
        {
            ErrorText.Text = msg;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
