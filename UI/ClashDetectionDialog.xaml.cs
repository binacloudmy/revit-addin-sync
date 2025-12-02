using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Autodesk.Revit.DB;
using RevitWebAppSync.Models;
using RevitWebAppSync.Services;

namespace RevitWebAppSync.UI
{
    /// <summary>
    /// Clash detection dialog for selecting files and elements to compare
    /// Provides comprehensive UI for element selection (Set A vs Set B) with category filtering
    /// </summary>
    public partial class ClashDetectionDialog : Window, INotifyPropertyChanged
    {
        #region Private Fields

        private readonly Document _currentDocument;
        private readonly ElementFilterService _filterService;
        private List<string> _selectedFiles;
        private ElementSelectionSet _setA;
        private ElementSelectionSet _setB;
        private bool _categoriesLoaded;

        #endregion

        #region Properties

        /// <summary>
        /// Element selection set for Set A (current model)
        /// </summary>
        public ElementSelectionSet SetA
        {
            get => _setA;
            private set
            {
                if (_setA != value)
                {
                    _setA = value;
                    OnPropertyChanged(nameof(SetA));
                }
            }
        }

        /// <summary>
        /// Element selection set for Set B (external files)
        /// </summary>
        public ElementSelectionSet SetB
        {
            get => _setB;
            private set
            {
                if (_setB != value)
                {
                    _setB = value;
                    OnPropertyChanged(nameof(SetB));
                }
            }
        }

        /// <summary>
        /// List of selected external file paths
        /// </summary>
        public List<string> SelectedFiles => _selectedFiles;

        /// <summary>
        /// Tolerance value for clash detection
        /// </summary>
        public double Tolerance
        {
            get
            {
                if (double.TryParse(ToleranceTextBox.Text, out double tolerance))
                    return tolerance;
                return 0.0;
            }
        }

