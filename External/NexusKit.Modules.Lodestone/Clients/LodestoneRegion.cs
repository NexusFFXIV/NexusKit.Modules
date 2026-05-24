using System.Globalization;

namespace NexusKit.Modules.Lodestone.Clients;

/// <summary>
/// Maps the active plugin culture to Lodestone's regional sub-domain. Lodestone serves
/// item / mount / minion / achievement names in the regional language; matching the
/// plugin's UI language gives the user gear and collectables in their own language.
/// </summary>
internal static class LodestoneRegion
{
    public const string DefaultBaseAddress = "https://eu.finalfantasyxiv.com/";

    public static string BaseAddressFor(CultureInfo culture)
        => TwoLetterCode(culture) switch
        {
            "de" => "https://de.finalfantasyxiv.com/",
            "fr" => "https://fr.finalfantasyxiv.com/",
            "ja" => "https://jp.finalfantasyxiv.com/",
            _    => DefaultBaseAddress, // en (eu/na share content)
        };

    /// <summary>Stable short tag for cache keys: <c>de</c>, <c>fr</c>, <c>ja</c>, or <c>en</c>.</summary>
    public static string CacheTag(CultureInfo culture)
        => TwoLetterCode(culture) switch
        {
            "de" => "de",
            "fr" => "fr",
            "ja" => "ja",
            _    => "en",
        };

    private static string TwoLetterCode(CultureInfo culture)
        => culture.TwoLetterISOLanguageName?.ToLowerInvariant() ?? "en";
}
