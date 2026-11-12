using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FreiAtlas.Licensing;

public sealed record LicensePersistedState(
    string ProductId,
    string Token,
    string RenewalSecret,
    string MachineHash,
    long LicenseExpiresAt,
    long TrialEndsAt,
    long TokenExpiresAt,
    long LastServerTime,
    long LastSuccessUtcSeconds,
    string LastHardRejectionCode,
    string LastHardRejectionMessage,
    string LastValidationFailure);

public sealed class LicenseStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public LicenseStateStore(string? path = null)
    {
        Path = path ?? DefaultPath();
    }

    public string Path { get; }

    public static string DefaultPath()
    {
        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FreiAtlas",
            "license",
            "frei-state.bin");
    }

    public LicensePersistedState? Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(Path);
            if (protectedBytes.Length == 0)
            {
                return null;
            }

            var plaintext = Dpapi.Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<LicensePersistedState>(Encoding.UTF8.GetString(plaintext), JsonOptions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return null;
        }
    }

    public void Save(LicensePersistedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var protectedBytes = Dpapi.Protect(Encoding.UTF8.GetBytes(json));
        var temporaryPath = Path + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, Path, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
