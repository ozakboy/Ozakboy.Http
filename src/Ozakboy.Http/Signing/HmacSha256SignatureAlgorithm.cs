using System.Security.Cryptography;
using System.Text;
using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Signing;

/// <summary>
/// HMAC-SHA256 簽章,輸出小寫十六進位字串。這是多數交易所 REST API 採用的形式。
/// HMAC-SHA256 signing with a lowercase hexadecimal result, the form most exchange REST APIs use.
/// </summary>
/// <remarks>
/// 金鑰與待簽字串都以 UTF-8 編碼。輸出使用 <see cref="Convert.ToHexStringLower(byte[])"/> ——
/// 大小寫在多數服務是敏感的,自行以 <c>ToString("x2")</c> 拼字串則既慢又容易寫錯。
/// Both the key and the payload are UTF-8 encoded. The result uses
/// <see cref="Convert.ToHexStringLower(byte[])"/>: case matters to most services, and hand-rolling the hex
/// with <c>ToString("x2")</c> is both slower and easier to get wrong.
/// </remarks>
public sealed class HmacSha256SignatureAlgorithm : ISignatureAlgorithm
{
    /// <summary>
    /// 共用實例。此型別無狀態,不需要為每個用戶端各建一個。
    /// The shared instance. The type is stateless, so no per-client instance is needed.
    /// </summary>
    public static HmacSha256SignatureAlgorithm Instance { get; } = new();

    /// <inheritdoc />
    public string Name => "HMAC-SHA256";

    /// <inheritdoc />
    public Result<string> Sign(string payload, string secret)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (string.IsNullOrEmpty(secret))
        {
            return Error.Validation(
                HttpErrorCodes.SigningSecretMissing,
                "簽章金鑰未設定,無法對請求簽章。The signing secret is not configured, so the request cannot be signed.");
        }

        var key = Encoding.UTF8.GetBytes(secret);
        try
        {
            var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
            return Result.Success(Convert.ToHexStringLower(hash));
        }
        finally
        {
            // 金鑰位元組用完即抹除,縮短它停留在受管記憶體(可能被寫進記憶體傾印)的時間。
            // Wipe the key bytes once done, shortening how long they sit in managed memory where a crash dump
            // could pick them up.
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
