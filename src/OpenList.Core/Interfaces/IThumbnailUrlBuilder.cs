namespace OpenList.Core.Interfaces;

/// <summary>
/// 缩略图 URL 构造器：给 <see cref="Models.Obj"/> 填 <c>thumb</c> 字段。
/// <para>
/// 定义在 Core 而不是实现所在的 Infrastructure，是为了让 <c>FileSystemService</c>
/// （在 Infrastructure 内）只依赖这个十几行的接口，而不必知道缩略图是怎么生成的。
/// 端点（<c>/p?type=thumb</c>）与生成的实现在 Infrastructure/Thumbnails 下。
/// </para>
/// <para>
/// <b>必须是纯内存判定</b>：它在 <c>fs/list</c> 的每个条目上调用一次，任何 IO（探测文件、
/// 查远端、解码）都会让大目录列表变慢。所以这里只看扩展名、大小与设置快照。
/// </para>
/// </summary>
public interface IThumbnailUrlBuilder
{
    /// <summary>
    /// 为文件条目构造缩略图 URL；不适用（目录 / 类型不支持 / 超限 / 总开关关闭）时返回 <c>null</c>。
    /// </summary>
    /// <param name="path">条目的完整挂载路径（用于拼接 <c>/p/{path}</c> 与签名）。</param>
    /// <param name="name">条目名（用于按扩展名判定类型）。</param>
    /// <param name="size">文件大小（字节，目录为 0）。</param>
    /// <param name="isDir">是否为目录。</param>
    /// <param name="signCtx">该路径的签名策略；需要签名时 URL 会带上 <c>&amp;sign=</c>。</param>
    /// <param name="driverAllowsThumb">
    /// 所属驱动是否允许出图（驱动实现了 <c>IThumbnailSwitch</c> 且自身开关为 false 时传 false）。
    /// 由调用方判定并传入，URL 构造器不关心驱动类型。
    /// </param>
    /// <returns>可直接给 <c>&lt;img src&gt;</c> 的绝对/相对 URL，或 <c>null</c>。</returns>
    string? Build(string path, string name, long size, bool isDir, SignContext signCtx, bool driverAllowsThumb = true);
}
