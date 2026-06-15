using Mapster;
using OpenList.Core.Models;
using OpenList.Infrastructure.Services;
using OpenList.Server.Models;

namespace OpenList.Server;

/// <summary>
/// Centralized Mapster type adapter configuration for all object mappings.
/// </summary>
public static class MappingConfig
{
    public static void Configure()
    {
        var config = TypeAdapterConfig.GlobalSettings;

        // IObj → ObjResp: IObj uses methods (GetName(), GetSize(), etc.), not properties
        config.NewConfig<IObj, ObjResp>()
            .MapWith(obj => new ObjResp
            {
                Name = obj.GetName(),
                Size = obj.GetSize(),
                IsDir = obj.IsDir(),
                Modified = obj.GetModTime(),
                Created = obj.GetCreateTime(),
                HashInfoStr = obj.GetHash(),
                HashInfo = ParseHashInfo(obj.GetHash()),
                Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
                Thumb = ObjTypeHelper.GetThumb(obj),
            });

        // IObj → GetResp: GetResp extends ObjResp, reuse same base mapping
        config.NewConfig<IObj, GetResp>()
            .MapWith(obj => new GetResp
            {
                Name = obj.GetName(),
                Size = obj.GetSize(),
                IsDir = obj.IsDir(),
                Modified = obj.GetModTime(),
                Created = obj.GetCreateTime(),
                HashInfoStr = obj.GetHash(),
                HashInfo = ParseHashInfo(obj.GetHash()),
                Type = ObjTypeHelper.GetObjType(obj.GetName(), obj.IsDir()),
                Thumb = ObjTypeHelper.GetThumb(obj),
            });

        // ArchiveContentEntry → ArchiveContentResp: recursive tree mapping
        config.NewConfig<ArchiveContentEntry, ArchiveContentResp>()
            .Map(dest => dest.Children, src => src.Children.Adapt<List<ArchiveContentResp>>());

        // User → UserResp: Role enum → int, OtpSecret → Otp bool, Password always empty
        config.NewConfig<User, UserResp>()
            .Map(dest => dest.Role, src => (int)src.Role)
            .Map(dest => dest.Otp, src => !string.IsNullOrEmpty(src.OtpSecret))
            .Map(dest => dest.Password, src => "");

        // CreateUserReq → User: int Role → UserRole enum, Password → PwdHash
        config.NewConfig<CreateUserReq, User>()
            .Map(dest => dest.Role, src => (UserRole)src.Role)
            .Map(dest => dest.PwdHash, src => UserService.StaticHash(src.Password))
            .Map(dest => dest.BasePath, src => src.BasePath ?? "/");

        // Link → LinkResp: direct property mapping
        config.NewConfig<Link, LinkResp>()
            .Map(dest => dest.URL, src => src.URL ?? "")
            .Map(dest => dest.Header, src => src.Header)
            .Map(dest => dest.Concurrency, src => src.Concurrency)
            .Map(dest => dest.PartSize, src => src.PartSize)
            .Map(dest => dest.ContentLength, src => src.ContentLength);
    }

    private static Dictionary<string, string>? ParseHashInfo(string? hashStr)
    {
        if (string.IsNullOrEmpty(hashStr) || hashStr == "{}") return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(hashStr); }
        catch { return null; }
    }
}
