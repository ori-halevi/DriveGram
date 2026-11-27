using System.Globalization;
using DriveGram.Host;

namespace DriveGram.Tray.Setup;

/// <summary>A country in the phone field: its two letters, its calling code, and its name.</summary>
sealed record Country(string Iso, string Dial, string Name)
{
    public string Plus => "+" + Dial;
}

/// <summary>
/// The countries of the phone field. The calling codes are the list below; the names come from Windows
/// itself (its English names, which is what a phone picker shows everywhere), and a short list of Hebrew
/// names lets someone typing in Hebrew find their country too.
/// </summary>
static class Countries
{
    // ISO code + calling code. Places with no dialling of their own (Antarctic territories, the EU flag)
    // are not here, even though the picture sheet holds their flag.
    const string Codes =
        "AD376 AE971 AF93 AG1268 AI1264 AL355 AM374 AO244 AR54 AS1684 AT43 AU61 AW297 AX358 AZ994 " +
        "BA387 BB1246 BD880 BE32 BF226 BG359 BH973 BI257 BJ229 BL590 BM1441 BN673 BO591 BR55 BS1242 BT975 BW267 BY375 BZ501 " +
        "CA1 CC61 CD243 CF236 CG242 CH41 CI225 CK682 CL56 CM237 CN86 CO57 CR506 CU53 CV238 CW599 CX61 CY357 CZ420 " +
        "DE49 DJ253 DK45 DM1767 DO1809 DZ213 EC593 EE372 EG20 ER291 ES34 ET251 " +
        "FI358 FJ679 FK500 FM691 FO298 FR33 " +
        "GA241 GB44 GD1473 GE995 GF594 GG44 GH233 GI350 GL299 GM220 GN224 GP590 GQ240 GR30 GT502 GU1671 GW245 GY592 " +
        "HK852 HN504 HR385 HT509 HU36 ID62 IE353 IL972 IM44 IN91 IO246 IQ964 IR98 IS354 IT39 " +
        "JE44 JM1876 JO962 JP81 KE254 KG996 KH855 KI686 KM269 KN1869 KP850 KR82 KW965 KY1345 KZ7 " +
        "LA856 LB961 LC1758 LI423 LK94 LR231 LS266 LT370 LU352 LV371 LY218 " +
        "MA212 MC377 MD373 ME382 MF590 MG261 MH692 MK389 ML223 MM95 MN976 MO853 MP1670 MQ596 MR222 MS1664 MT356 MU230 MV960 MW265 MX52 MY60 MZ258 " +
        "NA264 NC687 NE227 NF672 NG234 NI505 NL31 NO47 NP977 NR674 NU683 NZ64 OM968 " +
        "PA507 PE51 PF689 PG675 PH63 PK92 PL48 PM508 PN64 PR1787 PS970 PT351 PW680 PY595 QA974 " +
        "RE262 RO40 RS381 RU7 RW250 " +
        "SA966 SB677 SC248 SD249 SE46 SG65 SH290 SI386 SJ47 SK421 SL232 SM378 SN221 SO252 SR597 SS211 ST239 SV503 SX1721 SY963 SZ268 " +
        "TC1649 TD235 TG228 TH66 TJ992 TK690 TL670 TM993 TN216 TO676 TR90 TT1868 TV688 TW886 TZ255 " +
        "UA380 UG256 US1 UY598 UZ998 VA39 VC1784 VE58 VG1284 VI1340 VN84 VU678 WF681 WS685 XK383 YE967 YT262 ZA27 ZM260 ZW263";

