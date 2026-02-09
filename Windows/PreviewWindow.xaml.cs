using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitWebAppSync.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RevitWebAppSync.Windows
{
    /// <summary>
    /// Preview window showing what changes will be made before applying them
    /// </summary>
    public partial class PreviewWindow : Window
    {
        private readonly ExecutionPreview _preview;
        private readonly UIDocument _uidoc;

        /// <summary>
        /// Whether the user approved the changes
        /// </summary>
        public bool Approved { get; private set; }

        public PreviewWindow(ExecutionPreview preview, UIDocument uidoc)
        {
            InitializeComponent();

            _preview = preview;
            _uidoc = uidoc;
            Approved = false;

            PopulatePreview();
        }

        private void PopulatePreview()
        {
            // Set explanation text
            if (!string.IsNullOrEmpty(_preview.Explanation))
            {
                ExplanationText.Text = _preview.Explanation;
            }
            else if (!string.IsNullOrEmpty(_preview.ExecutionMessage))
            {
                ExplanationText.Text = _preview.ExecutionMessage;
            }
            else
            {
                ExplanationText.Visibility = Visibility.Collapsed;
            }

            // Set risk indicator
            SetRiskIndicator(_preview.Risk);

            // Set summary
            SummaryText.Text = _preview.Summary;
            PopulateSummaryBadges();

            // Populate change sections
            PopulateDeletedSection();
            PopulateModifiedSection();
            PopulateCreatedSection();
            PopulateSelectedSection();

            // Show no changes message if needed
            if (_preview.TotalAffected == 0)
            {
                NoChangesText.Visibility = Visibility.Visible;
            }

            // Show highlight button if there are elements to highlight
            if (_preview.TotalAffected > 0 && !_preview.IsReadOnly)
            {
                HighlightButton.Visibility = Visibility.Visible;
            }

            // Set apply button style based on risk
            if (_preview.Risk == RiskLevel.High)
            {
                ApplyButton.Style = (Style)FindResource("DangerButton");
                ApplyButton.Content = "Delete Elements";
                WarningText.Text = "Warning: This action will permanently delete elements.";
                WarningText.Visibility = Visibility.Visible;
            }
            else if (_preview.IsReadOnly)
            {
                ApplyButton.Style = (Style)FindResource("PrimaryButton");
                ApplyButton.Content = "Continue";
            }
            else
            {
                ApplyButton.Style = (Style)FindResource("PrimaryButton");
                ApplyButton.Content = "Apply Changes";
            }
        }

        private void SetRiskIndicator(RiskLevel risk)
        {
            switch (risk)
            {
                case RiskLevel.High:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    RiskText.Text = "HIGH RISK";
                    RiskText.Foreground = Brushes.White;
                    break;
                case RiskLevel.Medium:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    RiskText.Text = "MEDIUM RISK";
                    RiskText.Foreground = Brushes.White;
                    break;
                case RiskLevel.Low:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                    RiskText.Text = "LOW RISK";
                    RiskText.Foreground = Brushes.White;
                    break;
                default:
                    RiskBadge.Background = new SolidColorBrush(Color.FromRgb(33, 150, 243));
                    RiskText.Text = "SAFE";
                    RiskText.Foreground = Brushes.White;
                    break;
            }
        }

        private void PopulateSummaryBadges()
        {
            SummaryDetails.Children.Clear();

            if (_preview.DeletedElements.Count > 0)
            {
                AddSummaryBadge($"{_preview.DeletedElements.Count} deleted", "#F44336");
            }
            if (_preview.ModifiedElements.Count > 0)
            {
                AddSummaryBadge($"{_preview.ModifiedElements.Count} modified", "#FF9800");
            }
            if (_preview.CreatedElements.Count > 0)
            {
                AddSummaryBadge($"{_preview.CreatedElements.Count} created", "#4CAF50");
            }
            if (_preview.SelectedElements.Count > 0)
            {
                AddSummaryBadge($"{_preview.SelectedElements.Count} selected", "#2196F3");
            }
        }

        private void AddSummaryBadge(string text, string hexColor)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 0)
            };

            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };

            border.Child = textBlock;
            SummaryDetails.Children.Add(border);
        }

        private void PopulateDeletedSection()
        {
            if (_preview.DeletedElements.Count == 0) return;

            DeletedSection.Visibility = Visibility.Visible;

            foreach (var element in _preview.DeletedElements.Take(50)) // Limit to 50 items
            {
                AddChangeItem(DeletedList, element, "#F44336");
            }

            if (_preview.DeletedElements.Count > 50)
            {
                AddMoreIndicator(DeletedList, _preview.DeletedElements.Count - 50);
            }
        }

        private void PopulateModifiedSection()
        {
            if (_preview.ModifiedElements.Count == 0) return;

            ModifiedSection.Visibility = Visibility.Visible;

            foreach (var element in _preview.ModifiedElements.Take(50))
            {
                AddChangeItem(ModifiedList, element, "#FF9800", showDetails: true);
            }

            if (_preview.ModifiedElements.Count > 50)
            {
                AddMoreIndicator(ModifiedList, _preview.ModifiedElements.Count - 50);
            }
        }

        private void PopulateCreatedSection()
        {
            if (_preview.CreatedElements.Count == 0) return;

            CreatedSection.Visibility = Visibility.Visible;

            foreach (var element in _preview.CreatedElements.Take(50))
            {
                AddChangeItem(CreatedList, element, "#4CAF50");
            }

            if (_preview.CreatedElements.Count > 50)
            {
                AddMoreIndicator(CreatedList, _preview.CreatedElements.Count - 50);
            }
        }

        private void PopulateSelectedSection()
        {
            if (_preview.SelectedElements.Count == 0) return;

            SelectedSection.Visibility = Visibility.Visible;

            foreach (var element in _preview.SelectedElements.Take(50))
            {
                AddChangeItem(SelectedList, element, "#2196F3");
            }

            if (_preview.SelectedElements.Count > 50)
            {
                AddMoreIndicator(SelectedList, _preview.SelectedElements.Count - 50);
            }
        }

        private void AddChangeItem(StackPanel parent, ElementChange element, string hexColor, bool showDetails = false)
        {
            var color = (Color)ColorConverter.ConvertFromString(hexColor);

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 4),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
                BorderThickness = new Thickness(0, 0, 0, 2)
            };

            var stack = new StackPanel();

            // Main info row
            var mainRow = new StackPanel { Orientation = Orientation.Horizontal };

            // Category badge
            var categoryBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 8, 0)
            };
            categoryBadge.Child = new TextBlock
            {
                Text = element.Category,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                FontSize = 11
            };
            mainRow.Children.Add(categoryBadge);

            // Element name
            mainRow.Children.Add(new TextBlock
            {
                Text = element.ElementName,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            // Level info
            if (!string.IsNullOrEmpty(element.Level))
            {
                mainRow.Children.Add(new TextBlock
                {
                    Text = $" ({element.Level})",
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            stack.Children.Add(mainRow);

            // Parameter changes (for modified elements)
            if (showDetails && element.ParameterChanges?.Count > 0)
            {
                var detailsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

                foreach (var paramChange in element.ParameterChanges.Take(3))
                {
                    var changeRow = new StackPanel { Orientation = Orientation.Horizontal };

                    changeRow.Children.Add(new TextBlock
                    {
                        Text = $"{paramChange.ParameterName}: ",
                        Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                        FontSize = 11
                    });

                    changeRow.Children.Add(new TextBlock
                    {
                        Text = paramChange.BeforeValue,
                        Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                        FontSize = 11,
                        TextDecorations = TextDecorations.Strikethrough
                    });

                    changeRow.Children.Add(new TextBlock
                    {
                        Text = " -> ",
                        Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                        FontSize = 11
                    });

                    changeRow.Children.Add(new TextBlock
                    {
                        Text = paramChange.AfterValue,
                        Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80)),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold
                    });

                    detailsPanel.Children.Add(changeRow);
                }

                if (element.ParameterChanges.Count > 3)
                {
                    detailsPanel.Children.Add(new TextBlock
                    {
                        Text = $"...and {element.ParameterChanges.Count - 3} more parameter(s)",
                        Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                        FontSize = 11,
                        FontStyle = FontStyles.Italic
                    });
                }

                stack.Children.Add(detailsPanel);
            }

            border.Child = stack;
            parent.Children.Add(border);
        }

        private void AddMoreIndicator(StackPanel parent, int remainingCount)
        {
            parent.Children.Add(new TextBlock
            {
                Text = $"...and {remainingCount} more element(s)",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(12, 4, 0, 0)
            });
        }

        private void HighlightButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Collect element IDs to highlight
                var elementIds = new List<ElementId>();

                foreach (var change in _preview.Changes)
                {
                    if (change.ChangeType != ChangeType.Deleted) // Can't highlight deleted elements
                    {
                        elementIds.Add(new ElementId(change.ElementId));
                    }
                }

                if (elementIds.Count > 0)
                {
                    // Select elements in Revit (this will highlight them)
                    _uidoc.Selection.SetElementIds(elementIds);

                    // Try to zoom to selection
                    try
                    {
                        _uidoc.ShowElements(elementIds);
                    }
                    catch
                    {
                        // ShowElements may fail if elements are in different views
                    }

                    MessageBox.Show(
                        $"{elementIds.Count} element(s) have been selected and highlighted in the model.",
                        "Elements Highlighted",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch
            {
                MessageBox.Show(
                    "Could not highlight elements. Some elements may not exist in the current view.",
                    "Highlight Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            Approved = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Approved = false;
            DialogResult = false;
            Close();
        }
    }
}
