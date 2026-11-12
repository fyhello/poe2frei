using System.Reflection;
using System.Security.Cryptography;

namespace FreiAtlas.Licensing;

public sealed record LicenseBuildConfig(
    string ProductId,
    string ServerUrl,
    string PublicKeyPem,
    bool StrictBuild)
{
    public const string DefaultProductId = "frei";
    public const string DefaultServerUrl = "https://mf.fyhello.shop";

    public static LicenseBuildConfig FromEnvironment()
    {
#if DEBUG
        var useDevOverrides = string.Equals(
            Environment.GetEnvironmentVariable("FREI_ATLAS_LICENSE_DEV_OVERRIDES"),
            "1",
            StringComparison.Ordinal);
        if (useDevOverrides)
        {
            var productId = Environment.GetEnvironmentVariable("FREI_ATLAS_LICENSE_PRODUCT_ID");
            var serverUrl = Environment.GetEnvironmentVariable("FREI_ATLAS_LICENSE_SERVER_URL");
            var publicKeyPem = Environment.GetEnvironmentVariable("FREI_ATLAS_LICENSE_PUBLIC_KEY_PEM");
            return new LicenseBuildConfig(
                string.IsNullOrWhiteSpace(productId) ? DefaultProductId : productId.Trim(),
                string.IsNullOrWhiteSpace(serverUrl) ? DefaultServerUrl : serverUrl.Trim(),
                publicKeyPem?.Trim() ?? string.Empty,
                StrictBuild: false);
        }
#endif

        return new LicenseBuildConfig(
            DefaultProductId,
            DefaultServerUrl,
            ReadEmbeddedPublicKey(),
            StrictBuild: true);
    }

    public bool IsConfigured
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProductId) || !ProductId.All(IsProductIdChar))
            {
                return false;
            }

            if (StrictBuild)
            {
                if (!string.Equals(ProductId, DefaultProductId, StringComparison.Ordinal)
                    || !string.Equals(ServerUrl, DefaultServerUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var embeddedPublicKey = ReadEmbeddedPublicKey().Trim();
                if (string.IsNullOrWhiteSpace(embeddedPublicKey)
                    || !string.Equals(PublicKeyPem.Trim(), embeddedPublicKey, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            if (ServerUrl.Contains("workers.dev", StringComparison.OrdinalIgnoreCase)
                || ServerUrl.Contains("tcloudbase.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(PublicKeyPem))
            {
                return false;
            }

            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(PublicKeyPem);
                return rsa.KeySize >= 2048;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    private static bool IsProductIdChar(char value)
        => char.IsAsciiLetterLower(value) || char.IsAsciiDigit(value) || value == '-';

    private static string ReadEmbeddedPublicKey()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FreiAtlas.Licensing.Resources.frei-public.pem");
        if (stream is null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
