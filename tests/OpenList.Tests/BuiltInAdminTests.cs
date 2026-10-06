using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// 内置账号（<c>User.IsBuiltIn</c>）的生命周期测试。对应 Go 版
/// <c>internal/bootstrap/data/user.go</c> 的 <c>initUser()</c>：
/// 游客固定创建；管理员<em>仅在拿得到初始密码时</em>创建，否则交给 Web 安装向导
/// <c>POST /api/public/init/setup</c>。已存在的管理员永不被配置覆盖。
/// </summary>
public class BuiltInAdminTests
{
    // ---------------- 首次启动：无密码 ⇒ 不建管理员 ----------------

    [Test]
    public async Task InitializeAsync_Skips_Admin_When_No_Password_Configured()
    {
        await using var ctx = BuildContext();
        // 默认配置：AdminPassword 为空（生产环境首次启动的典型情形）。
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());

        // 不留 admin/admin 这种人人可猜的默认凭据，交由安装向导创建。
        await Assert.That(await ctx.Users.AnyAsync(u => u.Role == UserRole.Admin)).IsFalse();
        // 但游客照样建（判据独立）。
        await Assert.That(await ctx.Users.AnyAsync(u => u.Username == "guest")).IsTrue();
    }

    [Test]
    public async Task InitializeAsync_Skips_Admin_In_Production_Even_If_Password_Set_But_NoDev()
    {
        await using var ctx = BuildContext();
        // 非开发模式 + 无密码 → 不建。
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration(), devMode: false);
        await Assert.That(await ctx.Users.AnyAsync(u => u.Role == UserRole.Admin)).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_Creates_Admin_In_Dev_Mode_Without_Configured_Password()
    {
        await using var ctx = BuildContext();
        // devMode（对应 Go 的 --dev）：免去本地每次都走安装向导。
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration(), devMode: true);

        var admin = await AdminAsync(ctx);
        await Assert.That(admin.Username).IsEqualTo("admin");
        await Assert.That(UserService.VerifyPassword("admin", admin.PasswordHash)).IsTrue();
    }

    // ---------------- 首次启动：有密码 ⇒ 建管理员 ----------------

    [Test]
    public async Task InitializeAsync_Creates_BuiltIn_Admin_When_Password_Configured()
    {
        await using var ctx = BuildContext();
        await InitWithAdminAsync(ctx);

        var admin = await AdminAsync(ctx);
        await Assert.That(admin.Username).IsEqualTo("admin");
        await Assert.That(admin.Role).IsEqualTo(UserRole.Admin);
        await Assert.That(admin.IsBuiltIn).IsTrue();
        await Assert.That(admin.Disabled).IsFalse();
        // 权限位对齐 Go 的 0x71FF
        await Assert.That(admin.Permission).IsEqualTo(0x71FF);
        await Assert.That(admin.BasePath).IsEqualTo("/");
        await Assert.That(UserService.VerifyPassword("admin", admin.PasswordHash)).IsTrue();
    }

    [Test]
    public async Task InitializeAsync_Uses_Configured_Credentials_On_First_Run()
    {
        await using var ctx = BuildContext();
        var config = new AppConfiguration { AdminUsername = "root", AdminPassword = "s3cret" };
        await DbInitializer.InitializeAsync(ctx, config);

        var admin = await AdminAsync(ctx);
        await Assert.That(admin.Username).IsEqualTo("root");
        await Assert.That(UserService.VerifyPassword("s3cret", admin.PasswordHash)).IsTrue();
    }

    [Test]
    public async Task InitializeAsync_Creates_Disabled_Guest_Account()
    {
        await using var ctx = BuildContext();
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());

        // 对齐 Go 的 initGuest：用户名固定 guest、密码 guest、零权限、默认禁用。
        var guest = await ctx.Users.SingleAsync(u => u.Username == "guest");
        await Assert.That(guest.Role).IsEqualTo(UserRole.Guest);
        await Assert.That(guest.Disabled).IsTrue();
        await Assert.That(guest.Permission).IsEqualTo(0);
        await Assert.That(guest.BasePath).IsEqualTo("/");
        await Assert.That(guest.IsBuiltIn).IsTrue();
        await Assert.That(UserService.VerifyPassword("guest", guest.PasswordHash)).IsTrue();
    }

    // ---------------- 关键行为：已有管理员时绝不覆盖 ----------------

    [Test]
    public async Task InitializeAsync_Does_Not_Overwrite_Existing_Admin_Password()
    {
        await using var ctx = BuildContext();
        // 模拟"用户已在管理界面改过密码"：库里已有一个内置 admin。
        ctx.Users.Add(new User
        {
            Username = "admin",
            PasswordHash = UserService.HashPassword("changed-by-user"),
            Role = UserRole.Admin,
            Permission = 0x71FF,
            BasePath = "/",
            IsBuiltIn = true,
        });
        await ctx.SaveChangesAsync();

        // 第二次初始化：配置里给了不同的初始密码，但绝不能把密码打回去。
        await InitWithAdminAsync(ctx, "should-not-apply");

        var admin = await AdminAsync(ctx);
        await Assert.That(UserService.VerifyPassword("changed-by-user", admin.PasswordHash)).IsTrue();
        await Assert.That(UserService.VerifyPassword("should-not-apply", admin.PasswordHash)).IsFalse();
    }

    [Test]
    public async Task InitializeAsync_Does_Not_Rename_Existing_Admin()
    {
        await using var ctx = BuildContext();
        ctx.Users.Add(new User
        {
            Username = "renamed-admin",
            PasswordHash = UserService.HashPassword("pw"),
            Role = UserRole.Admin,
            IsBuiltIn = true,
        });
        await ctx.SaveChangesAsync();

        // 配置里的 AdminUsername="admin" 不应把已改名的账号改回去。
        await InitWithAdminAsync(ctx);

        var admin = await AdminAsync(ctx);
        await Assert.That(admin.Username).IsEqualTo("renamed-admin");
    }

    [Test]
    public async Task InitializeAsync_Creates_Admin_Even_If_Only_NonAdmin_Users_Exist()
    {
        await using var ctx = BuildContext();
        // 判据是「没有管理员」而非「用户表为空」：游客会被自动创建，
        // 若用表判据，管理员在第二次启动后就再也建不出来。
        ctx.Users.Add(new User
        {
            Username = "alice",
            PasswordHash = UserService.HashPassword("pw"),
            Role = UserRole.General,
        });
        await ctx.SaveChangesAsync();

        await InitWithAdminAsync(ctx);

        await Assert.That(await ctx.Users.AnyAsync(u => u.Role == UserRole.Admin)).IsTrue();
        await Assert.That(await ctx.Users.CountAsync()).IsEqualTo(3); // alice + guest + admin
    }

    [Test]
    public async Task Backfill_Marks_Earliest_Admin_As_BuiltIn()
    {
        await using var ctx = BuildContext();
        // 模拟旧库：两个 admin，IsBuiltIn 尚未引入（默认 false）。
        ctx.Users.Add(new User { Username = "first-admin", PasswordHash = UserService.HashPassword("pw"), Role = UserRole.Admin });
        ctx.Users.Add(new User { Username = "second-admin", PasswordHash = UserService.HashPassword("pw"), Role = UserRole.Admin });
        await ctx.SaveChangesAsync();
        // 让 Backfill 生效：EnsureCreated 建出的库已含该列，但值全为 false。
        await DbInitializer.BackfillBuiltInAdminAsync(ctx, default);

        var first = await ctx.Users.SingleAsync(u => u.Username == "first-admin");
        var second = await ctx.Users.SingleAsync(u => u.Username == "second-admin");
        await Assert.That(first.IsBuiltIn).IsTrue();
        // 只标记最早的那个，不把后来手工建的 admin 也算成内置。
        await Assert.That(second.IsBuiltIn).IsFalse();
    }

    // ---------------- 安装向导（生产环境的建号主路径） ----------------

    [Test]
    public async Task CreateInitialAdminAsync_Works_After_Guest_Seeded()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());

        // 典型时序：首次启动未配密码 → 只建了 guest，用户随后访问网页走向导。
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());
        await Assert.That(await ctx.Users.AnyAsync(u => u.Role == UserRole.Admin)).IsFalse();

        var r = await svc.CreateInitialAdminAsync("owner", "chosen-pw");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.IsBuiltIn).IsTrue();
        await Assert.That(r.Data.Permission).IsEqualTo(0x71FF);
        await Assert.That(UserService.VerifyPassword("chosen-pw", (await AdminAsync(ctx)).PasswordHash)).IsTrue();
    }

    [Test]
    public async Task CreateInitialAdminAsync_Rejects_When_Admin_Exists()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);

        // 向导不可被重复调用来追加管理员。
        var again = await svc.CreateInitialAdminAsync("second", "pw");
        await Assert.That(again.Code).IsEqualTo(400);
        await Assert.That(await ctx.Users.CountAsync(u => u.Role == UserRole.Admin)).IsEqualTo(1);
    }

    [Test]
    public async Task CreateAsync_Does_Not_Mark_User_As_BuiltIn()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);

        // "内置"是系统级保证，不该由通用建号接口指定 —— 否则能凭空多出受保护账号。
        var r = await svc.CreateAsync(new CreateUserReq
        {
            Username = "other-admin",
            Password = "pw",
            Role = UserRole.Admin,
        });
        await Assert.That(r.Data!.IsBuiltIn).IsFalse();
    }

    // ---------------- 删除保护 ----------------

    [Test]
    public async Task DeleteAsync_Blocks_BuiltIn_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);

        var admin = await AdminAsync(ctx);
        var r = await svc.DeleteAsync(admin.Id);
        await Assert.That(r.Code).IsEqualTo(403);
        await Assert.That(await ctx.Users.AnyAsync(u => u.Id == admin.Id)).IsTrue();
    }

    [Test]
    public async Task DeleteAsync_Allows_Non_BuiltIn_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        var created = await svc.CreateAsync(new CreateUserReq
        {
            Username = "second-admin",
            Password = "pw",
            Role = UserRole.Admin,
        });

        // 非内置的管理员可以删除（只保护唯一入口那一个）。
        var r = await svc.DeleteAsync(created.Data!.Id);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(await ctx.Users.AnyAsync(u => u.Id == created.Data.Id)).IsFalse();
    }

    // ---------------- 改名 / 降权保护 ----------------

    [Test]
    public async Task UpdateAsync_Blocks_Rename_And_Demotion_Of_BuiltIn_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);
        var admin = await AdminAsync(ctx);

        var rename = await svc.UpdateAsync(admin.Id, new UpdateUserReq { Username = "hacker" });
        await Assert.That(rename.Code).IsEqualTo(403);

        var demote = await svc.UpdateAsync(admin.Id, new UpdateUserReq { Role = UserRole.General });
        await Assert.That(demote.Code).IsEqualTo(403);

        var disable = await svc.UpdateAsync(admin.Id, new UpdateUserReq { Disabled = true });
        await Assert.That(disable.Code).IsEqualTo(403);

        await Assert.That((await AdminAsync(ctx)).Username).IsEqualTo("admin");
    }

    [Test]
    public async Task UpdateAsync_Allows_Password_Change_Of_BuiltIn_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);
        var admin = await AdminAsync(ctx);

        // 保留内置 admin 的唯一理由就是还能改密 —— 该路径必须畅通。
        var r = await svc.UpdateAsync(admin.Id, new UpdateUserReq { Password = "newpw" });
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(UserService.VerifyPassword("newpw", (await AdminAsync(ctx)).PasswordHash)).IsTrue();
    }

    [Test]
    public async Task ListAsync_Reports_IsBuiltIn_So_Frontend_Can_Disable_Delete()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);

        // 漏映射IsBuiltIn 会让前端放出"删除内置管理员"这个注定被拒的操作。
        var r = await svc.ListAsync();
        var admin = r.Data!.Single(u => u.Role == UserRole.Admin);
        var guest = r.Data!.Single(u => u.Username == "guest");
        await Assert.That(admin.IsBuiltIn).IsTrue();
        await Assert.That(guest.IsBuiltIn).IsTrue();
    }

    // ---------------- 密码重置通道 ----------------

    [Test]
    public async Task ResetAdminPasswordAsync_Updates_BuiltIn_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);

        var r = await svc.ResetAdminPasswordAsync("brand-new");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(UserService.VerifyPassword("brand-new", (await AdminAsync(ctx)).PasswordHash)).IsTrue();
    }

    [Test]
    public async Task ResetAdminPasswordAsync_Rejects_Empty_Password()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);
        var before = (await AdminAsync(ctx)).PasswordHash;

        // 空密码会把账号锁死（内置 admin 无其他恢复途径），必须拒绝。
        await Assert.That((await svc.ResetAdminPasswordAsync("")).Code).IsEqualTo(400);
        await Assert.That((await svc.ResetAdminPasswordAsync("   ")).Code).IsEqualTo(400);
        await Assert.That((await AdminAsync(ctx)).PasswordHash).IsEqualTo(before);
    }

    [Test]
    public async Task ResetAdminPasswordAsync_Undisables_Locked_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        await InitWithAdminAsync(ctx);

        var admin = await AdminAsync(ctx);
        admin.Disabled = true;
        await ctx.SaveChangesAsync();

        // 忘记密码时管理员很可能已被禁用，改密应顺带解锁。
        await svc.ResetAdminPasswordAsync("unlock-me");
        await Assert.That((await AdminAsync(ctx)).Disabled).IsFalse();
    }

    [Test]
    public async Task ResetAdminPasswordAsync_Returns_404_When_No_Admin()
    {
        await using var ctx = BuildContext();
        var svc = new UserService(ctx, new AppConfiguration());
        // 首次启动未配密码 → 只有 guest，没有 admin，改密无从谈起。
        await DbInitializer.InitializeAsync(ctx, new AppConfiguration());
        await Assert.That((await svc.ResetAdminPasswordAsync("x")).Code).IsEqualTo(404);
    }

    // ---------------- 保护判定基于标记而非用户名 ----------------

    [Test]
    public async Task IsSuperAdmin_Uses_Flag_Not_Username()
    {
        var renamed = new User { Username = "not-admin-anymore", Role = UserRole.Admin, IsBuiltIn = true };
        var plain = new User { Username = "admin", Role = UserRole.Admin, IsBuiltIn = false };

        // 用户名与配置默认值相同但没有标记 → 不受保护（否则无法创建同名管理员）。
        await Assert.That(UserService.IsSuperAdmin(plain)).IsFalse();
        // 标记为内置但改过名 → 仍受保护。
        await Assert.That(UserService.IsSuperAdmin(renamed)).IsTrue();
    }

    // ---------------- 初始密码的输出通道 ----------------

    /// <summary>
    /// 初始密码必须走日志器，不能只写控制台：桌面端把核心当日志子进程拉起（GUI 无控制台），
    /// 只 Console.WriteLine 的话用户根本看不到自己第一次的登录密码。
    /// </summary>
    [Test]
    public async Task InitializeAsync_Logs_Seed_Admin_Password_To_Logger()
    {
        await using var ctx = BuildContext();
        var logger = new RecordingLogger();

        await DbInitializer.InitializeAsync(
            ctx, new AppConfiguration { AdminPassword = "seeded-pw-123" }, logger: logger);

        // 建了管理员，且密码出现在日志里
        await Assert.That(await ctx.Users.AnyAsync(u => u.Role == UserRole.Admin)).IsTrue();
        await Assert.That(logger.Messages.Any(m => m.Contains("seeded-pw-123"))).IsTrue();
        await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Information)).IsTrue();
    }

    /// <summary>
    /// 未配置初始密码时同样要把"没建号、去走安装向导"这条写进日志 ——
    /// 否则用户只看到"登录失败"，不知道下一步该做什么。
    /// </summary>
    [Test]
    public async Task InitializeAsync_Logs_Hint_When_No_Password_Configured()
    {
        await using var ctx = BuildContext();
        var logger = new RecordingLogger();

        await DbInitializer.InitializeAsync(ctx, new AppConfiguration(), logger: logger);

        await Assert.That(logger.Messages.Any(m => m.Contains("init/setup"))).IsTrue();
        await Assert.That(logger.Entries.Any(e => e.Level == LogLevel.Warning)).IsTrue();
    }

    private static OpenListDbContext BuildContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>()
            .UseSqlite(conn)
            .Options;
        var ctx = new OpenListDbContext(opts, new DatabaseConfig());
        ctx.Database.EnsureCreated();
        return ctx;
    }

    /// <summary>
    /// 收集日志输出的最小 ILogger 实现。DbInitializer 的凭据消息用
    /// <c>{CredentialMessage}</c> 结构化占位符 + 原始文本作为参数传入，
    /// 这里同时把两者都收下来，断言时只看渲染后的文本。
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Messages => Entries.Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            // 结构化占位符已被 formatter 渲染进 text，这里再兜一层防止只拿到模板。
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                var extra = string.Join(" ", pairs
                    .Where(p => p.Key == "CredentialMessage")
                    .Select(p => p.Value?.ToString() ?? ""));
                if (extra.Length > 0 && !text.Contains(extra)) text += " " + extra;
            }
            Entries.Add((logLevel, text));
        }
    }

    /// <summary>
    /// 以「配置了初始密码」的方式跑初始化，等价于运维在首次启动前设置了
    /// <c>OpenList__AdminPassword</c>。需要 admin 的测试都用它，而不是默认配置 ——
    /// 默认配置下不会创建管理员（交由安装向导）。
    /// </summary>
    private static Task InitWithAdminAsync(OpenListDbContext ctx, string password = "admin")
        => DbInitializer.InitializeAsync(ctx, new AppConfiguration { AdminPassword = password });

    /// <summary>
    /// 取管理员。库里可能同时存在 guest（对齐 Go 的 initUser），因此按角色查而不用
    /// <c>SingleAsync()</c> 的无参重载。
    /// </summary>
    private static Task<User> AdminAsync(OpenListDbContext ctx)
        => ctx.Users.SingleAsync(u => u.Role == UserRole.Admin);
}
