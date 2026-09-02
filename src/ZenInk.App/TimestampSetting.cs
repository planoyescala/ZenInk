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

    private const string LtvKey = "zenink.ltv.on";

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

    /// <summary>
    /// Whether to store the proof that the certificates were good along with
    /// the signature, so it can be checked years later without asking anybody.
    ///
    /// Off by default and for the same reason as the timestamp: it asks
    /// somebody else's server a question, and that is the reader's call to
    /// make. Unlike the timestamp there is nothing to configure — the
    /// certificate says which responder answers for it.
    /// </summary>
    public static bool KeepValidationData
    {
        get => Read(LtvKey) == bool.TrueString;
        set => Write(LtvKey, value ? bool.TrueString : bool.FalseString);
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
