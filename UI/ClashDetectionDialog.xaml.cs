using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        private ObservableCollection<RevitLinkedFileInfo> _linkedFiles;
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
        /// List of linked files in the document
        /// </summary>
        public ObservableCollection<RevitLinkedFileInfo> LinkedFiles => _linkedFiles;

        /// <summary>
        /// List of selected linked files for clash detection
        /// </summary>
        public List<RevitLinkedFileInfo> SelectedLinkedFiles => _linkedFiles?.Where(lf => lf.IsSelected).ToList() ?? new List<RevitLinkedFileInfo>();

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
            _linkedFiles = new ObservableCollection<RevitLinkedFileInfo>();
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

            // Load linked files from the document
            LoadLinkedFiles();
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

        /// <summary>
        /// Loads linked files from the current document
        /// </summary>
        private void LoadLinkedFiles()
        {
            try
            {
                _linkedFiles.Clear();

                // Get all RevitLinkInstances in the document
                var linkInstances = new FilteredElementCollector(_currentDocument)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                if (linkInstances.Count == 0)
                {
                    // No linked files found
                    LinkedFilesListBox.ItemsSource = null;
                    MessageBox.Show(
                        "No linked Revit files found in the current document.\n\nPlease link Revit files first using 'Insert > Link Revit' before running clash detection.",
                        "No Linked Files",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                foreach (var linkInstance in linkInstances)
                {
                    var linkDoc = linkInstance.GetLinkDocument();
                    var linkType = _currentDocument.GetElement(linkInstance.GetTypeId()) as RevitLinkType;

                    string fileName = "Unknown";
                    string filePath = "";

                    if (linkType != null)
                    {
                        var externalRef = linkType.GetExternalFileReference();
                        if (externalRef != null)
                        {
                            filePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(externalRef.GetAbsolutePath());
                            fileName = System.IO.Path.GetFileName(filePath);
                        }
                    }

                    if (string.IsNullOrEmpty(fileName) || fileName == "Unknown")
                    {
                        fileName = linkInstance.Name;
                    }

                    var linkedFileInfo = new RevitLinkedFileInfo
                    {
                        LinkInstance = linkInstance,
                        LinkedDocument = linkDoc,
                        LinkInstanceId = linkInstance.Id,
                        FileName = fileName,
                        FilePath = filePath,
                        DisplayName = $"{fileName} {(linkDoc != null ? "(Loaded)" : "(Not Loaded)")}",
                        IsLoaded = linkDoc != null,
                        IsSelected = false,
                        LinkTransform = linkInstance.GetTotalTransform(),
                        ElementCount = linkDoc != null ? new FilteredElementCollector(linkDoc).WhereElementIsNotElementType().GetElementCount() : 0
                    };

                    _linkedFiles.Add(linkedFileInfo);
                }

                LinkedFilesListBox.ItemsSource = _linkedFiles;
                UpdateUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load linked files: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        #endregion

        #region Linked Files Event Handlers

        /// <summary>
        /// Handle linked file checkbox change
        /// </summary>
        private void LinkedFileCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            OnLinkedFilesSelectionChanged();
        }

        /// <summary>
        /// Handle linked files list selection change
        /// </summary>
        private void LinkedFilesListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selection changed in list box - no action needed as we use checkboxes
        }

        /// <summary>
        /// Select all linked files
        /// </summary>
        private void SelectAllLinksButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var linkedFile in _linkedFiles)
            {
                if (linkedFile.IsLoaded)
                {
                    linkedFile.IsSelected = true;
                }
            }
            LinkedFilesListBox.Items.Refresh();
            OnLinkedFilesSelectionChanged();
        }

        /// <summary>
        /// Clear all linked file selections
        /// </summary>
        private void ClearAllLinksButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var linkedFile in _linkedFiles)
            {
                linkedFile.IsSelected = false;
            }
            LinkedFilesListBox.Items.Refresh();
            OnLinkedFilesSelectionChanged();
        }

        /// <summary>
        /// Refresh the linked files list
        /// </summary>
        private void RefreshLinksButton_Click(object sender, RoutedEventArgs e)
        {
            LoadLinkedFiles();
        }

        /// <summary>
        /// Called when linked files selection changes
        /// Loads categories from selected linked files
        /// </summary>
        private void OnLinkedFilesSelectionChanged()
        {
            try
            {
                var selectedLinks = SelectedLinkedFiles;

                if (selectedLinks.Count == 0)
                {
                    // No links selected, hide categories
                    SetBPlaceholderText.Visibility = System.Windows.Visibility.Visible;
                    SetBCategoryTreeView.Visibility = System.Windows.Visibility.Collapsed;
                    _categoriesLoaded = false;
                    SetBStatusText.Text = "Selected: 0 elements";
                    UpdateUI();
                    return;
                }

                // Get categories from all selected linked documents
                var allCategories = new Dictionary<string, CategoryInfo>();

                foreach (var linkedFile in selectedLinks)
                {
                    if (linkedFile.LinkedDocument != null)
                    {
                        var categories = _filterService.GetAllCategories(linkedFile.LinkedDocument);
                        foreach (var cat in categories)
                        {
                            if (allCategories.ContainsKey(cat.Name))
                            {
                                // Add element counts
                                allCategories[cat.Name].ElementCount += cat.ElementCount;
                            }
                            else
                            {
                                allCategories[cat.Name] = new CategoryInfo
                                {
                                    Name = cat.Name,
                                    ElementCount = cat.ElementCount,
                                    DisciplineGroup = cat.DisciplineGroup
                                };
                            }
                        }
                    }
                }

                var categoryList = allCategories.Values.OrderBy(c => c.DisciplineGroup).ThenBy(c => c.Name).ToList();
                PopulateCategoryTree(SetBCategoryTreeView, categoryList, false);

                // Show category tree, hide placeholder
                SetBPlaceholderText.Visibility = System.Windows.Visibility.Collapsed;
                SetBCategoryTreeView.Visibility = System.Windows.Visibility.Visible;

                // Load Set B filters from first selected linked document
                LoadSetBFiltersFromLinks();

                _categoriesLoaded = true;
                UpdateUI();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load categories from linked files: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Loads filter options for Set B from linked files
        /// </summary>
        private void LoadSetBFiltersFromLinks()
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

            // If unchecking "Use Current Selection" and no categories selected, show hint
            if (!SetA.UseCurrentSelection && GetSelectedCategories(SetACategoryTreeView).Count == 0)
            {
                SetAStatusText.Text = "Please select categories or click 'Select All'";
            }

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
                        Margin = new Thickness(2),
                        Focusable = true
                    };

                    // Attach event handler
                    if (isSetA)
                    {
                        checkBox.Checked += (s, e) => { e.Handled = true; UpdateSetASelection(); };
                        checkBox.Unchecked += (s, e) => { e.Handled = true; UpdateSetASelection(); };
                    }
                    else
                    {
                        checkBox.Checked += (s, e) => { e.Handled = true; UpdateSetBSelection(); };
                        checkBox.Unchecked += (s, e) => { e.Handled = true; UpdateSetBSelection(); };
                    }

                    var categoryItem = new TreeViewItem
                    {
                        Header = checkBox,
                        Focusable = false  // Prevent TreeViewItem from stealing focus/clicks
                    };

                    // Handle click on TreeViewItem to toggle checkbox
                    categoryItem.PreviewMouseLeftButtonDown += (s, e) =>
                    {
                        if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton)
                            return; // Let checkbox handle its own clicks

                        checkBox.IsChecked = !checkBox.IsChecked;
                        e.Handled = true;
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

            if (SelectedLinkedFiles.Count == 0)
            {
                MessageBox.Show(
                    "Please select at least one linked file.",
                    "No Linked Files Selected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Warning for large element sets - can cause performance issues
            int totalSetAElements = SetA.TotalElementCount;
            int totalSetBElements = SetB.TotalElementCount;
            int estimatedOperations = totalSetBElements; // We process each Set B element

            if (totalSetAElements > 1000 || totalSetBElements > 500)
            {
                var result = MessageBox.Show(
                    $"Warning: Large element sets selected!\n\n" +
                    $"Set A: {totalSetAElements:N0} elements\n" +
                    $"Set B: {totalSetBElements:N0} elements\n\n" +
                    $"This may take a long time and could cause Revit to become unresponsive.\n\n" +
                    $"Recommendations:\n" +
                    $"• Select specific categories instead of 'All'\n" +
                    $"• Filter by Level to reduce elements\n" +
                    $"• Use preset buttons (Arch vs MEP, etc.)\n\n" +
                    $"Continue anyway?",
                    "Performance Warning",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
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
            // Enable/disable "Run" button
            bool hasSelectedLinks = SelectedLinkedFiles.Count > 0;
            bool canRun = hasSelectedLinks &&
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
            LoadingOverlay.Visibility = show ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
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
