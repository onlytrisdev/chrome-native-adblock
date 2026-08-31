using System.Text;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ChromeNativeAdblock.Launcher;
namespace ChromeNativeAdblock.Gui;

public sealed partial class MainPage : Page
{
    public static MainPage? Current { get; private set; }

    private readonly FilterManager _filterManager = new();

    // Category UI Controls
    private readonly Dictionary<string, CheckBox> _categoryCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _categoryTitleTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _categoryDescTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _categoryBadgeTexts = new(StringComparer.OrdinalIgnoreCase);

    // SubGroup UI Controls
    private readonly Dictionary<string, CheckBox> _subGroupCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _subGroupTitleTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _subGroupBadgeTexts = new(StringComparer.OrdinalIgnoreCase);

    // Filter Item UI Controls
    private readonly Dictionary<string, CheckBox> _filterCheckBoxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _filterNameTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _filterDescTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> _filterBadgeTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> _filterBadgeBorders = new(StringComparer.OrdinalIgnoreCase);
    private bool _isUpdatingFilterUi;
    private static readonly SolidColorBrush FilterItemHoverBrush = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x36));
    private static readonly SolidColorBrush FilterItemNormalBrush = new(Windows.UI.Color.FromArgb(255, 0x1A, 0x1D, 0x24));

    // Preset UI Brushes
    private static readonly SolidColorBrush PresetActiveBgBrush = new(Windows.UI.Color.FromArgb(255, 0x25, 0x63, 0xEB));
    private static readonly SolidColorBrush PresetActiveBorderBrush = new(Windows.UI.Color.FromArgb(255, 0x60, 0xA5, 0xFA));
    private static readonly SolidColorBrush PresetActiveTextBrush = new(Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush PresetActiveSubTextBrush = new(Windows.UI.Color.FromArgb(255, 0xBF, 0xDB, 0xFE));

    private static readonly SolidColorBrush PresetNormalBgBrush = new(Windows.UI.Color.FromArgb(255, 0x1A, 0x1D, 0x24));
    private static readonly SolidColorBrush PresetNormalBorderBrush = new(Windows.UI.Color.FromArgb(255, 0x2E, 0x34, 0x40));
    private static readonly SolidColorBrush PresetNormalTextBrush = new(Windows.UI.Color.FromArgb(255, 0xE2, 0xE8, 0xF0));
    private static readonly SolidColorBrush PresetNormalIconBrush = new(Windows.UI.Color.FromArgb(255, 0x94, 0xA3, 0xB8));
    private static readonly SolidColorBrush PresetNormalSubTextBrush = new(Windows.UI.Color.FromArgb(255, 0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush PresetHoverBgBrush = new(Windows.UI.Color.FromArgb(255, 0x26, 0x2A, 0x36));
    private static bool IsDescendantOf(DependencyObject? child, DependencyObject? parent)
    {
        if (child == null || parent == null) return false;
        var current = child;
        while (current != null)
        {
            if (current == parent) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private ChromeInstallation? _installation;
    private AnalysisReport? _analysis;
    private bool _busy;
    private bool _isRunning;
    private CancellationTokenSource? _cts;

    private long _blockedCount;
    private long _ytBypassCount;
    private readonly StringBuilder _logBuilder = new();

    public bool IsChromeRunning => _isRunning;

    public MainPage()
    {
        Current = this;
        try
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
        }
        catch (Exception ex)
        {
            Program.LogFatal("MainPage.Constructor", ex);
            throw;
        }
    }

    public void LaunchChrome()
    {
        if (!_isRunning && LaunchButton.IsEnabled)
        {
            LaunchButton_Click(this, new RoutedEventArgs());
        }
    }

    public void StopChrome()
    {
        if (_isRunning)
        {
            LaunchButton_Click(this, new RoutedEventArgs());
        }
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            BuildFilterListUi();
            SelectCurrentLanguage();
            ApplyLanguage();
            await AnalyzeAsync();
        }
        catch (Exception ex)
        {
            Program.LogFatal("MainPage.MainPage_Loaded", ex);
            ShowMessage(InfoBarSeverity.Error, "Initialization Error", ex.Message);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning)
        {
            await AnalyzeAsync();
        }
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        MainWindow.Current?.MinimizeToTray();
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            LocalizationService.SetLanguage(lang);
            ApplyLanguage();
        }
    }

    private void FeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateLaunchButtonText();
    }

    #region Filter Management UI

    private void BuildFilterListUi()
    {
        if (FilterCategoriesContainer == null) return;
        FilterCategoriesContainer.Children.Clear();

        _categoryCheckBoxes.Clear();
        _categoryTitleTexts.Clear();
        _categoryDescTexts.Clear();
        _categoryBadgeTexts.Clear();

        _subGroupCheckBoxes.Clear();
        _subGroupTitleTexts.Clear();
        _subGroupBadgeTexts.Clear();

        _filterCheckBoxes.Clear();
        _filterNameTexts.Clear();
        _filterDescTexts.Clear();
        _filterBadgeTexts.Clear();
        _filterBadgeBorders.Clear();

        if (FilterManagementExpander != null)
        {
            FilterManagementExpander.Expanding -= FilterManagementExpander_Expanding;
            FilterManagementExpander.Expanding += FilterManagementExpander_Expanding;
        }
        SetupPresetPills();

        foreach (var category in FilterCatalog.Categories.OrderBy(c => c.DisplayOrder))
        {
            var categoryExpander = new Expander
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = category.Id == FilterCatalog.CategoryBuiltin || category.Id == FilterCatalog.CategoryAds
            };

            // Category Header Grid
            var headerGrid = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ColumnSpacing = 12
            };
            headerGrid.SetHandCursor();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 0: Tri-state CheckBox
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 1: Icon Border
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 2: Title & Desc
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // 3: Live Counter Badge

            // Category Tri-State CheckBox
            var catCheckBox = new CheckBox
            {
                IsThreeState = true,
                Tag = category.Id,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0)
            };
            catCheckBox.Click += CategoryCheckBox_Click;
            Grid.SetColumn(catCheckBox, 0);
            headerGrid.Children.Add(catCheckBox);
            _categoryCheckBoxes[category.Id] = catCheckBox;

            headerGrid.Tapped += (s, e) =>
            {
                if (e.OriginalSource is DependencyObject src && (src == catCheckBox || IsDescendantOf(src, catCheckBox)))
                {
                    return;
                }
                bool newState = catCheckBox.IsChecked != true;
                catCheckBox.IsChecked = newState;
                TriggerCategoryToggle(category.Id, newState);
            };
            // Category Icon
            var iconBorder = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Background = GetCategoryBrush(category.Id)
            };
            var icon = new FontIcon
            {
                Glyph = category.IconGlyph,
                FontSize = 16,
                Foreground = GetCategoryForegroundBrush(category.Id),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconBorder.Child = icon;
            Grid.SetColumn(iconBorder, 1);
            headerGrid.Children.Add(iconBorder);

            // Category Titles
            var titlesStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2
            };
            var titleText = new TextBlock
            {
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = T(category.NameKey)
            };
            var descText = new TextBlock
            {
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Text = T(category.DescriptionKey),
                TextWrapping = TextWrapping.Wrap
            };
            titlesStack.Children.Add(titleText);
            titlesStack.Children.Add(descText);
            Grid.SetColumn(titlesStack, 2);
            headerGrid.Children.Add(titlesStack);

            _categoryTitleTexts[category.Id] = titleText;
            _categoryDescTexts[category.Id] = descText;

            // Summary Counter Badge (e.g. "5/6" or "Đã dùng 5 trên 6")
            var badgeBorder = new Border
            {
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["CardBackgroundBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            };
            var badgeText = new TextBlock
            {
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            badgeBorder.Child = badgeText;
            Grid.SetColumn(badgeBorder, 3);
            headerGrid.Children.Add(badgeBorder);

            _categoryBadgeTexts[category.Id] = badgeText;
            categoryExpander.Header = headerGrid;

            // Category Items Container
            var itemsContainer = new StackPanel
            {
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 4)
            };

            // 1. Sub-groups in this category
            var subGroups = FilterCatalog.GetSubGroupsForCategory(category.Id);
            foreach (var subGroup in subGroups)
            {
                var subGroupExpander = new Expander
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsExpanded = category.Id == FilterCatalog.CategoryBuiltin
                };

                var subHeaderGrid = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ColumnSpacing = 10
                };
                subHeaderGrid.SetHandCursor();
                subHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Checkbox
                subHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Name
                subHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Badge

                var sgCheckBox = new CheckBox
                {
                    IsThreeState = true,
                    Tag = subGroup.Id,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 2, 0)
                };
                sgCheckBox.Click += SubGroupCheckBox_Click;
                Grid.SetColumn(sgCheckBox, 0);
                subHeaderGrid.Children.Add(sgCheckBox);
                _subGroupCheckBoxes[subGroup.Id] = sgCheckBox;

                subHeaderGrid.Tapped += (s, e) =>
                {
                    if (e.OriginalSource is DependencyObject src && (src == sgCheckBox || IsDescendantOf(src, sgCheckBox)))
                    {
                        return;
                    }
                    bool newState = sgCheckBox.IsChecked != true;
                    sgCheckBox.IsChecked = newState;
                    TriggerSubGroupToggle(subGroup.Id, newState);
                };
                var sgTitle = new TextBlock
                {
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Text = T(subGroup.NameKey),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(sgTitle, 1);
                subHeaderGrid.Children.Add(sgTitle);
                _subGroupTitleTexts[subGroup.Id] = sgTitle;

                var sgBadgeBorder = new Border
                {
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)Application.Current.Resources["CardBackgroundBrush"],
                    BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4)
                };
                var sgBadgeText = new TextBlock
                {
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                sgBadgeBorder.Child = sgBadgeText;
                Grid.SetColumn(sgBadgeBorder, 2);
                subHeaderGrid.Children.Add(sgBadgeBorder);
                _subGroupBadgeTexts[subGroup.Id] = sgBadgeText;

                subGroupExpander.Header = subHeaderGrid;

                var subItemsContainer = new StackPanel
                {
                    Spacing = 6,
                    Margin = new Thickness(16, 6, 0, 4)
                };

                var subItems = FilterCatalog.GetItemsForSubGroup(subGroup.Id);
                foreach (var item in subItems)
                {
                    var itemRow = CreateFilterItemRow(item);
                    subItemsContainer.Children.Add(itemRow);
                }

                subGroupExpander.Content = subItemsContainer;
                itemsContainer.Children.Add(subGroupExpander);
            }

            // 2. Standalone items in this category (no SubGroup)
            var standaloneItems = FilterCatalog.Items
                .Where(i => string.Equals(i.CategoryId, category.Id, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(i.SubGroupId))
                .OrderBy(i => i.DisplayOrder);

            foreach (var item in standaloneItems)
            {
                var itemRow = CreateFilterItemRow(item);
                itemsContainer.Children.Add(itemRow);
            }

            categoryExpander.Content = itemsContainer;
            FilterCategoriesContainer.Children.Add(categoryExpander);
        }

        RefreshFilterListUi();
    }

    private Border CreateFilterItemRow(FilterItemDefinition item)
    {
        var itemBorder = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            Background = FilterItemNormalBrush,
            BorderBrush = (Brush)Application.Current.Resources["CardBorderBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        itemBorder.SetHandCursor();
        itemBorder.PointerEntered += (s, e) => itemBorder.Background = FilterItemHoverBrush;
        itemBorder.PointerExited += (s, e) => itemBorder.Background = FilterItemNormalBrush;
        var itemGrid = new Grid
        {
            ColumnSpacing = 12
        };
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // CheckBox
        itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Name & Desc

        var itemCheckBox = new CheckBox
        {
            IsThreeState = false,
            Tag = item.Id,
            IsChecked = _filterManager.IsFilterEnabled(item.Id),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0)
        };
        itemCheckBox.Click += FilterItemCheckBox_Click;
        Grid.SetColumn(itemCheckBox, 0);
        itemGrid.Children.Add(itemCheckBox);
        _filterCheckBoxes[item.Id] = itemCheckBox;

        itemBorder.Tapped += (s, e) =>
        {
            if (e.OriginalSource is DependencyObject src && (src == itemCheckBox || IsDescendantOf(src, itemCheckBox)))
            {
                return;
            }
            itemCheckBox.IsChecked = !itemCheckBox.IsChecked;
            TriggerItemToggle(item.Id, itemCheckBox.IsChecked == true);
        };
        var leftStack = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameText = new TextBlock
        {
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = T(item.NameKey),
            VerticalAlignment = VerticalAlignment.Center
        };
        nameRow.Children.Add(nameText);

        // Rule count badge
        var countBadgeBorder = new Border
        {
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 51, 36)),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        };
        var countBadgeText = new TextBlock
        {
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        countBadgeBorder.Child = countBadgeText;
        nameRow.Children.Add(countBadgeBorder);

        _filterBadgeBorders[item.Id] = countBadgeBorder;
        _filterBadgeTexts[item.Id] = countBadgeText;
        UpdateFilterBadge(item.Id);
        leftStack.Children.Add(nameRow);

        var itemDesc = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Text = T(item.DescriptionKey),
            TextWrapping = TextWrapping.Wrap
        };
        leftStack.Children.Add(itemDesc);

        Grid.SetColumn(leftStack, 1);
        itemGrid.Children.Add(leftStack);

        _filterNameTexts[item.Id] = nameText;
        _filterDescTexts[item.Id] = itemDesc;


        itemBorder.Child = itemGrid;
        return itemBorder;
    }

    private void RefreshFilterListUi()
    {
        _isUpdatingFilterUi = true;
        try
        {
            if (FilterPanelHeaderText != null) FilterPanelHeaderText.Text = T("FilterPanelHeader");
            if (FilterPanelSubtitleText != null) FilterPanelSubtitleText.Text = T("FilterPanelSubtitle");
            if (ApplyChangesButtonText != null) ApplyChangesButtonText.Text = T("ApplyChangesButton");
            if (UpdateFiltersButtonText != null) UpdateFiltersButtonText.Text = T("UpdateFiltersButton");
            if (RestoreDefaultsButtonText != null) RestoreDefaultsButtonText.Text = T("RestoreDefaultsButton");

            foreach (var category in FilterCatalog.Categories)
            {
                if (_categoryTitleTexts.TryGetValue(category.Id, out var titleBlock))
                {
                    titleBlock.Text = T(category.NameKey);
                }
                if (_categoryDescTexts.TryGetValue(category.Id, out var descBlock))
                {
                    descBlock.Text = T(category.DescriptionKey);
                }
                if (_categoryBadgeTexts.TryGetValue(category.Id, out var badgeBlock))
                {
                    var (enabled, total) = _filterManager.GetCategoryCounts(category.Id);
                    badgeBlock.Text = T("FilterCountSummaryFormat", enabled, total);
                }
                if (_categoryCheckBoxes.TryGetValue(category.Id, out var catCb))
                {
                    catCb.IsChecked = _filterManager.GetCategoryState(category.Id);
                }
            }

            foreach (var subGroup in FilterCatalog.SubGroups)
            {
                if (_subGroupTitleTexts.TryGetValue(subGroup.Id, out var titleBlock))
                {
                    titleBlock.Text = T(subGroup.NameKey);
                }
                if (_subGroupBadgeTexts.TryGetValue(subGroup.Id, out var badgeBlock))
                {
                    var (enabled, total) = _filterManager.GetSubGroupCounts(subGroup.Id);
                    badgeBlock.Text = T("FilterCountSummaryFormat", enabled, total);
                }
                if (_subGroupCheckBoxes.TryGetValue(subGroup.Id, out var sgCb))
                {
                    sgCb.IsChecked = _filterManager.GetSubGroupState(subGroup.Id);
                }
            }

            foreach (var item in FilterCatalog.Items)
            {
                if (_filterNameTexts.TryGetValue(item.Id, out var nameBlock))
                {
                    nameBlock.Text = T(item.NameKey);
                }
                if (_filterDescTexts.TryGetValue(item.Id, out var descBlock))
                {
                    descBlock.Text = T(item.DescriptionKey);
                }
                UpdateFilterBadge(item.Id);
                if (_filterCheckBoxes.TryGetValue(item.Id, out var cb))
                {
                    cb.IsChecked = _filterManager.IsFilterEnabled(item.Id);
                }
            }

            if (TotalFiltersActiveBadge != null)
            {
                var (totalEnabled, totalCount, totalRules) = _filterManager.GetOverallCounts();
                TotalFiltersActiveBadge.Text = T("TotalFiltersActiveFormat", totalEnabled, totalCount, totalRules);
            }
            UpdatePresetSelectorUi();
        }
        finally
        {
            _isUpdatingFilterUi = false;
        }
    }
    private void UpdateFilterBadge(string filterId)
    {
        if (!_filterBadgeTexts.TryGetValue(filterId, out var countBlock)) return;
        _filterBadgeBorders.TryGetValue(filterId, out var countBorder);

        var count = _filterManager.GetRuleCount(filterId);
        if (count.HasValue)
        {
            countBlock.Text = T("RulesCountFormat", count.Value);
            countBlock.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 211, 153));
            if (countBorder != null)
            {
                countBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 51, 36));
            }
        }
        else
        {
            countBlock.Text = T("FilterNotDownloaded");
            countBlock.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175));
            if (countBorder != null)
            {
                countBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 48, 56));
            }
        }
    }

    private void SetupPresetPills()
    {
        SetupPillHover(PresetPill_Basic, BlockingPreset.Basic);
        SetupPillHover(PresetPill_Standard, BlockingPreset.Standard);
        SetupPillHover(PresetPill_Advanced, BlockingPreset.Advanced);
        SetupPillHover(PresetPill_Max, BlockingPreset.Max);
        SetupPillHover(PresetPill_Custom, BlockingPreset.Custom);
    }

    private void SetupPillHover(Border? pill, BlockingPreset preset)
    {
        if (pill == null) return;
        pill.SetHandCursor();
        pill.PointerEntered += (s, e) =>
        {
            if (_filterManager.GetCurrentPreset() != preset)
            {
                pill.Background = PresetHoverBgBrush;
            }
        };
        pill.PointerExited += (s, e) =>
        {
            if (_filterManager.GetCurrentPreset() != preset)
            {
                pill.Background = PresetNormalBgBrush;
            }
        };
    }

    private void PresetPill_Basic_Tapped(object sender, TappedRoutedEventArgs e) => SelectPreset(BlockingPreset.Basic);
    private void PresetPill_Standard_Tapped(object sender, TappedRoutedEventArgs e) => SelectPreset(BlockingPreset.Standard);
    private void PresetPill_Advanced_Tapped(object sender, TappedRoutedEventArgs e) => SelectPreset(BlockingPreset.Advanced);
    private void PresetPill_Max_Tapped(object sender, TappedRoutedEventArgs e) => SelectPreset(BlockingPreset.Max);
    private void PresetPill_Custom_Tapped(object sender, TappedRoutedEventArgs e) => UpdatePresetSelectorUi();

    private void SelectPreset(BlockingPreset preset)
    {
        if (_filterManager.GetCurrentPreset() == preset) return;

        // 0ms instant UI update of all checkboxes and counter badges
        _filterManager.ApplyPresetFast(preset);
        RefreshFilterListUi();

        var (enabled, total, rules) = _filterManager.GetOverallCounts();
        var presetName = T($"Preset_{preset}");
        AppendLog(T("PresetSwitchedLog", presetName, enabled));

        // Background download and merge
        _ = Task.Run(async () =>
        {
            try
            {
                await _filterManager.ApplyPresetAsync(preset);
                DispatcherQueue.TryEnqueue(() =>
                {
                    RefreshFilterListUi();
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppendLog($"[FilterManager] Preset error: {ex.Message}");
                });
            }
        });
    }

    private void UpdatePresetSelectorUi()
    {
        if (PresetSelectorHeaderText != null) PresetSelectorHeaderText.Text = T("PresetSelectorHeader");
        if (PresetText_Basic != null) PresetText_Basic.Text = T("Preset_Basic");
        if (PresetSub_Basic != null) PresetSub_Basic.Text = T("Preset_Basic_Sub");
        if (PresetText_Standard != null) PresetText_Standard.Text = T("Preset_Standard");
        if (PresetSub_Standard != null) PresetSub_Standard.Text = T("Preset_Standard_Sub");
        if (PresetText_Advanced != null) PresetText_Advanced.Text = T("Preset_Advanced");
        if (PresetSub_Advanced != null) PresetSub_Advanced.Text = T("Preset_Advanced_Sub");
        if (PresetText_Max != null) PresetText_Max.Text = T("Preset_Max");
        if (PresetSub_Max != null) PresetSub_Max.Text = T("Preset_Max_Sub");
        if (PresetText_Custom != null) PresetText_Custom.Text = T("Preset_Custom");
        if (PresetSub_Custom != null) PresetSub_Custom.Text = T("Preset_Custom_Sub");

        var currentPreset = _filterManager.GetCurrentPreset();
        if (PresetActiveDescriptionText != null)
        {
            PresetActiveDescriptionText.Text = T($"Preset_{currentPreset}_Desc");
        }

        StylePresetPill(PresetPill_Basic, PresetIcon_Basic, PresetText_Basic, PresetSub_Basic, currentPreset == BlockingPreset.Basic);
        StylePresetPill(PresetPill_Standard, PresetIcon_Standard, PresetText_Standard, PresetSub_Standard, currentPreset == BlockingPreset.Standard);
        StylePresetPill(PresetPill_Advanced, PresetIcon_Advanced, PresetText_Advanced, PresetSub_Advanced, currentPreset == BlockingPreset.Advanced);
        StylePresetPill(PresetPill_Max, PresetIcon_Max, PresetText_Max, PresetSub_Max, currentPreset == BlockingPreset.Max);
        StylePresetPill(PresetPill_Custom, PresetIcon_Custom, PresetText_Custom, PresetSub_Custom, currentPreset == BlockingPreset.Custom);
    }

    private static void StylePresetPill(Border? pill, FontIcon? icon, TextBlock? text, TextBlock? subText, bool isActive)
    {
        if (pill == null) return;
        if (isActive)
        {
            pill.Background = PresetActiveBgBrush;
            pill.BorderBrush = PresetActiveBorderBrush;
            if (text != null) text.Foreground = PresetActiveTextBrush;
            if (icon != null) icon.Foreground = PresetActiveTextBrush;
            if (subText != null) subText.Foreground = PresetActiveSubTextBrush;
        }
        else
        {
            pill.Background = PresetNormalBgBrush;
            pill.BorderBrush = PresetNormalBorderBrush;
            if (text != null) text.Foreground = PresetNormalTextBrush;
            if (icon != null) icon.Foreground = PresetNormalIconBrush;
            if (subText != null) subText.Foreground = PresetNormalSubTextBrush;
        }
    }

    private async void FilterManagementExpander_Expanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        await CheckAndDownloadMissingFiltersAsync();
    }

    private async Task CheckAndDownloadMissingFiltersAsync()
    {
        var hasMissing = FilterCatalog.Items
            .Any(i => _filterManager.IsFilterEnabled(i.Id) && !_filterManager.IsFilterDownloaded(i.Id));

        if (!hasMissing) return;

        SetFiltersBusy(true);
        try
        {
            if (FilterStatusMessageText != null) FilterStatusMessageText.Text = T("UpdatingFiltersText");
            AppendLog($"[FilterManager] {T("UpdatingFiltersText")}");

            await Task.Run(async () =>
            {
                await _filterManager.EnsureFiltersReadyAsync(forceUpdate: false, msg =>
                {
                    DispatcherQueue.TryEnqueue(() => AppendLog(msg));
                });
            });

            RefreshFilterListUi();
            var counts = _filterManager.GetOverallCounts();
            AppendLog($"[FilterManager] {T("FilterUpdatedSuccess", counts.TotalCombinedRules, counts.TotalEnabled)}");
        }
        catch (Exception ex)
        {
            AppendLog($"[FilterManager Error] {ex.Message}");
        }
        finally
        {
            SetFiltersBusy(false);
            if (FilterStatusMessageText != null) FilterStatusMessageText.Text = string.Empty;
        }
    }

    private void CategoryCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFilterUi) return;
        if (sender is CheckBox cb && cb.Tag is string categoryId)
        {
            TriggerCategoryToggle(categoryId, cb.IsChecked == true);
        }
    }

    private void SubGroupCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFilterUi) return;
        if (sender is CheckBox cb && cb.Tag is string subGroupId)
        {
            TriggerSubGroupToggle(subGroupId, cb.IsChecked == true);
        }
    }

    private void FilterItemCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingFilterUi) return;
        if (sender is CheckBox cb && cb.Tag is string filterId)
        {
            TriggerItemToggle(filterId, cb.IsChecked == true);
        }
    }

    private void TriggerItemToggle(string filterId, bool enabled)
    {
        _isUpdatingFilterUi = true;
        try
        {
            // 1. Instant 0ms UI and settings update
            _filterManager.SetFilterEnabled(filterId, enabled);

            if (_filterCheckBoxes.TryGetValue(filterId, out var cb))
            {
                cb.IsChecked = enabled;
            }
            UpdateFilterBadge(filterId);

            if (FilterCatalog.ItemsById.TryGetValue(filterId, out var def))
            {
                if (!string.IsNullOrEmpty(def.SubGroupId))
                {
                    if (_subGroupCheckBoxes.TryGetValue(def.SubGroupId, out var sgCb))
                    {
                        sgCb.IsChecked = _filterManager.GetSubGroupState(def.SubGroupId);
                    }
                    if (_subGroupBadgeTexts.TryGetValue(def.SubGroupId, out var sgBadge))
                    {
                        var sgCounts = _filterManager.GetSubGroupCounts(def.SubGroupId);
                        sgBadge.Text = T("FilterCountSummaryFormat", sgCounts.EnabledCount, sgCounts.TotalCount);
                    }
                }

                if (_categoryCheckBoxes.TryGetValue(def.CategoryId, out var catCb))
                {
                    catCb.IsChecked = _filterManager.GetCategoryState(def.CategoryId);
                }
                if (_categoryBadgeTexts.TryGetValue(def.CategoryId, out var catBadge))
                {
                    var catCounts = _filterManager.GetCategoryCounts(def.CategoryId);
                    catBadge.Text = T("FilterCountSummaryFormat", catCounts.EnabledCount, catCounts.TotalCount);
                }

                if (TotalFiltersActiveBadge != null)
                {
                    var overall = _filterManager.GetOverallCounts();
                    TotalFiltersActiveBadge.Text = T("TotalFiltersActiveFormat", overall.TotalEnabled, overall.TotalCount, overall.TotalCombinedRules);
                }

                var filterName = T(def.NameKey);
                var action = enabled ? T("FilterActionEnabled") : T("FilterActionDisabled");
                var counts = _filterManager.GetOverallCounts();
                AppendLog(T("FilterToggleLog", action, filterName, counts.TotalCombinedRules));
            }
        }
        finally
        {
            _isUpdatingFilterUi = false;
        }

        // 2. Fire background file download & rule merge asynchronously via Task.Run
        _ = Task.Run(async () =>
        {
            try
            {
                await _filterManager.ToggleFilterAsync(filterId, enabled);
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppendLog($"[FilterManager Error] {ex.Message}");
                });
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateFilterBadge(filterId);
                    if (TotalFiltersActiveBadge != null)
                    {
                        var overall = _filterManager.GetOverallCounts();
                        TotalFiltersActiveBadge.Text = T("TotalFiltersActiveFormat", overall.TotalEnabled, overall.TotalCount, overall.TotalCombinedRules);
                    }
                });
            }
        });
    }

    private void TriggerSubGroupToggle(string subGroupId, bool enabled)
    {
        _isUpdatingFilterUi = true;
        try
        {
            // 1. Instant 0ms UI and settings update
            _filterManager.SetSubGroupFiltersFast(subGroupId, enabled);

            var items = FilterCatalog.GetItemsForSubGroup(subGroupId);
            foreach (var item in items)
            {
                if (_filterCheckBoxes.TryGetValue(item.Id, out var itemCb))
                {
                    itemCb.IsChecked = enabled;
                }
                UpdateFilterBadge(item.Id);
            }

            if (_subGroupCheckBoxes.TryGetValue(subGroupId, out var sgCb))
            {
                sgCb.IsChecked = enabled;
            }
            if (_subGroupBadgeTexts.TryGetValue(subGroupId, out var sgBadge))
            {
                var sgCounts = _filterManager.GetSubGroupCounts(subGroupId);
                sgBadge.Text = T("FilterCountSummaryFormat", sgCounts.EnabledCount, sgCounts.TotalCount);
            }

            if (FilterCatalog.SubGroupsById.TryGetValue(subGroupId, out var sgDef))
            {
                if (_categoryCheckBoxes.TryGetValue(sgDef.CategoryId, out var catCb))
                {
                    catCb.IsChecked = _filterManager.GetCategoryState(sgDef.CategoryId);
                }
                if (_categoryBadgeTexts.TryGetValue(sgDef.CategoryId, out var catBadge))
                {
                    var catCounts = _filterManager.GetCategoryCounts(sgDef.CategoryId);
                    catBadge.Text = T("FilterCountSummaryFormat", catCounts.EnabledCount, catCounts.TotalCount);
                }
            }

            if (TotalFiltersActiveBadge != null)
            {
                var overall = _filterManager.GetOverallCounts();
                TotalFiltersActiveBadge.Text = T("TotalFiltersActiveFormat", overall.TotalEnabled, overall.TotalCount, overall.TotalCombinedRules);
            }

            var sgName = FilterCatalog.SubGroupsById.TryGetValue(subGroupId, out var sgDefObj) ? T(sgDefObj.NameKey) : subGroupId;
            var action = enabled ? T("FilterActionEnabled") : T("FilterActionDisabled");
            AppendLog($"[FilterManager] Đã {action} tất cả bộ lọc trong nhóm con '{sgName}'.");
        }
        finally
        {
            _isUpdatingFilterUi = false;
        }

        // 2. Fire background file download & rule merge asynchronously via Task.Run
        _ = Task.Run(async () =>
        {
            try
            {
                await _filterManager.SetSubGroupFiltersAsync(subGroupId, enabled);
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppendLog($"[FilterManager Error] {ex.Message}");
                });
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var subItems = FilterCatalog.GetItemsForSubGroup(subGroupId);
                    foreach (var item in subItems)
                    {
                        UpdateFilterBadge(item.Id);
                    }
                    if (TotalFiltersActiveBadge != null)
                    {
                        var overall = _filterManager.GetOverallCounts();
                        TotalFiltersActiveBadge.Text = T("TotalFiltersActiveFormat", overall.TotalEnabled, overall.TotalCount, overall.TotalCombinedRules);
                    }
                });
            }
        });
    }

    private void TriggerCategoryToggle(string categoryId, bool enabled)
    {
        _isUpdatingFilterUi = true;
        try
        {
            // 1. Instant 0ms UI and settings update
            _filterManager.SetCategoryFiltersFast(categoryId, enabled);

            var items = FilterCatalog.GetItemsForCategory(categoryId);
            foreach (var item in items)
            {
                if (_filterCheckBoxes.TryGetValue(item.Id, out var itemCb))
                {
                    itemCb.IsChecked = enabled;
                }
                UpdateFilterBadge(item.Id);
            }

            var subGroups = FilterCatalog.GetSubGroupsForCategory(categoryId);
            foreach (var sg in subGroups)
            {
                if (_subGroupCheckBoxes.TryGetValue(sg.Id, out var sgCb))
                {
                    sgCb.IsChecked = enabled;
                }
                if (_subGroupBadgeTexts.TryGetValue(sg.Id, out var sgBadge))
                {
                    var sgCounts = _filterManager.GetSubGroupCounts(sg.Id);
                    sgBadge.Text = T("FilterCountSummaryFormat", sgCounts.EnabledCount, sgCounts.TotalCount);
                }
            }

            if (_categoryCheckBoxes.TryGetValue(categoryId, out var catCb))
            {
                catCb.IsChecked = enabled;
            }
            if (_categoryBadgeTexts.TryGetValue(categoryId, out var catBadge))
            {
                var catCounts = _filterManager.GetCategoryCounts(categoryId);
                catBadge.Text = T("FilterCountSummaryFormat", catCounts.EnabledCount, catCounts.TotalCount);
            }

            if (TotalFiltersActiveBadge != null)
            {
                var overall = _filterManager.GetOverallCounts();
                TotalFiltersActiveBadge.Text = T("TotalFiltersActiveFormat", overall.TotalEnabled, overall.TotalCount, overall.TotalCombinedRules);
            }

            var catDef = FilterCatalog.CategoriesById.GetValueOrDefault(categoryId);
            var catName = catDef != null ? T(catDef.NameKey) : categoryId;
            var action = enabled ? T("FilterActionEnabled") : T("FilterActionDisabled");
            AppendLog($"[FilterManager] Đã {action} tất cả bộ lọc trong nhóm '{catName}'.");
        }
        finally
        {
            _isUpdatingFilterUi = false;
        }

        // 2. Fire background file download & rule merge asynchronously via Task.Run
        _ = Task.Run(async () =>
        {
            try
            {
                await _filterManager.SetCategoryFiltersAsync(categoryId, enabled);
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AppendLog($"[FilterManager Error] {ex.Message}");
                });
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var catItems = FilterCatalog.GetItemsForCategory(categoryId);
                    foreach (var item in catItems)
                    {
                        UpdateFilterBadge(item.Id);
                    }
                    if (TotalFiltersActiveBadge != null)
                    {
                        var overall = _filterManager.GetOverallCounts();
                        TotalFiltersActiveBadge.Text = T("TotalFiltersActiveFormat", overall.TotalEnabled, overall.TotalCount, overall.TotalCombinedRules);
                    }
                });
            }
        });
    }

    private async void ApplyChangesButton_Click(object sender, RoutedEventArgs e)
    {
        SetFiltersBusy(true);
        try
        {
            if (FilterStatusMessageText != null) FilterStatusMessageText.Text = T("ApplyingChangesText");
            AppendLog($"[FilterManager] {T("ApplyingChangesText")}");

            var totalRules = await _filterManager.MergeFiltersAsync();
            RefreshFilterListUi();

            var counts = _filterManager.GetOverallCounts();
            ShowMessage(InfoBarSeverity.Success, "Filter Lists", T("FilterApplySuccess", totalRules));
            AppendLog($"[FilterManager] {T("FilterApplySuccess", totalRules)}");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Filter Apply Error", ex.Message);
            AppendLog($"[FilterManager Error] {ex.Message}");
        }
        finally
        {
            SetFiltersBusy(false);
            if (FilterStatusMessageText != null) FilterStatusMessageText.Text = string.Empty;
        }
    }

    private async void UpdateFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        SetFiltersBusy(true);
        try
        {
            if (FilterStatusMessageText != null) FilterStatusMessageText.Text = T("UpdatingFiltersText");
            AppendLog($"[FilterManager] {T("UpdatingFiltersText")}");

            await Task.Run(async () =>
            {
                await _filterManager.EnsureFiltersReadyAsync(forceUpdate: true, msg =>
                {
                    DispatcherQueue.TryEnqueue(() => AppendLog(msg));
                });
            });

            RefreshFilterListUi();
            var counts = _filterManager.GetOverallCounts();
            ShowMessage(InfoBarSeverity.Success, "Filter Lists", T("FilterUpdatedSuccess", counts.TotalCombinedRules, counts.TotalEnabled));
            AppendLog($"[FilterManager] {T("FilterUpdatedSuccess", counts.TotalCombinedRules, counts.TotalEnabled)}");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Filter Update Error", ex.Message);
            AppendLog($"[FilterManager Error] {ex.Message}");
        }
        finally
        {
            SetFiltersBusy(false);
            if (FilterStatusMessageText != null) FilterStatusMessageText.Text = string.Empty;
        }
    }

    private async void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        SetFiltersBusy(true);
        try
        {
            await _filterManager.ResetToDefaultsAsync();
            RefreshFilterListUi();
            var counts = _filterManager.GetOverallCounts();
            ShowMessage(InfoBarSeverity.Informational, "Filter Lists", T("FilterRestoreSuccess"));
            AppendLog($"[FilterManager] {T("FilterRestoreSuccess")} ({counts.TotalCombinedRules:N0} rules active)");
        }
        catch (Exception ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Filter Reset Error", ex.Message);
        }
        finally
        {
            SetFiltersBusy(false);
        }
    }

    private void SetFiltersBusy(bool busy)
    {
        if (FiltersBusyRing != null)
        {
            FiltersBusyRing.IsActive = busy;
            FiltersBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
        if (ApplyChangesButton != null) ApplyChangesButton.IsEnabled = !busy;
        if (UpdateFiltersButton != null) UpdateFiltersButton.IsEnabled = !busy;
        if (RestoreDefaultsButton != null) RestoreDefaultsButton.IsEnabled = !busy;
    }

    private static SolidColorBrush GetCategoryBrush(string categoryId) => categoryId switch
    {
        FilterCatalog.CategoryBuiltin => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 34, 84)),
        FilterCatalog.CategoryAds => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 58, 95)),
        FilterCatalog.CategoryPrivacy => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 51, 36)),
        FilterCatalog.CategorySecurity => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 24, 24)),
        FilterCatalog.CategoryMultipurpose => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 32, 72)),
        FilterCatalog.CategoryCookies => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 42, 16)),
        FilterCatalog.CategorySocial => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 19, 56, 59)),
        FilterCatalog.CategoryAnnoyances => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 61, 35, 20)),
        FilterCatalog.CategoryRegions => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 50, 72)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 30, 34, 42))
    };

    private static SolidColorBrush GetCategoryForegroundBrush(string categoryId) => categoryId switch
    {
        FilterCatalog.CategoryBuiltin => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 167, 139, 250)),
        FilterCatalog.CategoryAds => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 165, 250)),
        FilterCatalog.CategoryPrivacy => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 211, 153)),
        FilterCatalog.CategorySecurity => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113)),
        FilterCatalog.CategoryMultipurpose => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 129, 140, 248)),
        FilterCatalog.CategoryCookies => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 158, 11)),
        FilterCatalog.CategorySocial => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 212, 191)),
        FilterCatalog.CategoryAnnoyances => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 146, 60)),
        FilterCatalog.CategoryRegions => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248)),
        _ => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 232, 240))
    };

    #endregion

    private async Task AnalyzeAsync()
    {
        SetBusy(true);
        ResultInfoBar.IsOpen = false;
        try
        {
            AnalysisStatusText.Text = T("ChromeAnalyzing");
            AnalysisStatusDot.Background = Brush(Colors.Orange);

            var report = await Task.Run(() =>
            {
                var inst = ChromeInstallationFinder.Find();
                var rep = AnalysisService.Analyze(inst);
                return (Installation: inst, Report: rep);
            });

            _installation = report.Installation;
            _analysis = report.Report;

            AnalysisStatusText.Text = T("ChromeReady");
            AnalysisStatusDot.Background = Brush(Colors.MediumSeaGreen);
            ChromeVersionText.Text = $"{T("ChromeVersionLabel")} {_installation.Version}";
            ChromePathText.Text = _installation.ExecutablePath;
            LaunchButton.IsEnabled = true;

            DetailsTextBox.Text = FormatReport(_analysis);
        }
        catch (Exception ex)
        {
            _installation = null;
            _analysis = null;
            AnalysisStatusDot.Background = Brush(Colors.IndianRed);
            AnalysisStatusText.Text = T("ChromeNotFound");
            ChromeVersionText.Text = ex.Message;
            ChromePathText.Text = "—";
            LaunchButton.IsEnabled = false;
            ShowMessage(InfoBarSeverity.Warning, T("ChromeNotFound"), ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _cts?.Cancel();
            LaunchButton.IsEnabled = false;
            LaunchButtonText.Text = T("StopButton");
            return;
        }

        if (_installation == null)
        {
            return;
        }

        var nativeAdblock = NativeAdblockToggle.IsOn;
        var mv2Enabler = Mv2EnablerToggle.IsOn;
        var autoUpdate = AutoUpdateFiltersToggle.IsOn;
        var openExt = OpenExtensionsCheckBox.IsChecked == true;

        if (!nativeAdblock && !mv2Enabler)
        {
            ShowMessage(InfoBarSeverity.Warning, "Warning", "Please select at least one feature (Native Adblock or Manifest V2 Enabler).");
            return;
        }

        _isRunning = true;
        _cts = new CancellationTokenSource();
        LaunchButton.IsEnabled = true;
        LaunchButtonText.Text = T("StopButton");
        LaunchButtonIcon.Glyph = "\uE71A"; // Stop icon
        SetBusy(true);
        if (RunningBadge != null) RunningBadge.Visibility = Visibility.Collapsed;
        ResultInfoBar.IsOpen = false;

        AppendLog($"=== {T("HeroTitle")} - Session Started at {DateTime.Now:HH:mm:ss} ===");
        AppendLog($"Options: Native Adblock = {nativeAdblock}, MV2 Enabler = {mv2Enabler}, Auto-Update = {autoUpdate}");

        var options = new ChromeSupervisorOptions(
            ChromePath: _installation.ExecutablePath,
            FilterPath: _filterManager.GetActiveCombinedFilterPath(),
            EnableNativeAdblock: nativeAdblock,
            EnableMv2Enabler: mv2Enabler,
            AutoUpdateFilters: autoUpdate,
            OpenExtensionsPage: openExt,
            LogCallback: msg => DispatcherQueue.TryEnqueue(() => AppendLog(msg)),
            BlockCallback: (category, detail) => DispatcherQueue.TryEnqueue(() => HandleBlockEvent(category, detail)),
            StartedCallback: pid => DispatcherQueue.TryEnqueue(() =>
            {
                SetBusy(false);
                if (RunningBadge != null) RunningBadge.Visibility = Visibility.Visible;
                if (AnalysisStatusDot != null) AnalysisStatusDot.Background = Brush(Windows.UI.Color.FromArgb(255, 59, 130, 246));
                if (AnalysisStatusText != null) AnalysisStatusText.Text = T("ChromeRunning");
            })
        );
        try
        {
            await Task.Run(async () =>
            {
                using var supervisor = new ChromeSupervisor(options);
                await supervisor.RunAsync(_cts.Token);
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("[Supervisor] Chrome session stopped by user.");
        }
        catch (Exception ex)
        {
            AppendLog($"[Supervisor Error] {ex.Message}");
            ShowMessage(InfoBarSeverity.Error, "Launch Error", ex.Message);
        }
        finally
        {
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;
            if (RunningBadge != null) RunningBadge.Visibility = Visibility.Collapsed;
            if (AnalysisStatusDot != null) AnalysisStatusDot.Background = Brush(Colors.MediumSeaGreen);
            if (AnalysisStatusText != null) AnalysisStatusText.Text = T("ChromeReady");
            SetBusy(false);
            LaunchButton.IsEnabled = true;
            UpdateLaunchButtonText();
            LaunchButtonIcon.Glyph = "\uE768"; // Play / Launch icon
            AppendLog($"=== Session Ended at {DateTime.Now:HH:mm:ss} ===");
        }
    }

    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] {message}";
        _logBuilder.AppendLine(entry);
        if (LiveConsoleTextBox != null)
        {
            LiveConsoleTextBox.Text = _logBuilder.ToString();
        }
        if (ConsoleScrollViewer != null)
        {
            ConsoleScrollViewer.ChangeView(null, ConsoleScrollViewer.ScrollableHeight, null);
        }
    }

    private void HandleBlockEvent(string category, string detail)
    {
        if (category == "Network")
        {
            _blockedCount++;
            if (BlockedRequestsBadge != null) BlockedRequestsBadge.Text = T("BlockedCounterBadge", _blockedCount);
        }
        else if (category == "YouTube")
        {
            _ytBypassCount++;
            if (YouTubeBypassBadge != null) YouTubeBypassBadge.Text = T("YouTubeAdsBadge", _ytBypassCount);
        }
    }

    private void ClearLogsButton_Click(object sender, RoutedEventArgs e)
    {
        _logBuilder.Clear();
        if (LiveConsoleTextBox != null) LiveConsoleTextBox.Text = string.Empty;
        _blockedCount = 0;
        _ytBypassCount = 0;
        if (BlockedRequestsBadge != null) BlockedRequestsBadge.Text = T("BlockedCounterBadge", 0);
        if (YouTubeBypassBadge != null) YouTubeBypassBadge.Text = T("YouTubeAdsBadge", 0);
    }

    private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(LiveConsoleTextBox.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            ShowMessage(InfoBarSeverity.Success, "Notice", T("CopySuccess"));
        }
        catch { }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (BusyRing != null)
        {
            BusyRing.IsActive = busy;
            BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message)
    {
        ResultInfoBar.Severity = severity;
        ResultInfoBar.Title = title;
        ResultInfoBar.Message = message;
        ResultInfoBar.IsOpen = true;
    }

    private void SelectCurrentLanguage()
    {
        var code = LocalizationService.CurrentLanguage;
        for (var i = 0; i < LanguageComboBox.Items.Count; i++)
        {
            if (LanguageComboBox.Items[i] is ComboBoxItem item && (string)item.Tag == code)
            {
                LanguageComboBox.SelectedIndex = i;
                return;
            }
        }
        LanguageComboBox.SelectedIndex = 0;
    }

    private void UpdateLaunchButtonText()
    {
        if (LaunchButtonText == null)
        {
            return;
        }

        if (_isRunning)
        {
            LaunchButtonText.Text = T("StopButton");
            return;
        }

        LaunchButtonText.Text = T("LaunchButton");
    }

    private void ApplyLanguage()
    {
        if (HeroTitleText != null) HeroTitleText.Text = T("HeroTitle");
        if (HeroSubtitleText != null) HeroSubtitleText.Text = T("HeroSubtitle");
        if (LanguageLabelText != null) LanguageLabelText.Text = T("LanguageLabel");

        if (Feature1TitleText != null) Feature1TitleText.Text = T("Feature1Title");
        if (Feature1DescText != null) Feature1DescText.Text = T("Feature1Desc");
        if (Feature2TitleText != null) Feature2TitleText.Text = T("Feature2Title");
        if (Feature2DescText != null) Feature2DescText.Text = T("Feature2Desc");
        if (Feature3TitleText != null) Feature3TitleText.Text = T("Feature3Title");
        if (Feature3DescText != null) Feature3DescText.Text = T("Feature3Desc");

        if (OpenExtensionsCheckBox != null) OpenExtensionsCheckBox.Content = T("OpenExtensionsCheckBox");
        if (MinimizeToTrayButtonText != null) MinimizeToTrayButtonText.Text = T("MinimizeToTrayButton");
        if (RefreshButtonText != null) RefreshButtonText.Text = T("RefreshButton");
        if (LiveConsoleHeaderText != null) LiveConsoleHeaderText.Text = T("LiveConsoleHeader");
        if (ClearLogsButtonText != null) ClearLogsButtonText.Text = T("ClearLogsButton");
        if (CopyLogsButtonText != null) CopyLogsButtonText.Text = T("CopyLogsButton");
        if (TechnicalDetailsHeaderText != null) TechnicalDetailsHeaderText.Text = T("TechnicalDetailsHeader");

        if (BlockedRequestsBadge != null) BlockedRequestsBadge.Text = T("BlockedCounterBadge", _blockedCount);
        if (YouTubeBypassBadge != null) YouTubeBypassBadge.Text = T("YouTubeAdsBadge", _ytBypassCount);

        if (RunningBadgeText != null) RunningBadgeText.Text = T("RunningBadge");

        if (!_busy)
        {
            if (_isRunning)
            {
                if (AnalysisStatusText != null) AnalysisStatusText.Text = T("ChromeRunning");
            }
            else if (_installation != null)
            {
                if (AnalysisStatusText != null) AnalysisStatusText.Text = T("ChromeReady");
                if (ChromeVersionText != null) ChromeVersionText.Text = $"{T("ChromeVersionLabel")} {_installation.Version}";
            }
            else
            {
                if (AnalysisStatusText != null) AnalysisStatusText.Text = T("ChromeNotFound");
            }
        }
        UpdateLaunchButtonText();
        RefreshFilterListUi();

        if (_analysis != null && DetailsTextBox != null)
        {
            DetailsTextBox.Text = FormatReport(_analysis);
        }
    }

    private static string T(string key, params object[] arguments) => LocalizationService.Text(key, arguments);
    private static SolidColorBrush Brush(Windows.UI.Color color) => new(color);

    private static string FormatReport(AnalysisReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Chrome Executable: {report.ChromePath}");
        builder.AppendLine($"Chrome DLL:        {report.DllPath}");
        builder.AppendLine($"Chrome Version:    {report.Version}");
        builder.AppendLine($"Architecture:      {report.Architecture}");
        builder.AppendLine($"Preferred Base:    {report.PreferredImageBase}");
        builder.AppendLine($"DLL Size:          {report.FileSize:N0} bytes");
        builder.AppendLine($"SHA256:            {report.Sha256}");
        builder.AppendLine();
        builder.AppendLine($"Locator Success:   {report.Success}");
        builder.AppendLine($"Pattern Matches:   {report.PatternMatchCount}");
        builder.AppendLine($"Semantic Matches:  {report.SemanticMatchCount}");

        if (report.Target is { } target)
        {
            builder.AppendLine($"Rule ID:           {target.RuleId}");
            builder.AppendLine($"Description:       {target.Description}");
            builder.AppendLine($"Primary RVA:       0x{target.PatchRva:X}");
            builder.AppendLine($"Primary Raw:       0x{target.PatchRawOffset:X}");
            builder.AppendLine($"Primary Patch:     0x{target.ExpectedByte:X2} -> 0x{target.ReplacementByte:X2}");
            builder.AppendLine($"Additional Edits:  {target.AdditionalEdits.Count}");
            for (var i = 0; i < target.AdditionalEdits.Count; i++)
            {
                var edit = target.AdditionalEdits[i];
                builder.AppendLine($"  [{i + 1}] RVA: 0x{edit.PatchRva:X}, Byte: 0x{edit.ExpectedByte:X2} -> 0x{edit.ReplacementByte:X2}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Diagnostics:");
        foreach (var diagnostic in report.Diagnostics)
        {
            builder.AppendLine($" - {diagnostic}");
        }

        return builder.ToString();
    }
}


public static class CursorExtensions
{
    private static readonly System.Reflection.PropertyInfo? ProtectedCursorProp =
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

    public static void SetHandCursor(this UIElement element)
    {
        try
        {
            ProtectedCursorProp?.SetValue(element, InputSystemCursor.Create(InputSystemCursorShape.Hand));
        }
        catch
        {
        }
    }
}
