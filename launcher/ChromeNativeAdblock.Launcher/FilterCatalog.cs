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
    // 9 Categories matching uBlock Origin structure
    public const string CategoryBuiltin = "builtin";
    public const string CategoryCore = CategoryBuiltin;
    public const string CategoryAds = "ads";
    public const string CategoryPrivacy = "privacy";
    public const string CategorySecurity = "security";
    public const string CategoryMultipurpose = "multipurpose";
    public const string CategoryCookies = "cookies";
    public const string CategorySocial = "social";
    public const string CategoryAnnoyances = "annoyances";
    public const string CategoryRegions = "regions";

    public static IReadOnlyList<FilterCategoryDefinition> Categories { get; } = new List<FilterCategoryDefinition>
    {
        new(CategoryBuiltin, "Category_Builtin", "Dựng sẵn / Built-in", "Category_Builtin_Desc", "Quy tắc cốt lõi uBlock Origin chống rủi ro mã độc và sửa lỗi web", "\uE74C", 1),
        new(CategoryAds, "Category_Ads", "Quảng cáo / Ads", "Category_Ads_Desc", "Chặn quảng cáo mạng, pop-up, video ads và banner tài trợ", "\uE83D", 2),
        new(CategoryPrivacy, "Category_Privacy", "Riêng tư / Privacy", "Category_Privacy_Desc", "Bảo vệ quyền riêng tư, chặn theo dõi hành vi và telemetry", "\uEA18", 3),
        new(CategorySecurity, "Category_Security", "Chặn mã độc, bảo mật / Malware & Security", "Category_Security_Desc", "Chặn tên miền độc hại, lừa đảo phishing và ransomware", "\uE7BA", 4),
        new(CategoryMultipurpose, "Category_Multipurpose", "Đa chức năng / Multipurpose", "Category_Multipurpose_Desc", "Các danh sách máy chủ quảng cáo và file hosts tổng hợp", "\uE71D", 5),
        new(CategoryCookies, "Category_Cookies", "Các thông báo cookie / Cookie Notices", "Category_Cookies_Desc", "Tự động ẩn và loại bỏ các hộp thoại xin phép cookie GDPR", "\uE790", 6),
        new(CategorySocial, "Category_Social", "Tiện ích xã hội / Social Widgets", "Category_Social_Desc", "Chặn các nút chia sẻ mạng xã hội, like button và widget theo dõi", "\uE716", 7),
        new(CategoryAnnoyances, "Category_Annoyances", "Phiền toái / Annoyances", "Category_Annoyances_Desc", "Loại bỏ cửa sổ đăng ký nhận tin, khảo sát pop-up và thông báo đẩy", "\uE734", 8),
        new(CategoryRegions, "Category_Regions", "Khu vực, ngôn ngữ / Regions & Languages", "Category_Regions_Desc", "Bộ lọc tối ưu hóa cho các trang web theo từng khu vực và ngôn ngữ", "\uE774", 9)
    };

    public static IReadOnlyList<FilterSubGroupDefinition> SubGroups { get; } = new List<FilterSubGroupDefinition>
    {
        new("subgroup-ublock-filters", CategoryBuiltin, "SubGroup_uBlockFilters", "uBlock filters", "SubGroup_uBlockFilters_Desc", "Quy tắc cốt lõi uBlock Origin", 1),
        new("subgroup-easylist-cookies", CategoryCookies, "SubGroup_EasyListCookies", "EasyList/uBO – Cookie Notices", "SubGroup_EasyListCookies_Desc", "Thông báo cookie từ EasyList và uBlock Origin", 1),
        new("subgroup-adguard-cookies", CategoryCookies, "SubGroup_AdGuardCookies", "AdGuard/uBO – Cookie Notices", "SubGroup_AdGuardCookies_Desc", "Thông báo cookie từ AdGuard và uBlock Origin", 2),
        new("subgroup-easylist-annoyances", CategoryAnnoyances, "SubGroup_EasyListAnnoyances", "EasyList – Annoyances", "SubGroup_EasyListAnnoyances_Desc", "Bộ lọc phiền toái từ EasyList (AI, Chat, Newsletters, Notifications)", 1),
        new("subgroup-adguard-annoyances", CategoryAnnoyances, "SubGroup_AdGuardAnnoyances", "AdGuard – Annoyances", "SubGroup_AdGuardAnnoyances_Desc", "Bộ lọc phiền toái từ AdGuard (Banners, Popups, Widgets)", 2),
        new("subgroup-pl", CategoryRegions, "SubGroup_Pl", "pl: Oficjalne Polskie Filtry", "SubGroup_Pl_Desc", "Bộ lọc chính thức cho các trang web tiếng Ba Lan", 28),
        new("subgroup-ru", CategoryRegions, "SubGroup_Ru", "ru, ua, uz, kz: RU AdList", "SubGroup_Ru_Desc", "Bộ lọc RU AdList dành cho các trang web tiếng Nga và khu vực SNG", 30)
    };

    public static IReadOnlyList<FilterItemDefinition> Items { get; } = new List<FilterItemDefinition>
    {
        // =========================================================================
        // 1. Dựng sẵn / Built-in (CategoryBuiltin)
        // =========================================================================
        // Sub-group: uBlock filters (5 items, all default ON)
        new("ublock-filters", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_filters_Name", "uBlock filters – Ads", "Filter_ublock_filters_Desc", "Bộ lọc cơ bản của uBlock Origin loại bỏ quảng cáo và chống phá hoại", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/filters.txt", "ublock_filters.txt", true, 1),
        new("ublock-badware", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_badware_Name", "uBlock filters – Badware risks", "Filter_ublock_badware_Desc", "Chặn các tên miền nguy hiểm và mã độc rủi ro cao từ uBlock Origin", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/badware.txt", "ublock_badware.txt", true, 2),
        new("ublock-privacy", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_privacy_Name", "uBlock filters – Privacy", "Filter_ublock_privacy_Desc", "Bảo vệ quyền riêng tư và chặn theo dõi hành vi nâng cao", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/privacy.txt", "ublock_privacy.txt", true, 3),
        new("ublock-quick-fixes", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_quick_fixes_Name", "uBlock filters – Quick fixes", "Filter_ublock_quick_fixes_Desc", "Bản vá sửa lỗi nhanh cho các trang web và trình phát video phổ biến", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/quick-fixes.txt", "ublock_quick_fixes.txt", true, 4),
        new("ublock-unbreak", CategoryBuiltin, "subgroup-ublock-filters", "Filter_ublock_unbreak_Name", "uBlock filters – Unbreak", "Filter_ublock_unbreak_Desc", "Khắc phục lỗi hiển thị và chống vỡ giao diện trên các trang web", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/unbreak.txt", "ublock_unbreak.txt", true, 5),
        // Standalone
        new("ublock-experimental", CategoryBuiltin, null, "Filter_ublock_experimental_Name", "uBlock filters – Experimental", "Filter_ublock_experimental_Desc", "Các bộ lọc thử nghiệm mới nhất của uBlock Origin", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/experimental.txt", "ublock_experimental.txt", false, 6),

        // =========================================================================
        // 2. Quảng cáo / Ads (CategoryAds)
        // =========================================================================
        new("easylist", CategoryAds, null, "Filter_easylist_Name", "EasyList", "Filter_easylist_Desc", "Danh sách chặn quảng cáo phổ biến và toàn diện nhất thế giới", "https://easylist.to/easylist/easylist.txt", "easylist_basic.txt", true, 10),
        new("adguard-base", CategoryAds, null, "Filter_adguard_base_Name", "AdGuard – Ads", "Filter_adguard_base_Desc", "Bộ lọc quảng cáo toàn diện tiêu chuẩn từ AdGuard Team", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_2_Base/filter.txt", "adguard_base.txt", false, 11),
        new("adguard-mobile", CategoryAds, null, "Filter_adguard_mobile_Name", "AdGuard – Mobile Ads", "Filter_adguard_mobile_Desc", "Bộ lọc quảng cáo tối ưu cho giao diện di động và ứng dụng web", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_11_Mobile/filter.txt", "adguard_mobile.txt", false, 12),
        new("youtube-adblock", CategoryAds, null, "Filter_youtube_adblock_Name", "YouTube Adblock & Scriptlets", "Filter_youtube_adblock_Desc", "Chặn quảng cáo, biểu ngữ tài trợ và video ads trên YouTube", "https://raw.githubusercontent.com/yokoffing/youtube-adblock/master/YouTube-Adblock.txt", "youtube_rules.txt", true, 13),

        // =========================================================================
        // 3. Riêng tư / Privacy (CategoryPrivacy)
        // =========================================================================
        new("easyprivacy", CategoryPrivacy, null, "Filter_easyprivacy_Name", "EasyPrivacy", "Filter_easyprivacy_Desc", "Ngăn chặn toàn diện các trình theo dõi hành vi, phân tích và telemetry", "https://easylist.to/easylist/easyprivacy.txt", "easyprivacy.txt", true, 20),
        new("adguard-tracking", CategoryPrivacy, null, "Filter_adguard_tracking_Name", "AdGuard/uBO – URL Tracking Protection", "Filter_adguard_tracking_Desc", "Xóa các tham số theo dõi hành vi (utm, fbclid, gclid) trên URL", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_3_Spyware/filter.txt", "adguard_tracking.txt", false, 21),
        new("block-lan", CategoryPrivacy, null, "Filter_block_lan_Name", "Block Outsider Intrusion into LAN", "Filter_block_lan_Desc", "Ngăn chặn các trang web bên ngoài xâm nhập và quét mạng nội bộ LAN", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/lan.txt", "block_lan.txt", false, 22),

        // =========================================================================
        // 4. Chặn mã độc, bảo mật / Malware & Security (CategorySecurity)
        // =========================================================================
        new("malicious-urls", CategorySecurity, null, "Filter_malicious_urls_Name", "Online Malicious URL Blocklist", "Filter_malicious_urls_Desc", "Chặn các liên kết chứa mã độc, virus và trang web độc hại từ URLhaus", "https://curben.gitlab.io/malware-filter/urlhaus-filter-online.txt", "malicious_urls.txt", false, 30),
        new("phishing-urls", CategorySecurity, null, "Filter_phishing_urls_Name", "Phishing URL Blocklist", "Filter_phishing_urls_Desc", "Chặn các trang web lừa đảo trực tuyến (phishing) và giả mạo ngân hàng", "https://malware-filter.pages.dev/phishing-filter.txt", "phishing_urls.txt", false, 31),

        // =========================================================================
        // 5. Đa chức năng / Multipurpose (CategoryMultipurpose)
        // =========================================================================
        new("peter-lowe", CategoryMultipurpose, null, "Filter_peter_lowe_Name", "Peter Lowe’s Ad and tracking server list", "Filter_peter_lowe_Desc", "Danh sách máy chủ quảng cáo và theo dõi của Peter Lowe", "https://pgl.yoyo.org/adservers/serverlist.php?hostformat=hosts&showintro=0&mimetype=plaintext", "peter_lowe.txt", false, 40),
        new("dan-pollock", CategoryMultipurpose, null, "Filter_dan_pollock_Name", "Dan Pollock’s hosts file", "Filter_dan_pollock_Desc", "File hosts chặn quảng cáo và tên miền rác nổi tiếng của Dan Pollock", "https://someonewhocares.org/hosts/hosts", "dan_pollock.txt", false, 41),

        // =========================================================================
        // 6. Các thông báo cookie / Cookie Notices (CategoryCookies)
        // =========================================================================
        // Sub-group: EasyList/uBO – Cookie Notices
        new("easylist-cookies", CategoryCookies, "subgroup-easylist-cookies", "Filter_easylist_cookies_Name", "EasyList – Cookie Notices", "Filter_easylist_cookies_Desc", "Tự động ẩn và chặn các hộp thoại xin phép cookie (EasyList / Fanboy)", "https://secure.fanboy.co.nz/fanboy-cookiemonster.txt", "easylist_cookies.txt", false, 50),
        new("ublock-cookies-easylist", CategoryCookies, "subgroup-easylist-cookies", "Filter_ublock_cookies_easylist_Name", "uBlock filters – Cookie Notices", "Filter_ublock_cookies_easylist_Desc", "Bộ lọc thông báo cookie bổ sung của uBlock Origin", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/annoyances-cookies.txt", "ublock_cookies_easylist.txt", false, 51),
        // Sub-group: AdGuard/uBO – Cookie Notices
        new("adguard-cookies", CategoryCookies, "subgroup-adguard-cookies", "Filter_adguard_cookies_Name", "AdGuard – Cookie Notices", "Filter_adguard_cookies_Desc", "Bộ lọc thông báo cookie từ AdGuard Team", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_18_Annoyances_Cookies/filter.txt", "adguard_cookies.txt", false, 52),
        new("ublock-cookies-adguard", CategoryCookies, "subgroup-adguard-cookies", "Filter_ublock_cookies_adguard_Name", "uBlock filters – Cookie Notices", "Filter_ublock_cookies_adguard_Desc", "Bộ lọc thông báo cookie bổ sung kết hợp cùng AdGuard", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/annoyances-cookies.txt", "ublock_cookies_adguard.txt", false, 53),

        // =========================================================================
        // 7. Tiện ích xã hội / Social Widgets (CategorySocial)
        // =========================================================================
        new("easylist-social", CategorySocial, null, "Filter_easylist_social_Name", "EasyList – Social Widgets", "Filter_easylist_social_Desc", "Chặn các nút chia sẻ Facebook, Twitter, mạng xã hội (Fanboy Social)", "https://secure.fanboy.co.nz/fanboy-social.txt", "easylist_social.txt", false, 60),
        new("adguard-social", CategorySocial, null, "Filter_adguard_social_Name", "AdGuard – Social Widgets", "Filter_adguard_social_Desc", "Bộ lọc tiện ích mạng xã hội tiêu chuẩn từ AdGuard", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_4_Social/filter.txt", "adguard_social.txt", false, 61),
        new("fanboy-antifacebook", CategorySocial, null, "Filter_fanboy_antifacebook_Name", "Fanboy – Anti-Facebook", "Filter_fanboy_antifacebook_Desc", "Chặn hoàn toàn các tiện ích, theo dõi và nội dung nhúng Facebook", "https://secure.fanboy.co.nz/fanboy-antifacebook.txt", "fanboy_antifacebook.txt", false, 62),

        // =========================================================================
        // 8. Phiền toái / Annoyances (CategoryAnnoyances)
        // =========================================================================
        // Sub-group: EasyList – Annoyances
        new("easylist-ai", CategoryAnnoyances, "subgroup-easylist-annoyances", "Filter_easylist_ai_Name", "EasyList – AI Widgets", "Filter_easylist_ai_Desc", "Chặn các tiện ích chatbot AI và hộp thoại AI hỗ trợ nổi trên web", "https://secure.fanboy.co.nz/fanboy-ai.txt", "easylist_ai.txt", false, 70),
        new("easylist-chat", CategoryAnnoyances, "subgroup-easylist-annoyances", "Filter_easylist_chat_Name", "EasyList – Chat Widgets", "Filter_easylist_chat_Desc", "Chặn các hộp thoại hỗ trợ khách hàng và live chat tự bật", "https://secure.fanboy.co.nz/fanboy-chat.txt", "easylist_chat.txt", false, 71),
        new("easylist-newsletters", CategoryAnnoyances, "subgroup-easylist-annoyances", "Filter_easylist_newsletters_Name", "EasyList – Newsletter Notices", "Filter_easylist_newsletters_Desc", "Ẩn các pop-up xin đăng ký nhận email bản tin (newsletter)", "https://secure.fanboy.co.nz/fanboy-newsletters.txt", "easylist_newsletters.txt", false, 72),
        new("easylist-notifications", CategoryAnnoyances, "subgroup-easylist-annoyances", "Filter_easylist_notifications_Name", "EasyList – Notifications", "Filter_easylist_notifications_Desc", "Chặn các thông báo đẩy và nhắc nhở trên trình duyệt", "https://secure.fanboy.co.nz/fanboy-notifications.txt", "easylist_notifications.txt", false, 73),
        new("easylist-other-annoyances", CategoryAnnoyances, "subgroup-easylist-annoyances", "Filter_easylist_other_annoyances_Name", "EasyList – Other Annoyances", "Filter_easylist_other_annoyances_Desc", "Loại bỏ các phần tử gây phiền toái khác từ EasyList", "https://secure.fanboy.co.nz/fanboy-annoyance.txt", "easylist_other_annoyances.txt", false, 74),
        // Sub-group: AdGuard – Annoyances
        new("adguard-mobile-app-banners", CategoryAnnoyances, "subgroup-adguard-annoyances", "Filter_adguard_mobile_app_banners_Name", "AdGuard – Mobile App Banners", "Filter_adguard_mobile_app_banners_Desc", "Ẩn các biểu ngữ mời cài đặt ứng dụng di động", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_20_Annoyances_MobileAppBanners/filter.txt", "adguard_mobile_app_banners.txt", false, 75),
        new("adguard-other-annoyances", CategoryAnnoyances, "subgroup-adguard-annoyances", "Filter_adguard_other_annoyances_Name", "AdGuard – Other Annoyances", "Filter_adguard_other_annoyances_Desc", "Bộ lọc các yếu tố gây phiền toái khác từ AdGuard", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_22_Annoyances_Other/filter.txt", "adguard_other_annoyances.txt", false, 76),
        new("adguard-popup-overlays", CategoryAnnoyances, "subgroup-adguard-annoyances", "Filter_adguard_popup_overlays_Name", "AdGuard – Popup Overlays", "Filter_adguard_popup_overlays_Desc", "Chặn các lớp phủ pop-up chặn đọc nội dung trang", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_19_Annoyances_Popups/filter.txt", "adguard_popup_overlays.txt", false, 77),
        new("adguard-widgets", CategoryAnnoyances, "subgroup-adguard-annoyances", "Filter_adguard_widgets_Name", "AdGuard – Widgets", "Filter_adguard_widgets_Desc", "Ẩn các tiện ích nổi và widget gây mất tập trung từ AdGuard", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_21_Annoyances_Widgets/filter.txt", "adguard_widgets.txt", false, 78),
        // Standalone
        new("ublock-annoyances", CategoryAnnoyances, null, "Filter_ublock_annoyances_Name", "uBlock filters – Annoyances", "Filter_ublock_annoyances_Desc", "Bộ lọc loại bỏ phiền toái tổng hợp của uBlock Origin", "https://raw.githubusercontent.com/uBlockOrigin/uAssets/master/filters/annoyances.txt", "ublock_annoyances.txt", false, 79),

        // =========================================================================
        // 9. Khu vực, ngôn ngữ / Regions & Languages (CategoryRegions - 38 Lists)
        // =========================================================================
        new("reg-al", CategoryRegions, null, "Filter_reg_al_Name", "al, xk: Adblock List for Albania", "Filter_reg_al_Desc", "Bộ lọc quảng cáo cho các trang web Albania và Kosovo", "https://raw.githubusercontent.com/heradhis/indonesianadblockrules/master/subscriptions/albaniacountryspecificfilters.txt", "reg_al.txt", false, 100),
        new("reg-bg", CategoryRegions, null, "Filter_reg_bg_Name", "bg: Bulgarian Adblock list", "Filter_reg_bg_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Bulgaria", "https://raw.githubusercontent.com/alexstine/BulgarianAdblockRules/main/BulgarianAdblockRules.txt", "reg_bg.txt", false, 101),
        new("reg-cn", CategoryRegions, null, "Filter_reg_cn_Name", "cn, tw: AdGuard Chinese (中文)", "Filter_reg_cn_Desc", "Chặn quảng cáo trên các trang web tiếng Trung Quốc (AdGuard Chinese)", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_224_Chinese/filter.txt", "reg_cn.txt", false, 102),
        new("reg-cz", CategoryRegions, null, "Filter_reg_cz_Name", "cz, sk: EasyList Czech and Slovak", "Filter_reg_cz_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Séc và Slovakia", "https://raw.githubusercontent.com/tomasko126/easylistczechandslovak/master/filters.txt", "reg_cz.txt", false, 103),
        new("reg-de", CategoryRegions, null, "Filter_reg_de_Name", "de, ch, at: EasyList Germany", "Filter_reg_de_Desc", "Bộ lọc quảng cáo bổ sung cho các trang web tiếng Đức", "https://easylist.to/easylistgermany/easylistgermany.txt", "reg_de.txt", false, 104),
        new("reg-ee", CategoryRegions, null, "Filter_reg_ee_Name", "ee: Eesti saitidele kohandatud filter", "Filter_reg_ee_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Estonia", "https://adblock.ee/list.txt", "reg_ee.txt", false, 105),
        new("reg-eg", CategoryRegions, null, "Filter_reg_eg_Name", "eg, sa, ma, dz: Liste AR", "Filter_reg_eg_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Ả Rập", "https://raw.githubusercontent.com/easylist/liste_ar/master/liste_ar.txt", "reg_eg.txt", false, 106),
        new("reg-es-pt", CategoryRegions, null, "Filter_reg_es_pt_Name", "es, ar, br, pt: AdGuard Spanish/Portuguese", "Filter_reg_es_pt_Desc", "Bộ lọc quảng cáo cho tiếng Tây Ban Nha và Bồ Đào Nha", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_9_Spanish/filter.txt", "reg_es_pt.txt", false, 107),
        new("reg-es", CategoryRegions, null, "Filter_reg_es_Name", "es, ar, mx, co: EasyList Spanish", "Filter_reg_es_Desc", "Bộ lọc quảng cáo EasyList tiếng Tây Ban Nha", "https://easylist-downloads.adblockplus.org/easylistspanish.txt", "reg_es.txt", false, 108),
        new("reg-fi", CategoryRegions, null, "Filter_reg_fi_Name", "fi: Adblock List for Finland", "Filter_reg_fi_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Phần Lan", "https://raw.githubusercontent.com/finnish-easylist-addition/finnish-easylist-addition/master/Finland_adb.txt", "reg_fi.txt", false, 109),
        new("reg-fr", CategoryRegions, null, "Filter_reg_fr_Name", "fr, be, ca: AdGuard Français", "Filter_reg_fr_Desc", "Bộ lọc quảng cáo dành riêng cho các trang web tiếng Pháp", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_16_French/filter.txt", "reg_fr.txt", false, 110),
        new("reg-gr", CategoryRegions, null, "Filter_reg_gr_Name", "gr, cy: Greek AdBlock Filter", "Filter_reg_gr_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Hy Lạp", "https://raw.githubusercontent.com/kargig/greek-adblockplus-filter/master/greek-adblock-filters.txt", "reg_gr.txt", false, 111),
        new("reg-hr", CategoryRegions, null, "Filter_reg_hr_Name", "hr, rs: Dandelion Sprout's Serbo-Croatian filters", "Filter_reg_hr_Desc", "Bộ lọc cho các trang web Serbia, Croatia và Bosnia", "https://raw.githubusercontent.com/DandelionSprout/adfilt/master/Dandelion%20Sprout's%20Anti-Malware%20List/SerboCroatianList.txt", "reg_hr.txt", false, 112),
        new("reg-hu", CategoryRegions, null, "Filter_reg_hu_Name", "hu: hufilter", "Filter_reg_hu_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Hungary", "https://raw.githubusercontent.com/hufilter/hufilter/master/hufilter.txt", "reg_hu.txt", false, 113),
        new("reg-id", CategoryRegions, null, "Filter_reg_id_Name", "id, my: ABPindo", "Filter_reg_id_Desc", "Bộ lọc quảng cáo cho các trang web Indonesia và Malaysia", "https://raw.githubusercontent.com/ABPindo/indonesianadblockrules/master/subscriptions/abpindo.txt", "reg_id.txt", false, 114),
        new("reg-il", CategoryRegions, null, "Filter_reg_il_Name", "il: EasyList Hebrew", "Filter_reg_il_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Do Thái (Hebrew)", "https://raw.githubusercontent.com/easylist/easylisthebrew/master/easylisthebrew.txt", "reg_il.txt", false, 115),
        new("reg-in", CategoryRegions, null, "Filter_reg_in_Name", "in, lk, np: IndianList", "Filter_reg_in_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Ấn Độ, Sri Lanka, Nepal", "https://raw.githubusercontent.com/yous/indianlist/master/indianlist.txt", "reg_in.txt", false, 116),
        new("reg-ir", CategoryRegions, null, "Filter_reg_ir_Name", "ir: PersianBlocker", "Filter_reg_ir_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Ba Tư (Iran)", "https://raw.githubusercontent.com/MasterKia/PersianBlocker/main/PersianBlocker.txt", "reg_ir.txt", false, 117),
        new("reg-is", CategoryRegions, null, "Filter_reg_is_Name", "is: Icelandic ABP List", "Filter_reg_is_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Iceland", "https://adblock.gardar.net/is.abp.txt", "reg_is.txt", false, 118),
        new("reg-it", CategoryRegions, null, "Filter_reg_it_Name", "it: EasyList Italy", "Filter_reg_it_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Ý", "https://easylist-downloads.adblockplus.org/easylistitaly.txt", "reg_it.txt", false, 119),
        new("reg-jp", CategoryRegions, null, "Filter_reg_jp_Name", "jp: AdGuard Japanese", "Filter_reg_jp_Desc", "Chặn quảng cáo trên các trang web tiếng Nhật (AdGuard Japanese)", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_7_Japanese/filter.txt", "reg_jp.txt", false, 120),
        new("reg-kr", CategoryRegions, null, "Filter_reg_kr_Name", "kr: 한국어 (Korean)", "Filter_reg_kr_Desc", "Bộ lọc quảng cáo tối ưu cho các trang web tiếng Hàn Quốc (YousList)", "https://raw.githubusercontent.com/yous/YousList/master/youslist.txt", "reg_kr.txt", false, 121),
        new("reg-lt", CategoryRegions, null, "Filter_reg_lt_Name", "lt: EasyList Lithuania", "Filter_reg_lt_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Litva", "https://raw.githubusercontent.com/EasyList-Lithuania/easylist_lithuania/master/easylistlithuania.txt", "reg_lt.txt", false, 122),
        new("reg-lv", CategoryRegions, null, "Filter_reg_lv_Name", "lv: Latvian List", "Filter_reg_lv_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Latvia", "https://raw.githubusercontent.com/Latvian-List/adblock-latvian/master/list.txt", "reg_lv.txt", false, 123),
        new("reg-mk", CategoryRegions, null, "Filter_reg_mk_Name", "mk: Macedonian adBlock Filters", "Filter_reg_mk_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Macedonia", "https://raw.githubusercontent.com/thepman/macedonian-adblock-filters/master/filters.txt", "reg_mk.txt", false, 124),
        new("reg-nl", CategoryRegions, null, "Filter_reg_nl_Name", "nl, be: AdGuard Dutch", "Filter_reg_nl_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Hà Lan và Bỉ", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_8_Dutch/filter.txt", "reg_nl.txt", false, 125),
        new("reg-no", CategoryRegions, null, "Filter_reg_no_Name", "no, dk, is: Dandelion Sprouts nordiske filtre", "Filter_reg_no_Desc", "Bộ lọc cho các nước Bắc Âu (Na Uy, Đan Mạch, Iceland)", "https://raw.githubusercontent.com/DandelionSprout/adfilt/master/NorwegianList.txt", "reg_no.txt", false, 126),
        // Sub-group: pl: Oficjalne Polskie Filtry (2 items)
        new("reg-pl-cert", CategoryRegions, "subgroup-pl", "Filter_reg_pl_cert_Name", "pl: CERT.PL's Warning List", "Filter_reg_pl_cert_Desc", "Danh sách cảnh báo tên miền độc hại của CERT Ba Lan", "https://hole.cert.pl/domains/domains_adblock.txt", "reg_pl_cert.txt", false, 127),
        new("reg-pl-polskie", CategoryRegions, "subgroup-pl", "Filter_reg_pl_polskie_Name", "pl: Oficjalne Polskie Filtry do uBlocka Origin", "Filter_reg_pl_polskie_Desc", "Bộ lọc chính thức cho uBlock Origin tại Ba Lan", "https://raw.githubusercontent.com/olegwukr/polish-privacy-filters/master/adblock_adguard.txt", "reg_pl_polskie.txt", false, 128),
        new("reg-ro", CategoryRegions, null, "Filter_reg_ro_Name", "ro, md: Romanian Ad (ROad) Block List Light", "Filter_reg_ro_Desc", "Bộ lọc quảng cáo cho các trang web Romania và Moldova", "https://raw.githubusercontent.com/tcptomato/ROad-Block/master/road-block-filters.txt", "reg_ro.txt", false, 129),
        // Sub-group: ru, ua, uz, kz: RU AdList (2 items)
        new("reg-ru-adlist", CategoryRegions, "subgroup-ru", "Filter_reg_ru_adlist_Name", "ru, ua, uz, kz: RU AdList", "Filter_reg_ru_adlist_Desc", "Bộ lọc chính cho các trang web tiếng Nga và khu vực SNG", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_1_Russian/filter.txt", "reg_ru_adlist.txt", false, 130),
        new("reg-ru-counters", CategoryRegions, "subgroup-ru", "Filter_reg_ru_counters_Name", "ru, ua, uz, kz: RU AdList: Counters", "Filter_reg_ru_counters_Desc", "Chặn các bộ đếm và phân tích lưu lượng tại khu vực tiếng Nga", "https://raw.githubusercontent.com/easylist/ruadlist/master/advblock/adblock_social.txt", "reg_ru_counters.txt", false, 131),
        new("reg-se", CategoryRegions, null, "Filter_reg_se_Name", "se: Frellwit's Swedish Filter", "Filter_reg_se_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Thụy Điển", "https://raw.githubusercontent.com/lassekongo83/Frellwits-filter-lists/master/Frellwits-Swedish-Filter.txt", "reg_se.txt", false, 132),
        new("reg-si", CategoryRegions, null, "Filter_reg_si_Name", "si: Slovenian List", "Filter_reg_si_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Slovenia", "https://raw.githubusercontent.com/slovenian-list/slovenian-list/master/filters.txt", "reg_si.txt", false, 133),
        new("reg-th", CategoryRegions, null, "Filter_reg_th_Name", "th: EasyList Thailand", "Filter_reg_th_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Thái Lan", "https://raw.githubusercontent.com/easylist-thailand/easylist-thailand/master/subscription/easylist-thailand.txt", "reg_th.txt", false, 134),
        new("reg-tr", CategoryRegions, null, "Filter_reg_tr_Name", "tr: AdGuard Turkish", "Filter_reg_tr_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Thổ Nhĩ Kỳ", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_13_Turkish/filter.txt", "reg_tr.txt", false, 135),
        new("reg-ua", CategoryRegions, null, "Filter_reg_ua_Name", "ua: AdGuard Ukrainian", "Filter_reg_ua_Desc", "Bộ lọc quảng cáo cho các trang web tiếng Ukraina", "https://raw.githubusercontent.com/AdguardTeam/FiltersRegistry/master/filters/filter_24_Ukrainian/filter.txt", "reg_ua.txt", false, 136),
        new("reg-vn", CategoryRegions, null, "Filter_reg_vn_Name", "vn: ABPVN List", "Filter_reg_vn_Desc", "Bộ lọc tối ưu hóa dành riêng cho các trang web và báo điện tử tại Việt Nam", "https://raw.githubusercontent.com/abpvn/abpvn/master/filter/abpvn.txt", "abpvn_basic.txt", true, 137)
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
        "reg-vn",
        "youtube-adblock"
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
        "reg-vn",
        "youtube-adblock"
    };

    public static IReadOnlySet<string> AdvancedPresetFilterIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Standard (9)
        "ublock-filters",
        "ublock-badware",
        "ublock-privacy",
        "ublock-quick-fixes",
        "ublock-unbreak",
        "easylist",
        "easyprivacy",
        "reg-vn",
        "youtube-adblock",
        // Ads & Security
        "adguard-base",
        "malicious-urls",
        "phishing-urls",
        // Cookies & Annoyances
        "easylist-cookies",
        "easylist-other-annoyances",
        "adguard-mobile-app-banners",
        "adguard-other-annoyances",
        "adguard-popup-overlays",
        "adguard-widgets"
    };

    public static IReadOnlySet<string> MaxPresetFilterIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // All Advanced (17)
        "ublock-filters",
        "ublock-badware",
        "ublock-privacy",
        "ublock-quick-fixes",
        "ublock-unbreak",
        "easylist",
        "easyprivacy",
        "reg-vn",
        "youtube-adblock",
        "adguard-base",
        "malicious-urls",
        "phishing-urls",
        "easylist-cookies",
        "easylist-other-annoyances",
        "adguard-mobile-app-banners",
        "adguard-other-annoyances",
        "adguard-popup-overlays",
        "adguard-widgets",
        // Privacy & Multipurpose
        "adguard-tracking",
        "adguard-mobile",
        "block-lan",
        "dan-pollock",
        "peter-lowe",
        // Social
        "easylist-social",
        "fanboy-antifacebook",
        // All remaining Cookies
        "ublock-cookies-easylist",
        "adguard-cookies",
        "ublock-cookies-adguard",
        // All remaining Annoyances
        "easylist-ai",
        "easylist-chat",
        "easylist-newsletters",
        "easylist-notifications",
        "ublock-annoyances"
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
