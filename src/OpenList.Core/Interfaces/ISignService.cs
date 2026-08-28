namespace OpenList.Core.Interfaces;

/// <summary>
/// 签名服务：为 /d/、/p/、/ad/、/ap/、/ae/ 路由生成与校验签名 URL。
/// 与 OpenList Go 的 <c>server/common/sign.go</c> 对应。
/// </summary>
public interface ISignService
{
    /// <summary>为给定路径生成签名；可指定过期时间（秒或绝对时间）。</summary>
    string Sign(string path, long? expiration = null);
    /// <summary>校验路径与签名的有效性。</summary>
    bool Verify(string path, string sign);
}
