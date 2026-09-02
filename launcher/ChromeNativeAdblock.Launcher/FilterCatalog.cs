using System;
using System.Collections.Generic;
using System.Linq;

namespace ChromeNativeAdblock.Launcher;

public enum BlockingPreset
{
    Basic,
    Standard,
    Advanced,
    Max,
    Custom
}

public sealed record FilterCategoryDefinition(
    string Id,
    string NameKey,
    string DefaultName,
    string DescriptionKey,
    string DefaultDescription,
    string IconGlyph,
    int DisplayOrder
);

public sealed record FilterSubGroupDefinition(
    string Id,
    string CategoryId,
    string NameKey,
    string DefaultName,
    string DescriptionKey,
    string DefaultDescription,
    int DisplayOrder
);

public sealed record FilterItemDefinition(
    string Id,
    string CategoryId,
    string? SubGroupId,
    string NameKey,
    string DefaultName,
    string DescriptionKey,
    string DefaultDescription,
    string Url,
    string FallbackFileName,
    bool DefaultEnabled,
    int DisplayOrder
);

public static class FilterCatalog
{
    // Categories matching core uBlock Origin + EasyList structure
    public const string CategoryBuiltin = "builtin";
    public const string CategoryCore = CategoryBuiltin;
    public const string CategoryAds = "ads";
    public const string CategoryPrivacy = "privacy";
    public const string CategoryRegions = "regions";

    public static IReadOnlyList<FilterCategoryDefinition> Categories { get; } = new List<FilterCategoryDefinition>
    {
        new(CategoryBuiltin, "Category_Builtin", "Dựng sẵn / Built-in (uBlock)", "Category_Builtin_Desc", "Quy tắc cốt lõi uBlock Origin chống rủi ro mã độc, theo dõi và sửa lỗi web", "\uE74C", 1),
        new(CategoryAds, "Category_Ads", "Quảng cáo / Ads (EasyList)", "Category_Ads_Desc", "Chặn quảng cáo mạng, pop-up, video ads và banner tài trợ chuẩn EasyList", "\uE83D", 2),
        new(CategoryPrivacy, "Category_Privacy", "Riêng tư / Privacy (EasyPrivacy)", "Category_Privacy_Desc", "Bảo vệ quyền riêng tư, chặn theo dõi hành vi và telemetry", "\uEA18", 3),
        new(CategoryRegions, "Category_Regions", "Khu vực / Regions (Việt Nam)", "Category_Regions_Desc", "Bộ lọc tối ưu hóa dành riêng cho các trang web và báo điện tử tại Việt Nam", "\uE774", 4)
    };

    public static IReadOnlyList<FilterSubGroupDefinition> SubGroups { get; } = new List<FilterSubGroupDefinition>
    {
        new("subgroup-ublock-filters", CategoryBuiltin, "SubGroup_uBlockFilters", "uBlock Origin Filters", "SubGroup_uBlockFilters_Desc", "Quy tắc cốt lõi uBlock Origin", 1)
    };

