using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Infrastructure.Services;
using Zio;
using Zio.FileSystems;

namespace OpenList.Tests;

/// <summary>
/// Phase 13 — 搜索索引增量更新：ObjsUpdateHooks 发布/订阅、SearchService 增量索引、
/// 设置门控（search_index/auto_update_index）、启动恢复（InitIndexAsync）。
/// </summary>
public class SearchServiceTests
{
    // ---------------- ObjsUpdateHooks ----------------

    [Test]
    public async Task ObjsUpdateHooks_Invokes_Directory_Handler_On_Publish()
    {
        var hooks = new ObjsUpdateHooks();
        var received = new List<(string Parent, int Count)>();
        hooks.Subscribe((parent, entries, _) =>
        {
            received.Add((parent, entries.Count));
            return Task.CompletedTask;
        });

        await hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj> { new() { Name = "a.txt" }, new() { Name = "b.txt" } });

        await Assert.That(received).HasCount().EqualTo(1);
        await Assert.That(received[0].Parent).IsEqualTo("/docs");
        await Assert.That(received[0].Count).IsEqualTo(2);
    }

    [Test]
    public async Task ObjsUpdateHooks_Invokes_PathRemoved_Handler()
    {
        var hooks = new ObjsUpdateHooks();
        var removed = new List<string>();
        hooks.Subscribe((_, _, _) => Task.CompletedTask, (path, _) =>
        {
            removed.Add(path);
            return Task.CompletedTask;
        });

        await hooks.PublishPathRemovedAsync("/docs/old.txt");

        await Assert.That(removed).HasCount().EqualTo(1);
        await Assert.That(removed[0]).IsEqualTo("/docs/old.txt");
    }

    [Test]
    public async Task ObjsUpdateHooks_Isolates_Handler_Exceptions()
    {
        var hooks = new ObjsUpdateHooks();
        hooks.Subscribe((_, _, _) => throw new InvalidOperationException("boom"));

        // 单个订阅者抛异常不应冒泡到发布者（否则会破坏文件系统请求）。
        await hooks.PublishDirectoryUpdateAsync("/", new List<Obj>());
    }

    // ---------------- SearchService 增量索引 ----------------

    [Test]
    public async Task PublishDirectoryUpdateAsync_Adds_New_Entries()
    {
        await using var fixture = new Fixture();
        fixture.SetDatabaseBackend();

        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj>
        {
            new() { Name = "a.txt", Size = 10 },
            new() { Name = "sub", IsDir = true },
        });

        var rows = await fixture.Db().SearchIndex.ToListAsync();
        await Assert.That(rows).HasCount().EqualTo(2);
        await Assert.That(rows.Any(r => r.Parent == "/docs" && r.Name == "a.txt" && r.Size == 10)).IsTrue();
        await Assert.That(rows.Any(r => r.Parent == "/docs" && r.Name == "sub" && r.IsDir)).IsTrue();
        await Assert.That(rows.Any(r => r.Path == "/docs/a.txt")).IsTrue();
    }

    [Test]
    public async Task PublishDirectoryUpdateAsync_Removes_Stale_Entries()
    {
        await using var fixture = new Fixture();
        fixture.SetDatabaseBackend();
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj>
        {
            new() { Name = "a.txt" },
            new() { Name = "b.txt" },
        });
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj>
        {
            new() { Name = "a.txt" },
        });

        var rows = await fixture.Db().SearchIndex.ToListAsync();
        await Assert.That(rows).HasCount().EqualTo(1);
        await Assert.That(rows[0].Name).IsEqualTo("a.txt");
    }

    [Test]
    public async Task PublishPathRemovedAsync_Deletes_Path_And_Subtree()
    {
        await using var fixture = new Fixture();
        fixture.SetDatabaseBackend();
        // /docs/{keep,drop.txt,drop/…}；再单独建立 /docs2 验证不会被误删。
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj>
        {
            new() { Name = "keep.txt" },
            new() { Name = "drop.txt" },
            new() { Name = "drop", IsDir = true },
        });
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs/drop", new List<Obj>
        {
            new() { Name = "nested.txt" },
        });
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs2", new List<Obj>
        {
            new() { Name = "other.txt" },
        });

        await fixture.Hooks.PublishPathRemovedAsync("/docs/drop.txt");
        await fixture.Hooks.PublishPathRemovedAsync("/docs/drop");

        var names = (await fixture.Db().SearchIndex.ToListAsync()).Select(r => r.Path).OrderBy(p => p).ToList();
        await Assert.That(names).Contains("/docs/keep.txt");
        await Assert.That(names).Contains("/docs2/other.txt");
        await Assert.That(names.Any(p => p.StartsWith("/docs/drop"))).IsFalse();
    }

    [Test]
    public async Task PublishDirectoryUpdateAsync_Skips_When_Index_Not_Built_Yet()
    {
        // 不调用 InitIndexAsync / BuildIndexAsync → _lastDoneTime 为空 → 禁止增量。
        // 只设置 search_index / auto_update_index 两个开关，但不写“已完成”进度。
        await using var fixture = new Fixture();
        await fixture.SaveSetting("search_index", "database");
        await fixture.SaveSetting("auto_update_index", "true");

        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj> { new() { Name = "a.txt" } });

        await Assert.That(await fixture.Db().SearchIndex.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task PublishDirectoryUpdateAsync_Skips_When_AutoUpdateIndex_Disabled()
    {
        await using var fixture = new Fixture();
        fixture.SetDatabaseBackend();
        // 关闭 auto_update_index（保留 search_index=database）。
        await fixture.SaveSetting("auto_update_index", "false");
        fixture.InitIndex(); // 让“已构建”标记生效，隔离本测试只验证 auto_update_index 门控。

        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj> { new() { Name = "a.txt" } });

        await Assert.That(await fixture.Db().SearchIndex.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task PublishDirectoryUpdateAsync_Skips_For_Non_Database_Backend()
    {
        await using var fixture = new Fixture();
        // 后端保持默认 none。
        fixture.InitIndex();

        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj> { new() { Name = "a.txt" } });

        await Assert.That(await fixture.Db().SearchIndex.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task InitIndexAsync_Restores_LastDoneTime_From_Persisted_Progress()
    {
        await using var fixture = new Fixture();
        // 先持久化一条“已完成”的进度（含 last_done_time），模拟上次成功构建后重启。
        await fixture.SaveSetting("index_progress",
            """{"obj_count":5,"is_done":true,"last_done_time":"2026-08-28T00:00:00+00:00","error":""}""");

        var r = await fixture.Search.InitIndexAsync();
        await Assert.That(r.Code).IsEqualTo(200);

        // 恢复后增量更新应被允许。
        fixture.SetDatabaseBackend();
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj> { new() { Name = "a.txt" } });
        await Assert.That(await fixture.Db().SearchIndex.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task InitIndexAsync_Recovers_Stale_Running_State()
    {
        await using var fixture = new Fixture();
        // 崩溃遗留：is_done=false → 恢复后增量更新应重新被允许。
        await fixture.SaveSetting("index_progress",
            """{"obj_count":3,"is_done":false,"last_done_time":"2026-08-27T00:00:00+00:00","error":""}""");

        var r = await fixture.Search.InitIndexAsync();
        await Assert.That(r.Code).IsEqualTo(200);

        var progress = (await fixture.Search.GetProgressAsync()).Data!;
        await Assert.That(progress.State).IsEqualTo("idle");

        fixture.SetDatabaseBackend();
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj> { new() { Name = "a.txt" } });
        await Assert.That(await fixture.Db().SearchIndex.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ClearIndexAsync_Persists_Empty_Progress()
    {
        await using var fixture = new Fixture();
        fixture.SetDatabaseBackend();
        await fixture.Hooks.PublishDirectoryUpdateAsync("/docs", new List<Obj> { new() { Name = "a.txt" } });

        var r = await fixture.Search.ClearIndexAsync();
        await Assert.That(r.Code).IsEqualTo(200);
        await Assert.That(await fixture.Db().SearchIndex.CountAsync()).IsEqualTo(0);

        var item = (await fixture.GetSetting("index_progress")).Data!;
        await Assert.That(item.Value).Contains("\"is_done\":true");
    }

    // ---------------- FileSystemService → 钩子集成 ----------------

    [Test]
    public async Task MkdirAsync_Publishes_Parent_Directory_Update()
    {
        var hooks = new ObjsUpdateHooks();
        var received = new List<string>();
        hooks.Subscribe((parent, entries, _) =>
        {
            lock (received) { received.Add($"{parent}:{entries.Count}"); }
            return Task.CompletedTask;
        });

        var mount = new ZioMountService();
        var mem = new MemoryFileSystem();
        mount.Root.Mount("/test", mem);
        var fs = new FileSystemService(mount, null, null, hooks);

        var r = await fs.MkdirAsync("/test/newdir", new User { Id = 1, Username = "t", Role = UserRole.Admin });
        await Assert.That(r.Code).IsEqualTo(200);

        // PublishDir 是 fire-and-forget，轮询等待订阅者收到父目录 /test 的更新。
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (received)
            {
                if (received.Count > 0) break;
            }
            await Task.Delay(20);
        }

        List<string> snapshot;
        lock (received) { snapshot = new List<string>(received); }
        await Assert.That(snapshot.Count).IsGreaterThan(0);
        await Assert.That(snapshot[0]).IsEqualTo("/test:1");
    }

    [Test]
    public async Task ListAsync_Publishes_Directory_Update()
    {
        var hooks = new ObjsUpdateHooks();
        var received = new List<string>();
        hooks.Subscribe((parent, entries, _) =>
        {
            lock (received) { received.Add($"{parent}:{entries.Count}"); }
            return Task.CompletedTask;
        });

        var mount = new ZioMountService();
        var mem = new MemoryFileSystem();
        mount.Root.Mount("/test", mem);
        await using (var s = mount.Root.OpenFile("/test/a.txt", FileMode.Create, FileAccess.Write))
            await s.WriteAsync(Encoding.UTF8.GetBytes("x"));
        var fs = new FileSystemService(mount, null, null, hooks);

        var r = await fs.ListAsync(new FsListReq { Path = "/test" }, new User { Id = 1, Username = "t", Role = UserRole.Admin });
        await Assert.That(r.Code).IsEqualTo(200);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (received)
            {
                if (received.Count > 0) break;
            }
            await Task.Delay(20);
        }

        List<string> snapshot;
        lock (received) { snapshot = new List<string>(received); }
        await Assert.That(snapshot.Count).IsGreaterThan(0);
        await Assert.That(snapshot[0]).IsEqualTo("/test:1");
    }

    /// <summary>共享测试装置：内存 SQLite + DI 容器（scoped DbContext / SettingService + singleton hooks / FileSystemService）。</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _conn;
        private readonly ServiceProvider _provider;
        private readonly DatabaseConfig _dbConfig = new();

        public SearchService Search { get; }
        public IObjsUpdateHooks Hooks { get; }

        public Fixture()
        {
            _conn = new SqliteConnection("DataSource=:memory:");
            _conn.Open();

            var opts = new DbContextOptionsBuilder<OpenListDbContext>().UseSqlite(_conn).Options;
            // EnsureCreated 需要先建表（含 x_search_index / x_settings）。
            using (var init = new OpenListDbContext(opts, _dbConfig))
                init.Database.EnsureCreated();

            var appConfig = new AppConfiguration
            {
                JwtSecret = "testsecret_testsecret_testsecret_",
                Database = _dbConfig,
            };

            var services = new ServiceCollection();
            services.AddSingleton(_dbConfig);
            services.AddScoped<OpenListDbContext>(_ => new OpenListDbContext(opts, _dbConfig));
            services.AddScoped<ISettingService>(sp =>
                new SettingService(sp.GetRequiredService<OpenListDbContext>(), appConfig));
            services.AddSingleton<IObjsUpdateHooks, ObjsUpdateHooks>();
            services.AddSingleton(sp =>
            {
                var mount = new ZioMountService();
                mount.Root.Mount("/test", new MemoryFileSystem());
                return mount;
            });
            services.AddSingleton<IFileSystemService>(sp =>
                new FileSystemService(sp.GetRequiredService<ZioMountService>(), null, null,
                    sp.GetService<IObjsUpdateHooks>()));
            _provider = services.BuildServiceProvider();

            Hooks = _provider.GetRequiredService<IObjsUpdateHooks>();
            Search = new SearchService(
                _provider.GetRequiredService<IFileSystemService>(),
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Hooks);
        }

        /// <summary>写入设置项（无默认元信息时用 String 类型）。</summary>
        public Task SaveSetting(string key, string value) => SaveSetting(key, value, SettingValueType.String);

        public async Task SaveSetting(string key, string value, SettingValueType type)
        {
            using var scope = _provider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            await svc.SaveAsync([new SettingItem { Key = key, Value = value, Type = type }]);
        }

        public async Task<Resp<SettingItem>> GetSetting(string key)
        {
            using var scope = _provider.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ISettingService>();
            return await svc.GetAsync(key);
        }

        /// <summary>切换到 database 后端并开启自动增量。</summary>
        public void SetDatabaseBackend()
        {
            SaveSetting("search_index", "database").GetAwaiter().GetResult();
            SaveSetting("auto_update_index", "true").GetAwaiter().GetResult();
            InitIndex();
        }

        /// <summary>写一条“已完成”进度并恢复（使增量门控放行，模拟上次成功构建后重启）。</summary>
        public void InitIndex()
        {
            SaveSetting("index_progress",
                """{"obj_count":0,"is_done":true,"last_done_time":"2026-08-28T00:00:00+00:00","error":""}""")
                .GetAwaiter().GetResult();
            Search.InitIndexAsync().GetAwaiter().GetResult();
        }

        public OpenListDbContext Db()
        {
            var scope = _provider.CreateScope();
            return scope.ServiceProvider.GetRequiredService<OpenListDbContext>();
        }

        public ValueTask DisposeAsync()
        {
            _provider.Dispose();
            _conn.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
