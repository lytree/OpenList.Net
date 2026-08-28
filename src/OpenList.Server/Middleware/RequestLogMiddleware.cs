using System.Diagnostics;
using System.Net;
using OpenList.Core.Configuration;

namespace OpenList.Server.Middleware;

/// <summary>
/// 请求访问日志中间件，镜像 Go 的 <c>middlewares.FilteredLogger</c> + <c>skiperDecider</c>。
/// 每条请求记录 方法/路径/状态码/耗时/客户端IP；当 <c>Log.Filter.Enable</c> 时，
/// 命中任一过滤规则（规则内条件为"与"，规则间为"或"）的请求跳过日志。
/// 不依赖 Serilog.AspNetCore 的 UseSerilogRequestLogging，以便按 Go 语义精确控制过滤。
/// </summary>
public sealed class RequestLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLogMiddleware> _logger;
    private readonly LogFilterConfig _filter;
    private readonly LogFilterItem[] _filters;

    public RequestLogMiddleware(RequestDelegate next, ILogger<RequestLogMiddleware> logger, LogFilterConfig filter)
    {
        _next = next;
        _logger = logger;
        _filter = filter;
        _filters = filter?.Filters?.ToArray() ?? [];
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await _next(ctx);
        }
        finally
        {
            if (!ShouldSkip(ctx.Request))
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _logger.LogInformation(
                    "HTTP {Method} {Path} responded {StatusCode} in {Elapsed:0.0} ms, client {RemoteIp}",
                    ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, elapsedMs,
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "-");
            }
        }
    }

    /// <summary>
    /// 判断请求是否应被跳过（不写日志）。镜像 Go <c>skiperDecider</c>：
    /// 任一过滤规则的全部条件命中即返回 true；全部规则未命中返回 false。
    /// </summary>
    public bool ShouldSkip(HttpRequest request)
    {
        if (_filter is not { Enable: true } || _filters.Length == 0)
            return false;

        foreach (var f in _filters)
        {
            var matched = true;

            if (!string.IsNullOrEmpty(f.CIDR) &&
                !CidrContains(f.CIDR, request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? ""))
                matched = false;

            if (matched && !string.IsNullOrEmpty(f.Path))
            {
                var path = request.Path.Value ?? "";
                if (f.Path[0] == '/')
                    matched = path.StartsWith(f.Path, StringComparison.Ordinal);   // 前缀匹配
                else
                    matched = path.Contains("/" + f.Path, StringComparison.Ordinal); // 包含匹配
            }

            if (matched && !string.IsNullOrEmpty(f.Method) &&
                !string.Equals(f.Method, request.Method, StringComparison.OrdinalIgnoreCase))
                matched = false;

            if (matched) return true;
        }

        return false;
    }

    /// <summary>判断 IPv4/IPv6 地址是否属于某个 CIDR 网段（含无前缀的精确匹配）。</summary>
    public static bool CidrContains(string cidr, string ip)
    {
        if (string.IsNullOrEmpty(cidr) || string.IsNullOrEmpty(ip))
            return false;

        var slash = cidr.IndexOf('/');
        var networkText = slash < 0 ? cidr : cidr[..slash];
        if (!IPAddress.TryParse(networkText, out var network) ||
            !IPAddress.TryParse(ip, out var address))
            return false;

        var netBytes = network.GetAddressBytes();
        var addrBytes = address.GetAddressBytes();
        if (netBytes.Length != addrBytes.Length)
            return false;

        int prefixBits;
        if (slash < 0)
        {
            prefixBits = netBytes.Length * 8;
        }
        else if (!int.TryParse(cidr[(slash + 1)..], out prefixBits) ||
                 prefixBits < 0 || prefixBits > netBytes.Length * 8)
        {
            return false;
        }

        var fullBytes = prefixBits / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (netBytes[i] != addrBytes[i]) return false;
        }
        var remBits = prefixBits % 8;
        if (remBits > 0)
        {
            var mask = (byte)(0xFF << (8 - remBits));
            if ((netBytes[fullBytes] & mask) != (addrBytes[fullBytes] & mask)) return false;
        }
        return true;
    }
}