    /// <summary>Hebrew names for the countries someone here is likely to type in Hebrew. Everything else is
    /// found by its English name, its two letters, or its calling code.</summary>
    static readonly Dictionary<string, string> Hebrew = new()
    {
        ["IL"] = "ישראל", ["US"] = "ארצות הברית", ["GB"] = "בריטניה", ["RU"] = "רוסיה", ["UA"] = "אוקראינה",
        ["FR"] = "צרפת", ["DE"] = "גרמניה", ["IT"] = "איטליה", ["ES"] = "ספרד", ["PT"] = "פורטוגל",
        ["NL"] = "הולנד", ["BE"] = "בלגיה", ["CH"] = "שווייץ", ["AT"] = "אוסטריה", ["SE"] = "שוודיה",
        ["NO"] = "נורווגיה", ["DK"] = "דנמרק", ["FI"] = "פינלנד", ["PL"] = "פולין", ["CZ"] = "צ'כיה",
        ["HU"] = "הונגריה", ["RO"] = "רומניה", ["BG"] = "בולגריה", ["GR"] = "יוון", ["TR"] = "טורקיה",
        ["CY"] = "קפריסין", ["CA"] = "קנדה", ["MX"] = "מקסיקו", ["BR"] = "ברזיל", ["AR"] = "ארגנטינה",
        ["AU"] = "אוסטרליה", ["NZ"] = "ניו זילנד", ["ZA"] = "דרום אפריקה", ["IN"] = "הודו", ["CN"] = "סין",
        ["JP"] = "יפן", ["KR"] = "דרום קוריאה", ["TH"] = "תאילנד", ["PH"] = "הפיליפינים", ["VN"] = "וייטנאם",
        ["AE"] = "איחוד האמירויות", ["SA"] = "ערב הסעודית", ["EG"] = "מצרים", ["JO"] = "ירדן", ["MA"] = "מרוקו",
        ["ET"] = "אתיופיה", ["GE"] = "גאורגיה", ["AM"] = "ארמניה", ["AZ"] = "אזרבייג'ן", ["BY"] = "בלארוס",
        ["MD"] = "מולדובה", ["LV"] = "לטביה", ["LT"] = "ליטא", ["EE"] = "אסטוניה", ["IE"] = "אירלנד",
        ["IS"] = "איסלנד", ["SG"] = "סינגפור", ["HK"] = "הונג קונג", ["CL"] = "צ'ילה", ["CO"] = "קולומביה",
        ["PE"] = "פרו", ["UY"] = "אורוגוואי", ["PA"] = "פנמה", ["CR"] = "קוסטה ריקה", ["DO"] = "הרפובליקה הדומיניקנית",
        ["RS"] = "סרביה", ["HR"] = "קרואטיה", ["SI"] = "סלובניה", ["SK"] = "סלובקיה", ["UZ"] = "אוזבקיסטן",
        ["KZ"] = "קזחסטן", ["PK"] = "פקיסטן", ["BD"] = "בנגלדש", ["LK"] = "סרי לנקה", ["NG"] = "ניגריה",
        ["KE"] = "קניה", ["GH"] = "גאנה", ["TZ"] = "טנזניה", ["UG"] = "אוגנדה", ["MY"] = "מלזיה",
        ["ID"] = "אינדונזיה", ["TW"] = "טאיוואן", ["QA"] = "קטאר", ["KW"] = "כווית", ["BH"] = "בחריין",
        ["OM"] = "עומאן", ["LB"] = "לבנון", ["IQ"] = "עיראק", ["IR"] = "איראן", ["AF"] = "אפגניסטן",
        ["LU"] = "לוקסמבורג", ["MT"] = "מלטה", ["MC"] = "מונקו", ["AL"] = "אלבניה", ["MK"] = "צפון מקדוניה",
        ["BA"] = "בוסניה", ["ME"] = "מונטנגרו", ["TN"] = "תוניסיה", ["DZ"] = "אלג'יריה", ["LY"] = "לוב",
        ["SD"] = "סודאן", ["SY"] = "סוריה", ["YE"] = "תימן", ["NP"] = "נפאל", ["MM"] = "מיאנמר",
        ["KH"] = "קמבודיה", ["MN"] = "מונגוליה",
    };

    static List<Country>? _all;
    static bool _hebrewNames;

    /// <summary>Every country, by name. Built again when the language changes.</summary>
    public static IReadOnlyList<Country> All
    {
        get
        {
            if (_all is not null && _hebrewNames == L.Hebrew) return _all;
            _hebrewNames = L.Hebrew;
            var list = new List<Country>();
            foreach (var entry in Codes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var iso = entry[..2];
                var dial = entry[2..];
                list.Add(new Country(iso, dial, NameOf(iso)));
            }
            return _all = list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    static string NameOf(string iso)
    {
        if (L.Hebrew && Hebrew.TryGetValue(iso, out var he)) return he;
        try { return new RegionInfo(iso).EnglishName; }
        catch (ArgumentException) { return iso; }
    }

    /// <summary>What this PC's Windows says the user's country is (Israel if it says nothing useful).</summary>
    public static Country Here()
    {
        string iso;
        try { iso = RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch { iso = "IL"; }
        return Find(iso) ?? Find("IL") ?? All[0];
    }

    public static Country? Find(string iso) => All.FirstOrDefault(c => c.Iso.Equals(iso, StringComparison.OrdinalIgnoreCase));

    /// <summary>The countries matching what was typed: a name (in either language), two letters, or a code.</summary>
    public static IEnumerable<Country> Search(string text)
    {
        text = text.Trim().TrimStart('+');
        if (text.Length == 0) return All;
        return All.Where(c => c.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                              || (!L.Hebrew && Hebrew.TryGetValue(c.Iso, out var he) && he.Contains(text))
                              || (L.Hebrew && SafeEnglish(c.Iso).Contains(text, StringComparison.OrdinalIgnoreCase))
                              || c.Iso.Equals(text, StringComparison.OrdinalIgnoreCase)
                              || c.Dial.StartsWith(text, StringComparison.Ordinal));
    }

    static string SafeEnglish(string iso)
    {
        try { return new RegionInfo(iso).EnglishName; }
        catch (ArgumentException) { return iso; }
    }

    /// <summary>
    /// The country a typed number belongs to: the longest calling code it starts with. "+9725…" is Israel,
    /// and "+1264…" is Anguilla rather than the United States.
    /// </summary>
    public static Country? OfNumber(string number)
    {
        var digits = new string(number.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return null;
        return All.Where(c => digits.StartsWith(c.Dial, StringComparison.Ordinal))
            .OrderByDescending(c => c.Dial.Length)
            .ThenBy(Shared) // several countries share a code (+1, +7): the one most people mean
            .FirstOrDefault();
    }

    static int Shared(Country c) => c.Iso switch { "US" => 0, "RU" => 0, "CA" => 1, _ => 2 };
}
