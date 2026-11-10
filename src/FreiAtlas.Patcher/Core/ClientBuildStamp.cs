using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;

namespace FreiAtlas.Patcher.Core;

/// <summary>
/// 客户端构建戳：用 Authenticode 签名而不是日志版本来判断「是不是同一个客户端构建」。
///
/// 原理：游戏每次更新都会替换并重新签名 exe，PE 证书表（PKCS#7）随之改变；
/// 本工具打补丁只改 Content.ggpk，不碰 exe，所以戳保持不变。
///
/// 读取成本只有证书表本身（约 10 KB），不需要解析 100 MB 索引，也不依赖日志。
/// 无签名的渠道客户端回退到 HashCache.dat，再回退到根目录可执行文件列表。
/// </summary>
public sealed record ClientBuildStamp(
    string Stamp,
    string? Signer,
    DateTimeOffset? SignedAt,
    string Source)
{
    public static ClientBuildStamp Empty { get; } = new(string.Empty, null, null, "none");

    private static readonly string[] CandidateExecutables =
    [
        "PathOfExile.exe",
        "PathOfExile_x64.exe",
        "Client.exe"
    ];

    public static ClientBuildStamp Compute(GameModeInfo info)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.GameDirectory))
        {
            return Empty;
        }

        // 1. 首选：exe 的 Authenticode 证书表（每个构建唯一，且不受补丁影响）
        foreach (var name in CandidateExecutables)
        {
            string path = Path.Combine(info.GameDirectory, name);
            if (!File.Exists(path)) continue;

            byte[]? certificateTable = TryReadCertificateTable(path);
            if (certificateTable is null || certificateTable.Length == 0) continue;

            string stamp = "sig:" + Convert.ToHexString(SHA256.HashData(certificateTable));
            var (signer, signedAt) = TryParseSignature(certificateTable);
            return new ClientBuildStamp(stamp, signer, signedAt, name);
        }

        // 2. 回退：HashCache.dat（游戏维护的根文件哈希清单，不含 Content.ggpk）
        string hashCache = Path.Combine(info.GameDirectory, "HashCache.dat");
        if (File.Exists(hashCache))
        {
            try
            {
                string stamp = "hashcache:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(hashCache)));
                return new ClientBuildStamp(stamp, null, null, "HashCache.dat");
            }
            catch
            {
                // 读不到就继续走根文件列表回退。
            }
        }

        // 3. 回退：根目录可执行文件列表（名称 + 大小 + 最后写入时间）
        try
        {
            var files = Directory.EnumerateFiles(info.GameDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(p =>
                {
                    string ext = Path.GetExtension(p);
                    return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                           || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                           || Path.GetFileName(p).Equals("signature.bin", StringComparison.OrdinalIgnoreCase);
                })
                .Select(p => new FileInfo(p))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => $"{f.Name}|{f.Length}|{f.LastWriteTimeUtc.Ticks}");

            var text = new StringBuilder();
            foreach (var line in files) text.AppendLine(line);

            if (text.Length == 0) return Empty;

            string stamp = "files:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
            return new ClientBuildStamp(stamp, null, null, "root-files");
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>用于界面展示的简短描述，例如「Grinding Gear Games Limited / 2026-11-06 11:25」。</summary>
    public string Describe()
    {
        if (string.IsNullOrEmpty(Stamp)) return "未知构建";

        string signer = string.IsNullOrWhiteSpace(Signer) ? Source : Signer;
        if (signer.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            int comma = signer.IndexOf(',');
            signer = comma > 0 ? signer[3..comma] : signer[3..];
        }

        return SignedAt is null
            ? signer
            : $"{signer} / {SignedAt.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    /// <summary>读取 PE 的 Certificate Table（Data Directory 第 4 项，文件偏移而非 RVA）。</summary>
    internal static byte[]? TryReadCertificateTable(string executablePath)
    {
        try
        {
            using var stream = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40) return null;

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 24L > stream.Length) return null;

            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) return null; // "PE\0\0"

            stream.Position = peOffset + 4 + 20;
            ushort magic = reader.ReadUInt16();
            int dataDirectoryOffset = magic switch
            {
                0x20B => 112, // PE32+
                0x10B => 96,  // PE32
                _ => -1
            };
            if (dataDirectoryOffset < 0) return null;

            // Data Directory[4] = IMAGE_DIRECTORY_ENTRY_SECURITY
            stream.Position = peOffset + 4 + 20 + dataDirectoryOffset + (4 * 8);
            uint certificateOffset = reader.ReadUInt32();
            uint certificateSize = reader.ReadUInt32();

            if (certificateSize == 0 || certificateSize > 16 * 1024 * 1024) return null;
            if (certificateOffset + (long)certificateSize > stream.Length) return null;

            stream.Position = certificateOffset;
            var buffer = new byte[certificateSize];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) return null;
                read += n;
            }

            return buffer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析签名者与 RFC3161 时间戳；解析失败只影响展示，不影响构建戳。</summary>
    internal static (string? Signer, DateTimeOffset? SignedAt) TryParseSignature(byte[] certificateTable)
    {
        try
        {
            if (certificateTable.Length < 8) return (null, null);

            uint length = BitConverter.ToUInt32(certificateTable, 0);
            if (length <= 8 || length > certificateTable.Length) return (null, null);

            var pkcs7 = certificateTable.AsSpan(8, (int)length - 8).ToArray();

            var cms = new SignedCms();
            cms.Decode(pkcs7);
            try { cms.CheckSignature(verifySignatureOnly: true); } catch { /* 证书链问题不影响读取签名者 */ }

            var signerInfo = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
            string? signer = signerInfo?.Certificate?.Subject;
            DateTimeOffset? signedAt = null;

            if (signerInfo is not null)
            {
                foreach (var attribute in signerInfo.UnsignedAttributes)
                {
                    if (attribute.Oid.Value != "1.3.6.1.4.1.311.3.3.1" || attribute.Values.Count == 0) continue;

                    byte[] raw = attribute.Values[0].RawData;
                    if (Rfc3161TimestampToken.TryDecode(raw, out var token, out _))
                    {
                        signedAt = token.TokenInfo.Timestamp;
                    }
                }
            }

            return (signer, signedAt);
        }
        catch
        {
            return (null, null);
        }
    }
}
