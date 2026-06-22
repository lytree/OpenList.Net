namespace OpenList.Core.Models;

/// <summary>
/// Standard API response envelope. All endpoints return <c>{ code, message, data }</c>.
/// </summary>
public class Resp
{
    public int Code { get; set; }
    public string Message { get; set; } = "success";

    public static Resp Ok() => new() { Code = 200, Message = "success" };
    public static Resp Fail(string message, int code = 500) => new() { Code = code, Message = message };
}

public sealed class Resp<T> : Resp
{
    public T? Data { get; set; }

    public static new Resp<T> Ok(T? data = default) => new() { Code = 200, Message = "success", Data = data };
    public static new Resp<T> Fail(string message, int code = 500) => new() { Code = code, Message = message, Data = default };
}

public static class RespCodes
{
    public const int Ok = 200;
    public const int BadRequest = 400;
    public const int Unauthorized = 401;
    public const int Forbidden = 403;
    public const int NotFound = 404;
    public const int Conflict = 409;
    public const int InternalError = 500;
    public const int NotImplemented = 501;
}
