using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreiAtlas.Licensing;

public sealed class LicenseValidationException : Exception
{
    public LicenseValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record LicenseTokenPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("request_nonce")] string RequestNonce,
    [property: JsonPropertyName("machine_hash")] string MachineHash,
    [property: JsonPropertyName("license_id")] string LicenseId,
    [property: JsonPropertyName("issued_at")] long IssuedAt,
    [property: JsonPropertyName("expires_at")] long ExpiresAt,
    [property: JsonPropertyName("trial_ends_at")] long TrialEndsAt,
    [property: JsonPropertyName("license_expires_at")] long LicenseExpiresAt,
    [property: JsonPropertyName("server_time")] long ServerTime);

public sealed class LicenseTokenVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly string _publicKeyPem;

    public LicenseTokenVerifier(string publicKeyPem)
    {
        _publicKeyPem = publicKeyPem ?? string.Empty;
    }

    public LicenseTokenPayload Verify(
        string token,
        string expectedProduct,
        string expectedMachineHash,
        string expectedRequestNonce)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new LicenseValidationException("empty_token", "授权 token 为空。");
        }

        var firstDot = token.IndexOf('.');
        if (firstDot <= 0 || token.IndexOf('.', firstDot + 1) >= 0)
        {
            throw new LicenseValidationException("invalid_token_format", "授权 token 格式无效。");
        }

        var payloadSegment = token[..firstDot];
        var signatureSegment = token[(firstDot + 1)..];
        byte[] signature;
        try
        {
            signature = Base64UrlDecode(signatureSegment);
        }
        catch (FormatException)
        {
            throw new LicenseValidationException("invalid_signature_encoding", "授权签名编码无效。");
        }

        if (string.IsNullOrWhiteSpace(_publicKeyPem))
        {
            throw new LicenseValidationException("missing_public_key", "授权公钥未配置。");
        }

        bool signatureValid;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(_publicKeyPem);
            signatureValid = rsa.VerifyData(
                Encoding.ASCII.GetBytes(payloadSegment),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            throw new LicenseValidationException("invalid_public_key", "授权公钥无效。");
        }

        if (!signatureValid)
        {
            throw new LicenseValidationException("invalid_signature", "授权签名验证失败。");
        }

        LicenseTokenPayload? payload;
        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(payloadSegment));
            payload = JsonSerializer.Deserialize<LicenseTokenPayload>(payloadJson, JsonOptions);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new LicenseValidationException("invalid_payload", "授权载荷格式无效。");
        }

        if (payload is null)
        {
            throw new LicenseValidationException("invalid_payload", "授权载荷为空。");
        }

        if (!string.Equals(payload.Product, expectedProduct, StringComparison.Ordinal))
        {
            throw new LicenseValidationException("wrong_product", "授权 token 属于其他产品。");
        }

        if (!string.Equals(payload.MachineHash, expectedMachineHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new LicenseValidationException("machine_mismatch", "授权 token 绑定了其他设备。");
        }

        if (!string.Equals(payload.RequestNonce, expectedRequestNonce, StringComparison.Ordinal))
        {
            throw new LicenseValidationException("invalid_response_nonce", "授权响应不是针对当前请求。");
        }

        if (payload.ServerTime <= 0)
        {
            throw new LicenseValidationException("invalid_server_time", "授权载荷缺少服务端时间。");
        }

        if (payload.ExpiresAt <= 0)
        {
            throw new LicenseValidationException("invalid_token_expiry", "授权 token 缺少到期时间。");
        }

        if (payload.Status is not ("active" or "trial"))
        {
            throw new LicenseValidationException("invalid_status", "授权状态无效。");
        }

        return payload;
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = (4 - normalized.Length % 4) % 4;
        return Convert.FromBase64String(normalized + new string('=', padding));
    }
}
