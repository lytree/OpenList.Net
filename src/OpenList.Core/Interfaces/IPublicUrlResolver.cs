namespace OpenList.Core.Interfaces;

/// <summary>
/// 解析当前部署对外的站点基地址（形如 <c>https://pan.example.com</c>），
/// 用于生成交给浏览器之外程序的绝对 URL。
/// <para>
/// 对应 Go 的 <c>server/common/base.go</c> <c>GetApiUrlFromRequest</c>：
/// 优先用配置的 <c>site_url</c>（以 <c>http</c> 开头时即完整地址）；否则按请求的
/// 转发头与 Host 现场推导。外部程序（PotPlayer / IINA / 下载器）不会像浏览器那样
/// 用页面 origin 补全相对地址，所以这类 URL 必须是绝对的。
/// </para>
/// </summary>
public interface IPublicUrlResolver
{
    /// <summary>
    /// 返回不带末尾斜杠的站点基地址；无法确定时返回空字符串（调用方此时应回退为相对地址）。
    /// </summary>
    string Resolve();
}
