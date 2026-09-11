using Ozakboy.Core.Abstractions;

namespace Ozakboy.Http.Signing;

/// <summary>
/// 請求簽章演算法。不同服務的簽章規則不同,因此做成可抽換。
/// A request signing algorithm. Services sign differently, so the algorithm is substitutable.
/// </summary>
/// <remarks>
/// 實作必須是無狀態且執行緒安全的:同一個實例會被整條 HTTP 管線共用,並可能同時處理多個請求。
/// Implementations must be stateless and thread-safe: a single instance is shared by the whole HTTP pipeline
/// and may serve several requests at once.
/// </remarks>
public interface ISignatureAlgorithm
{
    /// <summary>
    /// 演算法名稱,僅用於日誌與診斷。
    /// The algorithm name, used only for logging and diagnostics.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 對待簽字串計算簽章。
    /// Signs the canonical payload.
    /// </summary>
    /// <param name="payload">
    /// 待簽字串。必須與實際送出的字串逐字相同 —— 先編碼再簽,不可簽完再編碼。
    /// The canonical string to sign. It must match the string actually sent byte for byte: encode first, then
    /// sign, never the other way round.
    /// </param>
    /// <param name="secret">
    /// 簽章金鑰。
    /// The signing secret.
    /// </param>
    /// <returns>
    /// 成功時為簽章字串;金鑰缺漏或計算失敗時為帶 <see cref="ErrorCategory.Validation"/> 或
    /// <see cref="ErrorCategory.Internal"/> 的失敗結果。
    /// The signature on success, or a failure carrying <see cref="ErrorCategory.Validation"/> or
    /// <see cref="ErrorCategory.Internal"/> when the secret is missing or the computation fails.
    /// </returns>
    Result<string> Sign(string payload, string secret);
}
