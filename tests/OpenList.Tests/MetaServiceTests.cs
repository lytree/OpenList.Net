using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

/// <summary>
/// MetaService tests against in-memory SQLite.
/// </summary>
public class MetaServiceTests
{
    [Test]
    public async Task CreateAsync_Persists_Meta()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        var r = await svc.CreateAsync(new Meta { Path = "/test", Password = "secret", Write = true });
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Path).IsEqualTo("/test");
        await Assert.That(r.Data.Password).IsEqualTo("secret");
    }

    [Test]
    public async Task CreateAsync_Rejects_Duplicate_Path()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        await svc.CreateAsync(new Meta { Path = "/dup" });
        var r = await svc.CreateAsync(new Meta { Path = "/dup" });
        await Assert.That(r.Code).IsEqualTo(409);
    }

    [Test]
    public async Task CreateAsync_Rejects_Empty_Path()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        var r = await svc.CreateAsync(new Meta { Path = "" });
        await Assert.That(r.Code).IsEqualTo(400);
    }

    [Test]
    public async Task GetAsync_Returns_Meta_By_Path()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        await svc.CreateAsync(new Meta { Path = "/docs", Readme = "hello" });
        var r = await svc.GetAsync("/docs");
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(r.Data!.Readme).IsEqualTo("hello");
    }

    [Test]
    public async Task GetAsync_Returns_NotFound_For_Missing()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        var r = await svc.GetAsync("/nope");
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task ListAsync_Returns_All_Metas_Ordered_By_Path()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        await svc.CreateAsync(new Meta { Path = "/b" });
        await svc.CreateAsync(new Meta { Path = "/a" });
        var r = await svc.ListAsync();
        await Assert.That(r.Data!.Count).IsEqualTo(2);
        await Assert.That(r.Data[0].Path).IsEqualTo("/a");
    }

    [Test]
    public async Task UpdateAsync_Modifies_All_Fields()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        var created = await svc.CreateAsync(new Meta { Path = "/u" });
        var r = await svc.UpdateAsync(created.Data!.Id, new Meta
        {
            Path = "/u2",
            Password = "pw",
            Write = true,
            Hide = true,
            Readme = "updated",
            PSub = true,
            Header = "X-Test: 1",
            WebdavPolicy = true,
            WebdavRead = true,
            WebdavWrite = false,
            WebdavManagement = false,
        });
        await Assert.That(r.Code).IsEqualTo(200);
        var meta = (await svc.GetAsync("/u2")).Data!;
        await Assert.That(meta.Password).IsEqualTo("pw");
        await Assert.That(meta.Write).IsTrue();
        await Assert.That(meta.Hide).IsTrue();
        await Assert.That(meta.Readme).IsEqualTo("updated");
        await Assert.That(meta.PSub).IsTrue();
    }

    [Test]
    public async Task UpdateAsync_Returns_NotFound_For_Missing()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        var r = await svc.UpdateAsync(999, new Meta { Path = "/x" });
        await Assert.That(r.Code).IsEqualTo(404);
    }

    [Test]
    public async Task DeleteAsync_Removes_Meta()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        var created = await svc.CreateAsync(new Meta { Path = "/del" });
        var r = await svc.DeleteAsync(created.Data!.Id);
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(await ctx.Metas.AnyAsync()).IsFalse();
    }

    [Test]
    public async Task DeleteAsync_Returns_NotFound_For_Missing()
    {
        await using var ctx = BuildContext();
        var svc = new MetaService(ctx);
        var r = await svc.DeleteAsync(999);
        await Assert.That(r.Code).IsEqualTo(404);
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
}