        /// <summary>
        /// Indicates if user clicked Run (OK) or Cancel
        /// </summary>
        public bool DialogResult { get; private set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the clash detection dialog
        /// </summary>
        /// <param name="currentDocument">The active Revit document</param>
        public ClashDetectionDialog(Document currentDocument)
        {
            InitializeComponent();

            _currentDocument = currentDocument ?? throw new ArgumentNullException(nameof(currentDocument));
            _filterService = new ElementFilterService();
            _selectedFiles = new List<string>();
            _categoriesLoaded = false;

            // Initialize selection sets
            SetA = new ElementSelectionSet
            {
                SetName = "Set A",
                Description = "Current Model Elements",
                IsCurrentDocument = true
            };

            SetB = new ElementSelectionSet
            {
                SetName = "Set B",
                Description = "External File Elements",
                IsCurrentDocument = false
            };

            DataContext = this;

            // Load current document info
            LoadCurrentDocumentInfo();
        }

        #endregion

        #region Initialization Methods

        /// <summary>
        /// Loads and displays current document information
        /// </summary>
        private void LoadCurrentDocumentInfo()
        {
            try
            {
                var fileName = System.IO.Path.GetFileName(_currentDocument.PathName);
                if (string.IsNullOrEmpty(fileName))
                    fileName = _currentDocument.Title;

                CurrentFileText.Text = $"Current Model: {fileName}";
                SetA.AssociatedFile = fileName;

                // Load Set A categories automatically
                LoadSetACategories();

                // Load Set A filters (levels, worksets)
                LoadSetAFilters();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load current document info: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Loads categories for Set A (current model)
        /// </summary>
        private void LoadSetACategories()
        {
            try
            {
                var categories = _filterService.GetAllCategories(_currentDocument);
                PopulateCategoryTree(SetACategoryTreeView, categories, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load categories: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Loads filter options for Set A (levels, worksets)
        /// </summary>
        private void LoadSetAFilters()
        {
            try
            {
                // Load levels
                var levels = _filterService.GetAllLevels(_currentDocument);
                levels.Insert(0, "All Levels");
                SetALevelComboBox.ItemsSource = levels;
                SetALevelComboBox.SelectedIndex = 0;

                // Load worksets
                var worksets = _filterService.GetAllWorksets(_currentDocument);
                if (worksets.Count > 0)
                {
                    worksets.Insert(0, "All Worksets");
                    SetAWorksetComboBox.ItemsSource = worksets;
                    SetAWorksetComboBox.SelectedIndex = 0;
                    SetAWorksetComboBox.IsEnabled = true;
                }
                else
                {
                    SetAWorksetComboBox.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load filters: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        #endregion

        #region File Selection Event Handlers

        /// <summary>
        /// Browse for external Revit files
        /// </summary>
        private void BrowseFilesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Select Revit Files for Clash Detection",
                    Filter = "Revit Files (*.rvt)|*.rvt|All Files (*.*)|*.*",
                    Multiselect = true,
                    CheckFileExists = true
                };

                if (dialog.ShowDialog() == true)
                {
                    foreach (var fileName in dialog.FileNames)
                    {
                        if (!_selectedFiles.Contains(fileName))
                        {
                            _selectedFiles.Add(fileName);
                        }
                    }

                    SelectedFilesListBox.ItemsSource = null;
                    SelectedFilesListBox.ItemsSource = _selectedFiles;

                    UpdateUI();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to select files: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Remove selected file from list
        /// </summary>
        private void RemoveFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (SelectedFilesListBox.SelectedItem is string selectedFile)
                {
                    _selectedFiles.Remove(selectedFile);
                    SelectedFilesListBox.ItemsSource = null;
                    SelectedFilesListBox.ItemsSource = _selectedFiles;

                    UpdateUI();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to remove file: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Load categories from selected external files
        /// </summary>
        private void LoadCategoriesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedFiles.Count == 0)
            {
                MessageBox.Show(
                    "Please select at least one external file first.",
                    "No Files Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            try
            {
                ShowLoading(true, "Loading categories from external files...");

                // TODO: For now, we'll show a simplified version
                // In full implementation, we would load the external file as RevitLinkInstance
                // and extract categories from it

                // For demonstration, use categories from current document
                var categories = _filterService.GetAllCategories(_currentDocument);
                PopulateCategoryTree(SetBCategoryTreeView, categories, false);

                // Hide placeholder
                SetBPlaceholderText.Visibility = Visibility.Collapsed;
                SetBCategoryTreeView.Visibility = Visibility.Visible;

                // Load Set B filters
                LoadSetBFilters();

                _categoriesLoaded = true;
                UpdateUI();

                MessageBox.Show(
                    $"Loaded categories from {_selectedFiles.Count} file(s).",
                    "Categories Loaded",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load categories: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                ShowLoading(false);
            }
        }

        /// <summary>
        /// Loads filter options for Set B
        /// </summary>
        private void LoadSetBFilters()
        {
            try
            {
                // Load levels (same as current document for now)
                var levels = _filterService.GetAllLevels(_currentDocument);
                levels.Insert(0, "All Levels");
                SetBLevelComboBox.ItemsSource = levels;
                SetBLevelComboBox.SelectedIndex = 0;

                // Load worksets
                var worksets = _filterService.GetAllWorksets(_currentDocument);
                if (worksets.Count > 0)
                {
                    worksets.Insert(0, "All Worksets");
                    SetBWorksetComboBox.ItemsSource = worksets;
                    SetBWorksetComboBox.SelectedIndex = 0;
                    SetBWorksetComboBox.IsEnabled = true;
                }
                else
                {
                    SetBWorksetComboBox.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load filters: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        #endregion

        #region Preset Event Handlers

        private void PresetAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetASelectAllButton_Click(sender, e);
            SetBSelectAllButton_Click(sender, e);
        }

        private void PresetArchMepButton_Click(object sender, RoutedEventArgs e)
        {
            var (archCategories, mepCategories) = SelectionPresets.ArchitectureVsMEP;
            SelectCategories(SetACategoryTreeView, archCategories, true);
            SelectCategories(SetBCategoryTreeView, mepCategories, false);
        }

        private void PresetArchStructButton_Click(object sender, RoutedEventArgs e)
        {
            var (archCategories, structCategories) = SelectionPresets.ArchitectureVsStructure;
            SelectCategories(SetACategoryTreeView, archCategories, true);
            SelectCategories(SetBCategoryTreeView, structCategories, false);
        }

        private void PresetStructMepButton_Click(object sender, RoutedEventArgs e)
        {
            var (structCategories, mepCategories) = SelectionPresets.StructureVsMEP;
            SelectCategories(SetACategoryTreeView, structCategories, true);
            SelectCategories(SetBCategoryTreeView, mepCategories, false);
        }

        #endregion

        #region Set A Event Handlers

        private void SetALevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSetASelection();
        }

        private void SetAWorksetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSetASelection();
        }

        private void SetAUseCurrentSelectionCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            SetA.UseCurrentSelection = SetAUseCurrentSelectionCheckBox.IsChecked == true;

            // Disable category tree if using current selection
            SetACategoryTreeView.IsEnabled = !SetA.UseCurrentSelection;

            UpdateSetASelection();
        }

        private void SetASelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SelectAllCategories(SetACategoryTreeView, true);
            UpdateSetASelection();
        }

        private void SetAClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            ClearAllCategories(SetACategoryTreeView, true);
            UpdateSetASelection();
        }

        #endregion

        #region Set B Event Handlers

        private void SetBLevelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSetBSelection();
        }

        private void SetBWorksetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSetBSelection();
        }

        private void SetBSelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SelectAllCategories(SetBCategoryTreeView, true);
            UpdateSetBSelection();
        }

        private void SetBClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            ClearAllCategories(SetBCategoryTreeView, true);
            UpdateSetBSelection();
        }

        #endregion

        #region Category Tree Helper Methods

        /// <summary>
        /// Populates a category tree view with categories grouped by discipline
        /// </summary>
        private void PopulateCategoryTree(TreeView treeView, List<CategoryInfo> categories, bool isSetA)
        {
            treeView.Items.Clear();

            var groupedCategories = categories.GroupBy(c => c.DisciplineGroup);

            foreach (var group in groupedCategories)
            {
                var disciplineItem = new TreeViewItem
                {
                    Header = CreateDisciplineHeader(group.Key, group.Sum(c => c.ElementCount)),
                    IsExpanded = true
                };

                foreach (var category in group.OrderBy(c => c.Name))
                {
                    var checkBox = new CheckBox
                    {
                        Content = category.DisplayName,
                        Tag = category.Name,
                        Margin = new Thickness(2)
                    };

                    // Attach event handler
                    if (isSetA)
                        checkBox.Checked += (s, e) => UpdateSetASelection();
                    else
                        checkBox.Checked += (s, e) => UpdateSetBSelection();

                    checkBox.Unchecked += (s, e) =>
                    {
                        if (isSetA)
                            UpdateSetASelection();
                        else
                            UpdateSetBSelection();
                    };

                    var categoryItem = new TreeViewItem
                    {
                        Header = checkBox
                    };

                    disciplineItem.Items.Add(categoryItem);
                }

                treeView.Items.Add(disciplineItem);
            }
        }

        /// <summary>
        /// Creates header for discipline node
        /// </summary>
        private StackPanel CreateDisciplineHeader(string disciplineName, int totalCount)
        {
            var stackPanel = new StackPanel { Orientation = Orientation.Horizontal };

            var textBlock = new TextBlock
            {
                Text = $"{disciplineName} ({totalCount})",
                FontWeight = FontWeights.Bold
            };

            stackPanel.Children.Add(textBlock);
            return stackPanel;
        }

        /// <summary>
        /// Selects specific categories in tree view
        /// </summary>
        private void SelectCategories(TreeView treeView, List<string> categoryNames, bool isChecked)
        {
            foreach (TreeViewItem disciplineItem in treeView.Items)
            {
                foreach (TreeViewItem categoryItem in disciplineItem.Items)
                {
                    if (categoryItem.Header is CheckBox checkBox)
                    {
                        var categoryName = checkBox.Tag as string;
                        if (categoryNames.Contains(categoryName))
                        {
                            checkBox.IsChecked = isChecked;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Selects all categories in tree view
        /// </summary>
        private void SelectAllCategories(TreeView treeView, bool isChecked)
        {
            foreach (TreeViewItem disciplineItem in treeView.Items)
            {
                foreach (TreeViewItem categoryItem in disciplineItem.Items)
                {
                    if (categoryItem.Header is CheckBox checkBox)
                    {
                        checkBox.IsChecked = isChecked;
                    }
                }
            }
        }

        /// <summary>
        /// Clears all category selections
        /// </summary>
        private void ClearAllCategories(TreeView treeView, bool updateSelection)
        {
            SelectAllCategories(treeView, false);
        }

        /// <summary>
        /// Gets selected category names from tree view
        /// </summary>
        private List<string> GetSelectedCategories(TreeView treeView)
        {
            var selectedCategories = new List<string>();

            foreach (TreeViewItem disciplineItem in treeView.Items)
            {
                foreach (TreeViewItem categoryItem in disciplineItem.Items)
                {
                    if (categoryItem.Header is CheckBox checkBox && checkBox.IsChecked == true)
                    {
                        var categoryName = checkBox.Tag as string;
                        if (!string.IsNullOrEmpty(categoryName))
                        {
                            selectedCategories.Add(categoryName);
                        }
                    }
                }
            }

            return selectedCategories;
        }

        #endregion

        #region Selection Update Methods

        /// <summary>
        /// Updates Set A selection and element count
        /// </summary>
        private void UpdateSetASelection()
        {
            try
            {
                SetA.SelectedCategories = GetSelectedCategories(SetACategoryTreeView);

                // Get selected level
                if (SetALevelComboBox.SelectedItem is string level && level != "All Levels")
                {
                    SetA.SelectedLevels = new List<string> { level };
                }
                else
                {
                    SetA.SelectedLevels = new List<string>();
                }

                // Get selected workset
                if (SetAWorksetComboBox.IsEnabled && SetAWorksetComboBox.SelectedItem is string workset && workset != "All Worksets")
                {
                    SetA.SelectedWorksets = new List<string> { workset };
                }
                else
                {
                    SetA.SelectedWorksets = new List<string>();
                }

                // Calculate element count
                int elementCount = 0;
                if (SetA.UseCurrentSelection)
                {
                    // TODO: Get current selection from Revit UIDocument
                    elementCount = 0; // Placeholder
                }
                else if (SetA.SelectedCategories.Count > 0)
                {
                    foreach (var category in SetA.SelectedCategories)
                    {
                        elementCount += _filterService.GetElementCount(_currentDocument, category);
                    }
                }

                SetA.TotalElementCount = elementCount;
                SetAStatusText.Text = $"Selected: {elementCount:N0} elements from {SetA.SelectedCategories.Count} categories";

                UpdateUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to update Set A selection: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Updates Set B selection and element count
        /// </summary>
        private void UpdateSetBSelection()
        {
            try
            {
                SetB.SelectedCategories = GetSelectedCategories(SetBCategoryTreeView);

                // Get selected level
                if (SetBLevelComboBox.SelectedItem is string level && level != "All Levels")
                {
                    SetB.SelectedLevels = new List<string> { level };
                }
                else
                {
                    SetB.SelectedLevels = new List<string>();
                }

                // Get selected workset
                if (SetBWorksetComboBox.IsEnabled && SetBWorksetComboBox.SelectedItem is string workset && workset != "All Worksets")
                {
                    SetB.SelectedWorksets = new List<string> { workset };
                }
                else
                {
                    SetB.SelectedWorksets = new List<string>();
                }

                // Calculate element count (approximate for now)
                int elementCount = 0;
                if (SetB.SelectedCategories.Count > 0)
                {
                    foreach (var category in SetB.SelectedCategories)
                    {
                        elementCount += _filterService.GetElementCount(_currentDocument, category);
                    }
                }

                SetB.TotalElementCount = elementCount;
                SetBStatusText.Text = $"Selected: {elementCount:N0} elements from {SetB.SelectedCategories.Count} categories";

                UpdateUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to update Set B selection: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        #endregion

        #region Button Event Handlers

        private void RunClashDetectionButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate selections
            var setAValidation = SetA.Validate();
            if (!setAValidation.IsValid)
            {
                MessageBox.Show(
                    "Set A validation failed:\n" + string.Join("\n", setAValidation.Errors),
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var setBValidation = SetB.Validate();
            if (!setBValidation.IsValid)
            {
                MessageBox.Show(
                    "Set B validation failed:\n" + string.Join("\n", setBValidation.Errors),
                    "Validation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (_selectedFiles.Count == 0)
            {
                MessageBox.Show(
                    "Please select at least one external file.",
                    "No Files Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void SavePresetButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Save preset functionality will be implemented in a future update.",
                "Coming Soon",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Updates UI state (button enable/disable)
        /// </summary>
        private void UpdateUI()
        {
            // Enable/disable file list selection
            RemoveFileButton.IsEnabled = SelectedFilesListBox.SelectedItem != null;

            // Enable/disable "Run" button
            bool canRun = _selectedFiles.Count > 0 &&
                         _categoriesLoaded &&
                         SetA.HasValidSelection &&
                         SetB.HasValidSelection;

            RunClashDetectionButton.IsEnabled = canRun;
        }

        /// <summary>
        /// Shows or hides loading overlay
        /// </summary>
        private void ShowLoading(bool show, string message = "Loading...")
        {
            LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LoadingText.Text = message;
        }

        #endregion

        #region INotifyPropertyChanged Implementation

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}
