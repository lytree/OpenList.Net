namespace OpenList.Server.Models;

public class Resp<T>
{
    public int Code { get; set; } = 200;
    public string Message { get; set; } = "success";
    public T? Data { get; set; }

    public Resp() { }
    public Resp(T? data) { Data = data; }

    public static Resp<T> Error(int code, string message) => new() { Code = code, Message = message };
}