    public static IReadOnlyList<FilterItemDefinition> Items { get; } = new List<FilterItemDefinition>
    {
        // =========================================================================
        // 1. Dựng sẵn / Built-in (uBlock Origin)
        // =========================================================================
        new("ublock-filters", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_filters_Name", "uBlock filters – Ads", "Filter_ublock_filters_Desc", "Bộ lọc cơ bản của uBlock Origin loại bỏ quảng cáo và chống phá hoại", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt", "ublock_filters.txt", true, 1),
        new("ublock-badware", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_badware_Name", "uBlock filters – Badware risks", "Filter_ublock_badware_Desc", "Chặn các tên miền nguy hiểm và mã độc rủi ro cao từ uBlock Origin", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/badware.txt", "ublock_badware.txt", true, 2),
        new("ublock-privacy", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_privacy_Name", "uBlock filters – Privacy", "Filter_ublock_privacy_Desc", "Bảo vệ quyền riêng tư và chặn theo dõi hành vi nâng cao", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/privacy.txt", "ublock_privacy.txt", true, 3),
        new("ublock-quick-fixes", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_quick_fixes_Name", "uBlock filters – Quick fixes", "Filter_ublock_quick_fixes_Desc", "Bản vá sửa lỗi nhanh cho các trang web và trình phát video phổ biến", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/quick-fixes.txt", "ublock_quick_fixes.txt", true, 4),
        new("ublock-unbreak", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_unbreak_Name", "uBlock filters – Unbreak", "Filter_ublock_unbreak_Desc", "Khắc phục lỗi hiển thị và chống vỡ giao diện trên các trang web", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/unbreak.txt", "ublock_unbreak.txt", true, 5),

        // =========================================================================
        // 2. Quảng cáo / Ads (EasyList - ABP)
        // =========================================================================
        new("easylist", CategoryAds, null, "Filter_easylist_Name", "EasyList", "Filter_easylist_Desc", "Danh sách chặn quảng cáo phổ biến và toàn diện nhất thế giới (Adblock Plus)", "https://easylist.to/easylist/easylist.txt", "easylist_basic.txt", true, 10),

        // =========================================================================
        // 3. Riêng tư / Privacy (EasyPrivacy - ABP)
        // =========================================================================
        new("easyprivacy", CategoryPrivacy, null, "Filter_easyprivacy_Name", "EasyPrivacy", "Filter_easyprivacy_Desc", "Ngăn chặn toàn diện các trình theo dõi hành vi, phân tích và telemetry (Adblock Plus)", "https://easylist.to/easylist/easyprivacy.txt", "easyprivacy.txt", true, 20),

        // =========================================================================
        // 4. Khu vực / Regions (ABPVN - Việt Nam)
        // =========================================================================
        new("reg-vn", CategoryRegions, null, "Filter_reg_vn_Name", "vn: ABPVN List", "Filter_reg_vn_Desc", "Bộ lọc tối ưu hóa dành riêng cho các trang web và báo điện tử tại Việt Nam", "https://raw.githubusercontent.com/abpvn/abpvn/master/filter/abpvn.txt", "abpvn_basic.txt", true, 30)
    };

    public static IReadOnlyDictionary<string, FilterCategoryDefinition> CategoriesById { get; } =
        Categories.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, FilterSubGroupDefinition> SubGroupsById { get; } =
        SubGroups.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, FilterItemDefinition> ItemsById { get; } =
        Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> BasicPresetFilterIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "easylist",
        "reg-vn"
    };

    public static IReadOnlySet<string> StandardPresetFilterIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ublock-filters",
        "ublock-badware",
        "ublock-privacy",
        "ublock-quick-fixes",
        "ublock-unbreak",
        "easylist",
        "easyprivacy",
        "reg-vn"
    };

    public static IReadOnlySet<string> AdvancedPresetFilterIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ublock-filters",
        "ublock-badware",
        "ublock-privacy",
        "ublock-quick-fixes",
        "ublock-unbreak",
        "easylist",
        "easyprivacy",
        "reg-vn"
    };

    public static IReadOnlySet<string> MaxPresetFilterIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ublock-filters",
        "ublock-badware",
        "ublock-privacy",
        "ublock-quick-fixes",
        "ublock-unbreak",
        "easylist",
        "easyprivacy",
        "reg-vn"
    };

    public static IReadOnlySet<string> GetFilterIdsForPreset(BlockingPreset preset) => preset switch
    {
        BlockingPreset.Basic => BasicPresetFilterIds,
        BlockingPreset.Standard => StandardPresetFilterIds,
        BlockingPreset.Advanced => AdvancedPresetFilterIds,
        BlockingPreset.Max => MaxPresetFilterIds,
        _ => DefaultEnabledFilterIds
    };

    public static IReadOnlySet<string> DefaultEnabledFilterIds { get; } =
        new HashSet<string>(Items.Where(i => i.DefaultEnabled).Select(i => i.Id), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<FilterItemDefinition> GetItemsForCategory(string categoryId) =>
        Items.Where(i => string.Equals(i.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
             .OrderBy(i => i.DisplayOrder)
             .ToList();

    public static IReadOnlyList<FilterItemDefinition> GetItemsForSubGroup(string subGroupId) =>
        Items.Where(i => string.Equals(i.SubGroupId, subGroupId, StringComparison.OrdinalIgnoreCase))
             .OrderBy(i => i.DisplayOrder)
             .ToList();

    public static IReadOnlyList<FilterSubGroupDefinition> GetSubGroupsForCategory(string categoryId) =>
        SubGroups.Where(s => string.Equals(s.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(s => s.DisplayOrder)
                 .ToList();
}
