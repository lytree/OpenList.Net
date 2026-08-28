namespace OpenList.Core.Models;

/// <summary>
/// 标准 API 响应包装。所有端点统一返回 <c>{ code, message, data }</c> 结构。
/// </summary>
public class Resp
{
    /// <summary>业务状态码，200 表示成功。</summary>
    public int Code { get; set; }
    /// <summary>提示信息，默认 "success"。</summary>
    public string Message { get; set; } = "success";

    /// <summary>构造一个成功响应（code=200）。</summary>
    public static Resp Ok() => new() { Code = 200, Message = "success" };
    /// <summary>构造一个失败响应，可自定义状态码。</summary>
    /// <param name="message">错误提示信息。</param>
    /// <param name="code">错误状态码，默认 500。</param>
    public static Resp Fail(string message, int code = 500) => new() { Code = code, Message = message };
}

/// <summary>
/// 带数据载荷的响应包装，继承自 <see cref="Resp"/>。
/// </summary>
/// <typeparam name="T">数据载荷的类型。</typeparam>
public sealed class Resp<T> : Resp
{
    /// <summary>响应的数据内容。</summary>
    public T? Data { get; set; }

    /// <summary>构造带数据的成功响应（code=200）。</summary>
    /// <param name="data">要返回的数据，默认为类型默认值。</param>
    public static new Resp<T> Ok(T? data = default) => new() { Code = 200, Message = "success", Data = data };
    /// <summary>构造带数据的失败响应。</summary>
    /// <param name="message">错误提示信息。</param>
    /// <param name="code">错误状态码，默认 500。</param>
    public static new Resp<T> Fail(string message, int code = 500) => new() { Code = code, Message = message, Data = default };
}

/// <summary>
/// 分页响应载荷。与 Go <c>common.PageResp</c> 对应。
/// 序列化为 <c>{ "content": [...], "total": N }</c>。
/// </summary>
/// <typeparam name="T">列表元素类型。</typeparam>
public sealed class PageResp<T>
{
    /// <summary>当前页的数据列表。</summary>
    public List<T> Content { get; set; } = [];
    /// <summary>符合条件的数据总条数。</summary>
    public long Total { get; set; }
}

/// <summary>
/// 统一业务状态码常量定义。
/// </summary>
public static class RespCodes
{
    /// <summary>成功。</summary>
    public const int Ok = 200;
    /// <summary>请求参数错误。</summary>
    public const int BadRequest = 400;
    /// <summary>未授权。</summary>
    public const int Unauthorized = 401;
    /// <summary>无权访问。</summary>
    public const int Forbidden = 403;
    /// <summary>资源不存在。</summary>
    public const int NotFound = 404;
    /// <summary>冲突。</summary>
    public const int Conflict = 409;
    /// <summary>服务器内部错误。</summary>
    public const int InternalError = 500;
    /// <summary>尚未实现。</summary>
    public const int NotImplemented = 501;
}
