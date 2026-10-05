using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OpenList.Core.Configuration;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Cli;

/// <summary>
/// 运维 CLI，镜像 Go 版 <c>openlist</c> 的 <c>cmd/</c> 目录：<c>admin</c> / <c>storage</c> /
/// <c>cancel2fa</c>。参数解析交给 System.CommandLine，带子命令时执行运维操作，
/// 裸跑时启动 Web Server（与 Go 的行为一致）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么改密必须能脱离 Web 层。</b>内置管理员被刻意设为不可删除 / 不可改名 / 不可降权，
/// 它的密码就是唯一的恢复入口。若改密只能走需要服务在线 + 有效 token 的 API，那么
/// 「端口被占」「存储驱动导致启动崩溃」「token 丢失」时就彻底失管。本 CLI 直连数据库。
/// </para>
/// <para>
/// <b>为什么不复用 StorageService。</b>它依赖 <c>MountService</c>，构造时就会加载并挂载
/// 全部存储驱动 —— 运维只想改个密码，却因为某个驱动（比如需要网络可达的 S3）挂载失败而
/// 命令直接失败。这里只操作数据库行，不碰文件系统。
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>进程退出码：成功。</summary>
    public const int ExitOk = 0;
    /// <summary>进程退出码：用法错误或操作失败。</summary>
    public const int ExitFail = 1;

    /// <summary>密码最小长度（与 Web 安装向导一致，对齐 Go 的 &gt;= 4）。</summary>
    private const int MinPasswordLength = 4;

    public static async Task<int> Main(string[] args)
    {
        var root = BuildRootCommand();

        // 裸跑（无子命令）时启动 Web Server —— System.CommandLine 对「无子命令且无 action」
        // 只会打印帮助，因此先自行判断，再交回解析器。
        if (args.Length == 0)
            return await StartServerAsync();

        return await root.Parse(args).InvokeAsync();
    }

    // ---------------- 命令树 ----------------

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("OpenList 运维命令行工具。裸跑则启动 Web 服务。")
        {
            Subcommands =
            {
                BuildAdminCommand(),
                BuildStorageCommand(),
                BuildCancel2FaCommand(),
            },
        };
        return root;
    }

    // ---------------- admin ----------------

    private static Command BuildAdminCommand()
    {
        var cmd = new Command("admin", "查看管理员信息 / 设置管理员密码");

        var info = new Command("info", "显示管理员账号名、是否内置与禁用状态");
        info.SetAction(async (_, ct) => await PrintAdminInfoAsync(ct));

        var setPwd = new Command("set", "设置管理员密码");
        var password = new Argument<string>("password") { Description = "新的明文密码" };
        setPwd.Arguments.Add(password);
        setPwd.SetAction(async (ctx, ct) =>
            await SetPasswordAsync(ctx.GetValue(password), ct));

        var random = new Command("random", "生成 12 位随机密码并应用（避开易混淆字符）");
        random.SetAction(async (_, ct) => await SetPasswordAsync(GeneratePassword(), ct));

        cmd.Subcommands.Add(info);
        cmd.Subcommands.Add(setPwd);
        cmd.Subcommands.Add(random);
        cmd.SetAction((_, _) => Task.FromResult(ExitFail)); // 缺子命令 → 打印用法
        return cmd;
    }

    /// <summary>
    /// 设置内置管理员密码。直接定位「内置且是管理员」的行并重写哈希 —— 走服务层需要把整个
    /// UserService 的依赖（配置 + TimeProvider）拉进来，而这里只需要 DbContext。
    /// </summary>
    private static async Task<int> SetPasswordAsync(string password, CancellationToken ct)
    {
        if (password.Length < MinPasswordLength)
        {
            Console.Error.WriteLine($"password must be at least {MinPasswordLength} characters");
            return ExitFail;
        }

        // 走 OpenDbAsync：它会先确保 schema 与内置 admin 存在，全新机器上第一条命令也能用。
        var (db, _) = await OpenDbAsync();
        await using var _ = db;

        // 判据必须带 Role == Admin：guest 同样带 IsBuiltIn 标记，只按标记查会改掉游客的密码。
        // 老库尚未回填时退回「最早的管理员」。
        var admin = await db.Users.FirstOrDefaultAsync(u => u.IsBuiltIn && u.Role == UserRole.Admin, ct)
                    ?? await db.Users.Where(u => u.Role == UserRole.Admin)
                                     .OrderBy(u => u.Id)
                                     .FirstOrDefaultAsync(ct);
        if (admin is null)
        {
            Console.Error.WriteLine(
                "No admin account exists yet. Finish setup in the web UI " +
                "(POST /api/public/init/setup), or set OpenList__AdminPassword before the " +
                "first start and run this command again.");
            return ExitFail;
        }

        admin.PasswordHash = UserService.HashPassword(password);
        // 改密即解锁：忘记密码时管理员很可能已被禁用。
        admin.Disabled = false;
        admin.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        // 明文只打控制台，绝不写日志文件（对齐 Go 版 initAdmin 的处理）。
        Console.WriteLine("Admin user updated:");
        Console.WriteLine($"  username: {admin.Username}");
        Console.WriteLine($"  password: {password}");
        return ExitOk;
    }

    private static async Task<int> PrintAdminInfoAsync(CancellationToken ct)
    {
        var (db, _) = await OpenDbAsync();
        await using var _ = db;

        var admin = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.Admin)
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(ct);
        if (admin is null)
        {
            Console.Error.WriteLine(
                "No admin account exists yet. Finish setup in the web UI, " +
                "or set OpenList__AdminPassword before the first start.");
            return ExitFail;
        }

        Console.WriteLine($"Admin user's username: {admin.Username}");
        Console.WriteLine($"  built-in: {(admin.IsBuiltIn ? "yes" : "no")}");
        Console.WriteLine($"  disabled: {(admin.Disabled ? "yes" : "no")}");
        Console.WriteLine("The password is stored as a hash and cannot be recovered.");
        Console.WriteLine("Reset it with a random value:  openlist admin random");
        Console.WriteLine("Or set a specific one:         openlist admin set NEW_PASSWORD");
        return ExitOk;
    }

    // ---------------- storage ----------------

    private static Command BuildStorageCommand()
    {
        var cmd = new Command("storage", "列出 / 禁用 / 删除存储");

        var list = new Command("list", "列出所有存储");
        list.SetAction(async (_, ct) => await ListStoragesAsync(ct));

        var disable = new Command("disable", "按挂载路径禁用一个存储");
        var mountPath = new Argument<string>("mount-path") { Description = "挂载路径，如 /local" };
        disable.Arguments.Add(mountPath);
        disable.SetAction(async (ctx, ct) => await DisableStorageAsync(ctx.GetValue(mountPath), ct));

        var delete = new Command("delete", "按 id 删除存储（默认交互确认）");
        var id = new Argument<long>("id") { Description = "存储 id" };
        var force = new Option<bool>("--force", "-f") { Description = "跳过确认直接删除" };
        delete.Arguments.Add(id);
        delete.Options.Add(force);
        delete.SetAction(async (ctx, ct) =>
            await DeleteStorageAsync(ctx.GetValue(id), ctx.GetValue(force), ct));

        cmd.Subcommands.Add(list);
        cmd.Subcommands.Add(disable);
        cmd.Subcommands.Add(delete);
        cmd.SetAction((_, _) => Task.FromResult(ExitFail));
        return cmd;
    }

    private static async Task<int> ListStoragesAsync(CancellationToken ct)
    {
        var (db, _) = await OpenDbAsync();
        await using var _ = db;

        var all = await db.Storages.AsNoTracking()
            .OrderBy(s => s.Order).ThenBy(s => s.Id)
            .ToListAsync(ct);
        if (all.Count == 0)
        {
            Console.WriteLine("No storages found.");
            return ExitOk;
        }

        Console.WriteLine($"{"ID",-6}{"MOUNT PATH",-30}{"DRIVER",-22}{"STATUS",-10}");
        foreach (var s in all)
            Console.WriteLine(
                $"{s.Id,-6}{Truncate(s.MountPath, 29),-30}{Truncate(s.Driver, 21),-22}{(s.Disabled ? "disabled" : "enabled"),-10}");
        Console.WriteLine($"Total: {all.Count}");
        return ExitOk;
    }

    private static async Task<int> DisableStorageAsync(string mountPath, CancellationToken ct)
    {
        var (db, _) = await OpenDbAsync();
        await using var _ = db;

        var storage = await db.Storages.FirstOrDefaultAsync(s => s.MountPath == mountPath, ct);
        if (storage is null)
        {
            Console.Error.WriteLine($"storage not found for mount path: {mountPath}");
            return ExitFail;
        }
        storage.Disabled = true;
        storage.Modified = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"Storage with mount path [{mountPath}] has been disabled");
        return ExitOk;
    }

    private static async Task<int> DeleteStorageAsync(long id, bool force, CancellationToken ct)
    {
        // 删除会连带丢掉该存储的凭据配置，不可撤销 —— 除非显式 --force，否则交互确认。
        if (!force)
        {
            Console.Write($"Are you sure you want to delete storage with id [{id}]? [y/N]: ");
            var confirm = Console.ReadLine();
            if (!string.Equals(confirm?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Delete operation cancelled.");
                return ExitOk;
            }
        }

        var (db, _) = await OpenDbAsync();
        await using var _ = db;

        var target = await db.Storages.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (target is null)
        {
            Console.Error.WriteLine($"storage not found: {id}");
            return ExitFail;
        }
        db.Storages.Remove(target);
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"Storage with id [{id}] has been deleted");
        return ExitOk;
    }

    // ---------------- cancel2fa ----------------

    private static Command BuildCancel2FaCommand()
    {
        var cmd = new Command("cancel2fa", "关闭某用户的两步验证（验证器丢失时的救援）");
        var username = new Argument<string>("username") { Description = "用户名" };
        cmd.Arguments.Add(username);
        cmd.SetAction(async (ctx, ct) => await Cancel2FaAsync(ctx.GetValue(username), ct));
        return cmd;
    }

    private static async Task<int> Cancel2FaAsync(string username, CancellationToken ct)
    {
        var (db, _) = await OpenDbAsync();
        await using var _ = db;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            Console.Error.WriteLine($"user not found: {username}");
            return ExitFail;
        }
        if (!user.TwoFactorEnabled)
        {
            Console.WriteLine($"User [{user.Username}] does not have two-factor authentication enabled");
            return ExitOk;
        }

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"Two-factor authentication has been disabled for user [{user.Username}]");
        return ExitOk;
    }

    // ---------------- DbContext ----------------

    /// <summary>
    /// 打开数据库连接，顺带确保 schema 与内置 admin 存在 —— CLI 也走完整的建库流程，
    /// 这样全新机器上的第一条命令就能把 admin 建出来。
    /// </summary>
    private static async Task<(OpenListDbContext Db, AppConfiguration Config)> OpenDbAsync()
    {
        var config = LoadConfiguration();
        var options = BuildDbContextOptions(config);
        var db = new OpenListDbContext(options, config.Database);
        // devMode=false：CLI 是运维入口，不该因为本地是 Development 就替用户定下密码。
        // 首次部署请走 Web 安装向导，或显式设置 OpenList__AdminPassword 后再运行 CLI。
        await DbInitializer.InitializeAsync(db, config, devMode: false);
        return (db, config);
    }

    // ---------------- 启动 Server ----------------

    /// <summary>
    /// 裸跑时启动 Web Server，对齐 Go 的 <c>openlist</c> 无子命令即起服务。
    /// </summary>
    /// <remarks>
    /// 采用<em>拉起子进程</em>而非在当前进程里调用 Server 的入口点：两个 ASP.NET Host
    /// 共处一个进程会共享静态状态（实测直接触发 <c>DefaultHttpClientFactory</c> 的
    /// <see cref="NullReferenceException"/>），且原生库的探测基准是进程目录而非程序集目录。
    /// 子进程则与桌面端 <c>OpenListCoreService</c> 托管核心的做法一致，各 concerns 互不干扰。
    /// </remarks>
    private static async Task<int> StartServerAsync()
    {
        var (serverDir, exeName) = LocateServer();
        if (serverDir is null)
        {
            Console.Error.WriteLine(
                "Could not locate the OpenList server to start. " +
                "In a release bundle use run.sh / run.bat, or run app/OpenList.Server directly.");
            return ExitFail;
        }

        var exe = Path.Combine(serverDir, exeName);
        Console.WriteLine($"Starting OpenList server: {exe}");
        Console.WriteLine("Press Ctrl+C to stop.");

        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            // 让服务端沿用它自己目录里的 appsettings.json 与相对路径（data/ 就在那儿）。
            WorkingDirectory = serverDir,
            UseShellExecute = false,
        };
        // 转发 ASPNETCORE_URLS 等，让 CLI 侧的监听配置生效。
        foreach (var key in new[] { "ASPNETCORE_URLS", "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(val)) psi.Environment[key] = val;
        }

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                Console.Error.WriteLine("Failed to start the server process.");
                return ExitFail;
            }
            await proc.WaitForExitAsync();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start the server: {ex.Message}");
            return ExitFail;
        }
    }

    /// <summary>
    /// 定位 Server 可执行文件，返回 (所在目录, 文件名)。两种布局都要探：
    /// <list type="bullet">
    /// <item>与 CLI 同级 —— 开发态，或两者一起发布到同一目录；</item>
    /// <item><c>../app/</c> —— 发布包布局（<c>cli/</c> 与 <c>app/</c> 是兄弟目录，见
    /// <c>build/build.cs</c> 的 Bundle 任务）。</item>
    /// </list>
    /// </summary>
    private static (string? Dir, string ExeName) LocateServer()
    {
        var isWindows = OperatingSystem.IsWindows();
        var exeName = isWindows ? "OpenList.Server.exe" : "OpenList.Server";
        var dirs = new[]
        {
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "app")),
        };
        foreach (var dir in dirs)
        {
            if (File.Exists(Path.Combine(dir, exeName))) return (dir, exeName);
            // framework-dependent 构建下只有 dll，由 apphost 或 dotnet 启动。
            if (File.Exists(Path.Combine(dir, "OpenList.Server.dll"))) return (dir, exeName);
        }
        return (null, exeName);
    }

    // ---------------- 配置 ----------------

    /// <summary>
    /// 加载应用配置。与 Server 一致：<c>appsettings.json</c> ＋ <c>OpenList__</c> 前缀环境变量，
    /// 后者覆盖前者。
    /// </summary>
    private static AppConfiguration LoadConfiguration()
    {
        // 用绝对路径而非 SetBasePath：后者需要额外的 FileExtensions 包，
        // 而这里只需要读一个固定文件。
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var builder = new ConfigurationBuilder()
            .AddJsonFile(settingsPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("OpenList__");

        var config = builder.Build().Get<AppConfiguration>() ?? new AppConfiguration();

        // SQLite 的 DBFile 相对路径按当前工作目录解析（与 Go 的 --data 语义一致）。
        if (!Path.IsPathRooted(config.Database.DBFile))
        {
            var dir = Path.GetDirectoryName(config.Database.DBFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        return config;
    }

    /// <summary>
    /// 按配置构建 DbContext 选项。SQLite 与 PostgreSQL 各绑定自己的迁移程序集，
    /// 与 <c>OpenList.Server/Program.cs</c> 保持一致。
    /// </summary>
    private static DbContextOptions<OpenListDbContext> BuildDbContextOptions(AppConfiguration config)
    {
        var builder = new DbContextOptionsBuilder<OpenListDbContext>();
        if (IsPostgres(config.Database.Type))
        {
            var cs = new Npgsql.NpgsqlConnectionStringBuilder
            {
                Host = config.Database.Host,
                Port = config.Database.Port,
                Username = config.Database.User,
                Password = config.Database.Password,
                Database = config.Database.Name,
            }.ToString();
            builder.UseNpgsql(cs, npgsql => npgsql.MigrationsAssembly("OpenList.Migrations.Postgres"));
        }
        else
        {
            builder.UseSqlite($"Data Source={config.Database.DBFile}",
                sqlite => sqlite.MigrationsAssembly("OpenList.Migrations.Sqlite"));
        }
        return builder.Options;
    }

    private static bool IsPostgres(string type)
        => type.Equals("postgres", StringComparison.OrdinalIgnoreCase) ||
           type.Equals("postgresql", StringComparison.OrdinalIgnoreCase);

    // ---------------- helpers ----------------

    /// <summary>
    /// 生成 12 位随机密码。字符集刻意避开易混淆字符（0/O、1/l/I），因为这串密码通常要靠
    /// 日志或剪贴板转述（Go 版是 8 位）。
    /// </summary>
    internal static string GeneratePassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return RandomNumberGenerator.GetString(alphabet, 12);
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
