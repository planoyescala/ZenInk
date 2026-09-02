using Windows.Storage;

namespace ZenInk_App;

/// <summary>
/// The timestamping authority to ask, remembered between runs.
///
/// It is a setting rather than something built in because it is a decision
/// nobody else can make for the reader: whose clock a signature leans on is a
/// legal question in the reader's own country, not a technical one, and a
/// default authority chosen here would be a stranger silently vouching for
/// every drawing they sign.
///
/// Empty means no timestamp, which is what a signature has always been in this
/// program: valid while the certificate is.
/// </summary>
public static class TimestampSetting
{
    private const string UrlKey = "zenink.tsa.url";

    private const string OnKey = "zenink.tsa.on";

    public static string Url
    {
        get => Read(UrlKey) ?? string.Empty;
        set => Write(UrlKey, value.Trim());
    }

    /// <summary>Whether to ask for one. Off until the reader says otherwise, and there is nowhere to ask.</summary>
    public static bool Wanted
    {
        get => Read(OnKey) == bool.TrueString && Url.Length > 0;
        set => Write(OnKey, value ? bool.TrueString : bool.FalseString);
    }

    private static string? Read(string key)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values[key] as string;
        }
        catch
        {
            // An unreadable setting is not worth failing a signature over.
            return null;
        }
    }

    private static void Write(string key, string value)
    {
        try
        {
            ApplicationData.Current.LocalSettings.Values[key] = value;
        }
        catch
        {
            // Losing the preference is survivable; crashing while signing is not.
        }
    }
}
