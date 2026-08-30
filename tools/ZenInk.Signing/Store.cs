using System.Security.Cryptography.X509Certificates;
using ZenInk.Core;

namespace ZenInk.Signing;

/// <summary>
/// The certificates on this machine, listed and picked from.
///
/// This is the only part of signing that cannot be checked by the test suite:
/// it depends on what the user has installed. An FNMT certificate turns up here
/// once its installer has run.
/// </summary>
public static class Store
{
    public static int List()
    {
        var usable = PdfSignatures.AvailableCertificates();

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        foreach (var certificate in store.Certificates)
        {
            bool serves = usable.Any(c => c.Thumbprint == certificate.Thumbprint);
            Console.WriteLine(
                $"{(serves ? "sirve " : "      ")} "
                + $"{certificate.GetNameInfo(X509NameType.SimpleName, false),-44} "
                + $"caduca {certificate.NotAfter:yyyy-MM-dd}  "
                + $"{certificate.Thumbprint[..8]}  "
                + $"emisor {certificate.GetNameInfo(X509NameType.SimpleName, true)}");
        }

        Console.WriteLine($"\n{usable.Count} certificado(s) con los que se puede firmar.");
        return 0;
    }

    /// <summary>Picks one by thumbprint or by a piece of its name; the only one, if there is only one.</summary>
    public static X509Certificate2? Pick(string? which)
    {
        var usable = PdfSignatures.AvailableCertificates();
        if (usable.Count == 0)
        {
            Console.Error.WriteLine("No hay ningún certificado con el que firmar en el almacén del usuario.");
            return null;
        }

        if (which is null)
        {
            if (usable.Count == 1) return usable[0];

            Console.Error.WriteLine("Hay varios certificados. Di cuál, por huella o por parte del nombre:");
            List();
            return null;
        }

        var matches = usable
            .Where(c => c.Thumbprint.StartsWith(which, StringComparison.OrdinalIgnoreCase)
                        || c.GetNameInfo(X509NameType.SimpleName, false)
                            .Contains(which, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 1) return matches[0];

        Console.Error.WriteLine(matches.Count == 0
            ? $"Ningún certificado coincide con «{which}»."
            : $"«{which}» coincide con {matches.Count} certificados.");
        return null;
    }
}
