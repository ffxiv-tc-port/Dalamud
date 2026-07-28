using Dalamud.Game;
using Lumina.Data;

namespace Dalamud.Utility;

/// <summary>
/// Extension methods for the <see cref="ClientLanguage"/> class.
/// </summary>
public static class ClientLanguageExtensions
{
    /// <summary>
    /// Converts a Dalamud ClientLanguage to the corresponding Lumina variant.
    /// </summary>
    /// <param name="language">Language to convert.</param>
    /// <returns>Converted language.</returns>
    public static Language ToLumina(this ClientLanguage language)
    {
        return language switch
        {
            ClientLanguage.Japanese           => Language.Japanese,
            ClientLanguage.English            => Language.English,
            ClientLanguage.German             => Language.German,
            ClientLanguage.French             => Language.French,
            ClientLanguage.ChineseSimplified  => Language.ChineseSimplified,
            ClientLanguage.ChineseTraditional => Language.ChineseTraditional,
            _                                 => Language.ChineseTraditional,
        };
    }

    /// <summary>
    /// Gets the language code from a ClientLanguage.
    /// </summary>
    /// <param name="value">The ClientLanguage to convert.</param>
    /// <returns>The language code (ja, en, de, fr).</returns>
    /// <exception cref="ArgumentOutOfRangeException">An exception that is thrown when no valid ClientLanguage was given.</exception>
    public static string ToCode(this ClientLanguage value)
    {
        return value switch
        {
            ClientLanguage.Japanese           => "ja",
            ClientLanguage.English            => "en",
            ClientLanguage.German             => "de",
            ClientLanguage.French             => "fr",
            ClientLanguage.ChineseSimplified  => "chs",
            ClientLanguage.ChineseTraditional => "tc",
            _                                 => "tc",
        };
    }

    /// <summary>
    /// Gets the ClientLanguage from a language code.
    /// </summary>
    /// <param name="value">The language code to convert (ja, en, de, fr).</param>
    /// <returns>The ClientLanguage.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An exception that is thrown when no valid language code was given.</exception>
    public static ClientLanguage ToClientLanguage(this string value)
    {
        return value switch
        {
            "ja"  => ClientLanguage.Japanese,
            "en"  => ClientLanguage.English,
            "de"  => ClientLanguage.German,
            "fr"  => ClientLanguage.French,
            "chs" => ClientLanguage.ChineseSimplified,
            "tc" => ClientLanguage.ChineseTraditional,
            _     => ClientLanguage.ChineseTraditional,
        };
    }
}
