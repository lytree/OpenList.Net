using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenList.Core.Configuration;
using OpenList.Core.Interfaces;
using OpenList.Core.Models;
using OpenList.Infrastructure.Data;
using OpenList.Media.Domain;
using OpenList.Media.Domain.Abstractions;
using OpenList.Media.Services;

namespace OpenList.Tests;

/// <summary>
/// 关注 / 想看 / 下载三段的服务层用例。
/// <para>
/// 这三块共用一条链：关注一个人 → 检查出「没见过的作品」→ 其中一部加进想看 →
/// 提交下载 → 下载完成扫描入库 → 想看行被回填 <c>ItemId</c>。用例按这条链的顺序写，
/// 每步只钉一处口径，避免把「能跑」当成「语义对」。
/// </para>
/// <para>
/// 外部依赖全部替身化：TMDB 作品流用 <see cref="FakeFeedProvider"/>，
/// 离线下载与任务查询用假服务 —— 单测不该真的出网或起 aria2。
/// </para>
/// </summary>
public class MediaFollowWantDownloadTests
{
    /// <summary>已登录的管理员（<see cref="User.IsAdmin"/> 对 Id != 0 为真）。</summary>
    private static readonly User Admin = new() { Id = 1, Username = "admin" };

    /// <summary>匿名游客。</summary>
    private static readonly User Guest = new() { Id = 0 };

    // ------------------------------------------------------------------
    // 关注
    // ------------------------------------------------------------------

    [Test]
    public async Task Follow_Seeds_Feed_And_Feed_Keeps_Source_Order()
    {
        using var ctx = BuildContext();
        var personId = await SeedPersonAsync(ctx, "1001");
        var feed = new FakeFeedProvider()
            .With("1001", Work("movie", "11", "代表作"), Work("movie", "12", "第二部"), Work("tv", "13", "剧集"));
        var svc = NewFollowService(ctx, feed);

        var followed = await svc.FollowAsync(personId, Admin);
        await Assert.That(followed.Code).IsEqualTo(200);

        var state = await svc.GetStateAsync([personId], Admin);
        await Assert.That(state.Data![personId]).IsTrue();

        // 首次关注立刻检查：三部作品全部落成更新流条目，且保持数据源顺序（代表作在前）。
        var page = await svc.GetFeedAsync(1, 30, null, Admin);
        await Assert.That(page.Data!.Total).IsEqualTo(3);
        await Assert.That(string.Join(",", page.Data.Content.Select(e => e.Title)))
            .IsEqualTo("代表作,第二部,剧集");
    }

    [Test]
    public async Task Follow_Is_Idempotent_And_Rejects_Person_Without_Tmdb_Id()
    {
        using var ctx = BuildContext();
        var personId = await SeedPersonAsync(ctx, "1001");
        var localOnly = await SeedPersonAsync(ctx, null, "本地人物");
        var feed = new FakeFeedProvider().With("1001", Work("movie", "11", "A"));
        var svc = NewFollowService(ctx, feed);

        var first = await svc.FollowAsync(personId, Admin);
        var second = await svc.FollowAsync(personId, Admin);
        await Assert.That(first.Code).IsEqualTo(200);
        await Assert.That(second.Code).IsEqualTo(200);
        await Assert.That(await ctx.MediaFollows.CountAsync()).IsEqualTo(1);

        // 没有 TMDB id 的人物关注了也永远不会更新 —— 直接 400，而不是建一条死行。
        var noTmdb = await svc.FollowAsync(localOnly, Admin);
        await Assert.That(noTmdb.Code).IsEqualTo(400);

        // 游客不能关注（写操作要登录）。
        var guest = await svc.FollowAsync(personId, Guest);
        await Assert.That(guest.Code).IsEqualTo(403);
    }

    [Test]
    public async Task Unfollow_Removes_Entries_And_Is_Idempotent()
    {
        using var ctx = BuildContext();
        var personId = await SeedPersonAsync(ctx, "1001");
        var feed = new FakeFeedProvider().With("1001", Work("movie", "11", "A"), Work("movie", "12", "B"));
        var svc = NewFollowService(ctx, feed);
        await svc.FollowAsync(personId, Admin);

        await Assert.That((await svc.UnfollowAsync(personId, Admin)).Code).IsEqualTo(200);
        await Assert.That(await ctx.MediaFollows.CountAsync()).IsEqualTo(0);
        await Assert.That(await ctx.MediaFollowEntries.CountAsync()).IsEqualTo(0);
        // 再取关一次仍成功（幂等）。
        await Assert.That((await svc.UnfollowAsync(personId, Admin)).Code).IsEqualTo(200);
    }

    [Test]
    public async Task Check_Only_Adds_Works_Seen_For_The_First_Time()
    {
        using var ctx = BuildContext();
        var personId = await SeedPersonAsync(ctx, "1001");
        var feed = new FakeFeedProvider().With("1001", Work("movie", "11", "A"));
        var svc = NewFollowService(ctx, feed);
        await svc.FollowAsync(personId, Admin);

        // 数据源多出一部：只有新出现的那部落成条目。
        feed.With("1001", Work("movie", "11", "A"), Work("movie", "12", "新片"));
        var result = await svc.CheckAsync(personId, Admin);

        await Assert.That(result.Code).IsEqualTo(200);
        await Assert.That(result.Data!.NewEntries).IsEqualTo(1);
        await Assert.That(await ctx.MediaFollowEntries.CountAsync()).IsEqualTo(2);
    }

    [Test]
    public async Task Check_Records_Provider_Failure_Without_Throwing()
    {
        using var ctx = BuildContext();
        var personId = await SeedPersonAsync(ctx, "1001");
        var feed = new FakeFeedProvider { ThrowOn = "tmdb down" };
        var svc = NewFollowService(ctx, feed);

        // 关注本身成功（行已建好），失败原因落在 LastError 上给用户看。
        var followed = await svc.FollowAsync(personId, Admin);
        await Assert.That(followed.Code).IsEqualTo(200);

        var list = await svc.ListFollowedAsync(Admin);
        await Assert.That(list.Data!.Single().LastError).IsEqualTo("tmdb down");
    }

    // ------------------------------------------------------------------
    // 想看
    // ------------------------------------------------------------------

    [Test]
    public async Task Want_Add_Is_Idempotent_And_Links_Existing_Item()
    {
        using var ctx = BuildContext();
        var libraryId = await SeedLibraryAsync(ctx, "/lib");
        await SeedItemAsync(ctx, libraryId, "11", MediaLibraryType.Movie, "已经在库");

        var wants = new MediaWantService(ctx);
        var req = new AddMediaWantReq { ExternalId = "11", MediaType = "movie", Title = "已经在库" };

        var first = await wants.AddAsync(req, Admin);
        var second = await wants.AddAsync(req, Admin);

        await Assert.That(first.Code).IsEqualTo(200);
        await Assert.That(second.Code).IsEqualTo(200);
        await Assert.That(await ctx.MediaWants.CountAsync()).IsEqualTo(1);
        // 库里已有 → 加入时就回填 ItemId，「想看」页不会列它。
        await Assert.That(second.Data!.ItemId).IsNotNull();
    }

    [Test]
    public async Task Want_Add_Requires_Login_And_External_Id()
    {
        using var ctx = BuildContext();
        var wants = new MediaWantService(ctx);

        var guest = await wants.AddAsync(new AddMediaWantReq { ExternalId = "1", Title = "A" }, Guest);
        var empty = await wants.AddAsync(new AddMediaWantReq { ExternalId = "", Title = "A" }, Admin);
        var noTitle = await wants.AddAsync(new AddMediaWantReq { ExternalId = "1", Title = " " }, Admin);

        await Assert.That(guest.Code).IsEqualTo(403);
        await Assert.That(empty.Code).IsEqualTo(400);
        await Assert.That(noTitle.Code).IsEqualTo(400);
    }

    [Test]
    public async Task Want_List_Hides_Ingested_Unless_Asked()
    {
        using var ctx = BuildContext();
        var libraryId = await SeedLibraryAsync(ctx, "/lib");
        var wants = new MediaWantService(ctx);

        await wants.AddAsync(new AddMediaWantReq { ExternalId = "11", Title = "没入库" }, Admin);
        await wants.AddAsync(new AddMediaWantReq { ExternalId = "12", Title = "会入库" }, Admin);

        // 后一部被扫描入库 → 回填
        await SeedItemAsync(ctx, libraryId, "12", MediaLibraryType.Movie, "会入库");
        await wants.LinkIngestedAsync();

        var open = await wants.ListAsync(1, 30, false, Admin);
        var all = await wants.ListAsync(1, 30, true, Admin);

        await Assert.That(open.Data!.Total).IsEqualTo(1);
        await Assert.That(open.Data.Content.Single().Title).IsEqualTo("没入库");
        await Assert.That(all.Data!.Total).IsEqualTo(2);
    }

    [Test]
    public async Task Want_Remove_Also_Removes_Its_Downloads()
    {
        using var ctx = BuildContext();
        var wants = new MediaWantService(ctx);
        var want = await wants.AddAsync(new AddMediaWantReq { ExternalId = "11", Title = "A" }, Admin);
        ctx.MediaDownloads.Add(new MediaDownload
        {
            WantId = want.Data!.Id, LibraryId = 1, Title = "A", Url = "magnet:?xt=urn:x",
            Status = MediaDownloadStatus.Failed, CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        await wants.RemoveAsync(want.Data.Id, Admin);

        await Assert.That(await ctx.MediaWants.CountAsync()).IsEqualTo(0);
        await Assert.That(await ctx.MediaDownloads.CountAsync()).IsEqualTo(0);
    }

    // ------------------------------------------------------------------
    // 下载
    // ------------------------------------------------------------------

    [Test]
    public async Task Submit_Maps_Media_Type_To_DownloadType()
    {
        await Assert.That(MediaDownloadService.ResolveDownType("aria2")).IsEqualTo(TaskType.Aria2Down);
        await Assert.That(MediaDownloadService.ResolveDownType("qBittorrent")).IsEqualTo(TaskType.QbitDown);
        await Assert.That(MediaDownloadService.ResolveDownType("transmission")).IsEqualTo(TaskType.TransmissionDown);
        await Assert.That(MediaDownloadService.ResolveDownType("")).IsEqualTo(TaskType.OfflineDownload);
        await Assert.That(MediaDownloadService.ResolveDownType(null)).IsEqualTo(TaskType.OfflineDownload);
    }

    [Test]
    public async Task SanitizeDirectoryName_Strips_Path_Separators()
    {
        var name = MediaExternal.SanitizeDirectoryName("Bad/Name: With*Chars?");
        await Assert.That(name).DoesNotContain("/");
        await Assert.That(name).DoesNotContain(":");
        await Assert.That(name).DoesNotContain("*");
        await Assert.That(MediaExternal.SanitizeDirectoryName("   ")).IsEqualTo("media");
        // 尾部点会被 Windows 静默吞掉，必须在清洗时就剪掉。
        await Assert.That(MediaExternal.SanitizeDirectoryName("Movie...")).IsEqualTo("Movie");
    }

    [Test]
    public async Task Submit_Creates_Library_Subdirectory_And_Records_Task()
    {
        using var ctx = BuildContext();
        var libraryId = await SeedLibraryAsync(ctx, "/lib");
        var want = new MediaWant
        {
            ExternalId = "88", MediaType = "movie", Title = "Bad/Name: 2024", Year = 2024,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.MediaWants.Add(want);
        await ctx.SaveChangesAsync();

        var fs = new FakeFileSystem();
        var offline = new FakeOfflineDownload();
        var svc = new MediaDownloadService(
            ctx, new MediaLibraryService(ctx), new MediaWantService(ctx), offline,
            new FakeTaskService(), fs, new FakeScanService(), TimeProvider.System);

        var resp = await svc.SubmitAsync(new SubmitMediaDownloadReq
        {
            WantId = want.Id, LibraryId = libraryId, Url = "magnet:?xt=urn:x", Tool = "aria2",
        }, Admin);

        await Assert.That(resp.Code).IsEqualTo(200);
        var row = await ctx.MediaDownloads.AsNoTracking().SingleAsync();
        // 目录名把路径分隔符与冒号都洗掉，并带上年份。
        await Assert.That(row.TargetDirectory).IsEqualTo("/lib/Bad_Name_ 2024 (2024)");
        await Assert.That(row.TaskId).IsEqualTo("task-1");
        await Assert.That(row.Status).IsEqualTo(MediaDownloadStatus.Pending);
        await Assert.That(fs.CreatedDirs.Single()).IsEqualTo(row.TargetDirectory);
        await Assert.That(offline.Last!.Path).IsEqualTo(row.TargetDirectory);
    }

    [Test]
    public async Task Submit_Rejects_Unsupported_Url_And_Unknown_Want()
    {
        using var ctx = BuildContext();
        var libraryId = await SeedLibraryAsync(ctx, "/lib");
        var svc = NewDownloadService(ctx, new FakeTaskService(), new FakeScanService());

        var missing = await svc.SubmitAsync(new SubmitMediaDownloadReq
        {
            WantId = 999, LibraryId = libraryId, Url = "magnet:?xt=urn:x",
        }, Admin);
        await Assert.That(missing.Code).IsEqualTo(404);

        ctx.MediaWants.Add(new MediaWant { ExternalId = "1", Title = "A", MediaType = "movie", CreatedAt = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();
        var wantId = (await ctx.MediaWants.AsNoTracking().SingleAsync()).Id;

        var badUrl = await svc.SubmitAsync(new SubmitMediaDownloadReq
        {
            WantId = wantId, LibraryId = libraryId, Url = "ftp://example.com/a.mkv",
        }, Admin);
        await Assert.That(badUrl.Code).IsEqualTo(400);

        var guest = await svc.SubmitAsync(new SubmitMediaDownloadReq
        {
            WantId = wantId, LibraryId = libraryId, Url = "magnet:?xt=urn:x",
        }, Guest);
        await Assert.That(guest.Code).IsEqualTo(403);
    }

    [Test]
    public async Task Refresh_Maps_Succeeded_Task_To_Completed_And_Backfills_Item()
    {
        using var ctx = BuildContext();
        var libraryId = await SeedLibraryAsync(ctx, "/lib");
        var want = new MediaWant
        {
            ExternalId = "77", MediaType = "movie", Title = "会入库", Year = 2024,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.MediaWants.Add(want);
        await ctx.SaveChangesAsync();

        var tasks = new FakeTaskService();
        tasks.Set("t1", TaskState.Running, 50, 100);
        ctx.MediaDownloads.Add(new MediaDownload
        {
            WantId = want.Id, LibraryId = libraryId, Tool = "aria2", TaskId = "t1",
            Title = want.Title, Url = "magnet:?xt=urn:x", TargetDirectory = "/lib/会入库 (2024)",
            Status = MediaDownloadStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var svc = NewDownloadService(ctx, tasks, new FakeScanService());

        // 第一轮：任务在跑 → Downloading + 进度
        await svc.RefreshAsync();
        var row = await ctx.MediaDownloads.AsNoTracking().SingleAsync();
        await Assert.That(row.Status).IsEqualTo(MediaDownloadStatus.Downloading);
        await Assert.That(row.Progress).IsEqualTo(50);

        // 第二轮：任务成功 → Completed；库里刚好有这部电影 → 想看行被回填
        tasks.Set("t1", TaskState.Succeeded, 100, 100);
        await SeedItemAsync(ctx, libraryId, "77", MediaLibraryType.Movie, "会入库");
        await svc.RefreshAsync();

        row = await ctx.MediaDownloads.AsNoTracking().SingleAsync();
        await Assert.That(row.Status).IsEqualTo(MediaDownloadStatus.Completed);
        await Assert.That(row.Progress).IsEqualTo(100);
        await Assert.That(row.CompletedAt).IsNotNull();

        var linked = await ctx.MediaWants.AsNoTracking().SingleAsync();
        await Assert.That(linked.ItemId).IsNotNull();
    }

    [Test]
    public async Task Refresh_Marks_Cancelled_And_Failed_Tasks()
    {
        using var ctx = BuildContext();
        var libraryId = await SeedLibraryAsync(ctx, "/lib");
        var tasks = new FakeTaskService();
        tasks.Set("t1", TaskState.Errored, 0, 100, error: "no peers");
        tasks.Set("t2", TaskState.Canceled, 0, 100);
        ctx.MediaDownloads.AddRange(
            new MediaDownload { WantId = 1, LibraryId = libraryId, Tool = "qbit", TaskId = "t1", Title = "A", Url = "magnet:?x", TargetDirectory = "/lib/a", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow },
            new MediaDownload { WantId = 2, LibraryId = libraryId, Tool = "qbit", TaskId = "t2", Title = "B", Url = "magnet:?x", TargetDirectory = "/lib/b", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await ctx.SaveChangesAsync();

        await NewDownloadService(ctx, tasks, new FakeScanService()).RefreshAsync();

        var rows = await ctx.MediaDownloads.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
        await Assert.That(rows[0].Status).IsEqualTo(MediaDownloadStatus.Failed);
        await Assert.That(rows[0].Error).IsEqualTo("no peers");
        await Assert.That(rows[1].Status).IsEqualTo(MediaDownloadStatus.Cancelled);
    }

    // ------------------------------------------------------------------
    // 装置
    // ------------------------------------------------------------------

    private static MediaFollowService NewFollowService(OpenListDbContext ctx, IPersonFeedProvider feed)
        => new(ctx, new MediaLibraryService(ctx), feed, TimeProvider.System, personDelay: TimeSpan.Zero);

    private static MediaDownloadService NewDownloadService(
        OpenListDbContext ctx, FakeTaskService tasks, FakeScanService scans)
        => new(
            ctx,
            new MediaLibraryService(ctx),
            new MediaWantService(ctx),
            new FakeOfflineDownload(),
            tasks,
            new FakeFileSystem(),
            scans,
            TimeProvider.System);

    private static PersonFeedWork Work(string type, string id, string title)
        => new() { MediaType = type, ExternalId = id, Title = title, ReleaseDate = "2024-01-01" };

    private static async Task<long> SeedPersonAsync(OpenListDbContext ctx, string? tmdbId, string name = "演员")
    {
        var person = new MediaPerson
        {
            Name = name, TmdbId = tmdbId, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        ctx.MediaPersons.Add(person);
        await ctx.SaveChangesAsync();
        return person.Id;
    }

    private static async Task<long> SeedLibraryAsync(OpenListDbContext ctx, string path)
    {
        var library = new MediaLibrary
        {
            Name = "Lib", Path = path, Type = MediaLibraryType.Movie,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        ctx.MediaLibraries.Add(library);
        await ctx.SaveChangesAsync();
        return library.Id;
    }

    private static async Task SeedItemAsync(
        OpenListDbContext ctx, long libraryId, string tmdbId, MediaLibraryType type, string title)
    {
        ctx.MediaItems.Add(new MediaItem
        {
            LibraryId = libraryId, TmdbId = tmdbId, Type = type, Title = title,
            DirectoryPath = $"/lib/{title}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private static OpenListDbContext BuildContext()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<OpenListDbContext>().UseSqlite(conn).Options;
        var ctx = new OpenListDbContext(opts, new DatabaseConfig());
        ctx.Database.EnsureCreated();
        return ctx;
    }

    /// <summary>TMDB combined_credits 的替身：按 person tmdb id 返回预置作品。</summary>
    private sealed class FakeFeedProvider : IPersonFeedProvider
    {
        private readonly Dictionary<string, List<PersonFeedWork>> _works = [];

        /// <summary>非空时任何调用都抛这个错（模拟 TMDB 不可用）。</summary>
        public string? ThrowOn { get; set; }

        public FakeFeedProvider With(string tmdbId, params PersonFeedWork[] works)
        {
            _works[tmdbId] = [.. works];
            return this;
        }

        public Task<IReadOnlyList<PersonFeedWork>> GetWorksAsync(string tmdbPersonId, CancellationToken ct = default)
        {
            if (ThrowOn is not null) throw new InvalidOperationException(ThrowOn);
            return Task.FromResult<IReadOnlyList<PersonFeedWork>>(
                _works.TryGetValue(tmdbPersonId, out var list) ? list : []);
        }
    }

    /// <summary>离线下载底座的替身：只记请求，不真的下载。</summary>
    private sealed class FakeOfflineDownload : IOfflineDownloadService
    {
        public OfflineDownloadReq? Last { get; private set; }

        public Task<Resp<OfflineDownloadResp>> AddAsync(
            OfflineDownloadReq req, User user, CancellationToken ct = default)
        {
            Last = req;
            return Task.FromResult(Resp<OfflineDownloadResp>.Ok(
                new OfflineDownloadResp { Tasks = ["task-1"] }));
        }
    }

    /// <summary>任务服务的替身：按 tid 返回预置状态。</summary>
    private sealed class FakeTaskService : ITaskService
    {
        private readonly Dictionary<string, TaskInfo> _tasks = [];

        public void Set(string tid, TaskState state, long progress, long total, string? error = null)
            => _tasks[tid] = new TaskInfo
            {
                Id = tid, Type = TaskType.Aria2Down, State = state,
                Progress = progress, Total = total, Error = error,
            };

        public Task<Resp<TaskInfo>> InfoAsync(TaskType type, string tid, CancellationToken ct = default)
            => Task.FromResult(_tasks.TryGetValue(tid, out var info)
                ? Resp<TaskInfo>.Ok(info)
                : Resp<TaskInfo>.Fail("task not found", RespCodes.NotFound));

        public Task<Resp<List<TaskInfo>>> DoneAsync(TaskType type, CancellationToken ct = default)
            => Task.FromResult(Resp<List<TaskInfo>>.Ok([]));

        public Task<Resp<List<TaskInfo>>> UndoneAsync(TaskType type, CancellationToken ct = default)
            => Task.FromResult(Resp<List<TaskInfo>>.Ok([]));

        public Task<Resp> DeleteAsync(TaskType type, string tid, CancellationToken ct = default)
            => Task.FromResult(Resp.Ok());

        public Task<Resp> CancelAsync(TaskType type, string tid, CancellationToken ct = default)
        {
            if (_tasks.TryGetValue(tid, out var info)) info.State = TaskState.Canceled;
            return Task.FromResult(Resp.Ok());
        }

        public Task<Resp> ClearAsync(TaskType type, CancellationToken ct = default) => Task.FromResult(Resp.Ok());
        public Task<Resp> ClearSucceededAsync(TaskType type, CancellationToken ct = default) => Task.FromResult(Resp.Ok());
        public Task<Resp> RetryAsync(TaskType type, string tid, CancellationToken ct = default) => Task.FromResult(Resp.Ok());
        public Task<Resp> RetryFailedAsync(TaskType type, CancellationToken ct = default) => Task.FromResult(Resp.Ok());

        public Task<Resp<Dictionary<string, string>>> CancelSomeAsync(
            TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default)
            => Task.FromResult(Resp<Dictionary<string, string>>.Ok([]));

        public Task<Resp<Dictionary<string, string>>> DeleteSomeAsync(
            TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default)
            => Task.FromResult(Resp<Dictionary<string, string>>.Ok([]));

        public Task<Resp<Dictionary<string, string>>> RetrySomeAsync(
            TaskType type, IReadOnlyList<string> tids, CancellationToken ct = default)
            => Task.FromResult(Resp<Dictionary<string, string>>.Ok([]));

        public long? OwnerOf(string tid) => 1;

        public Task<string> CreateAsync(
            TaskType type, Func<IProgress<TaskProgress>, CancellationToken, Task> work,
            CancellationToken ct = default, long ownerId = 0)
            => Task.FromResult(Guid.NewGuid().ToString("N"));

        public Task RestoreAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>媒体扫描的替身：只记「被触发过」，不真的扫盘。</summary>
    private sealed class FakeScanService : IMediaScanService
    {
        public List<long> Started { get; } = [];

        public Task<Resp> StartAsync(long libraryId, User user, CancellationToken ct = default)
        {
            Started.Add(libraryId);
            return Task.FromResult(Resp.Ok());
        }

        public Task<Resp> StopAsync(long libraryId, User user, CancellationToken ct = default)
            => Task.FromResult(Resp.Ok());

        public Task<Resp<MediaScanProgress>> GetProgressAsync(long libraryId, CancellationToken ct = default)
            => Task.FromResult(Resp<MediaScanProgress>.Ok(new MediaScanProgress()));

        public Task<Resp<List<MediaScanLog>>> ListLogsAsync(
            long libraryId, int limit, User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaScanLog>>.Ok([]));

        public Task<Resp<List<MediaScanTask>>> ListTasksAsync(User user, CancellationToken ct = default)
            => Task.FromResult(Resp<List<MediaScanTask>>.Ok([]));
    }

    /// <summary>文件系统的替身：只实现下载服务用到的 Mkdir，其余成员都当「不该被调到」。</summary>
    private sealed class FakeFileSystem : IFileSystemService
    {
        public List<string> CreatedDirs { get; } = [];

        public Task<Resp> MkdirAsync(string path, User user, CancellationToken ct = default)
        {
            CreatedDirs.Add(path);
            return Task.FromResult(Resp.Ok());
        }

        public Task<Resp<FsListResp>> ListAsync(FsListReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<FsGetResp>> GetAsync(FsGetReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RenameAsync(string oldPath, string newPath, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> BatchRenameAsync(IReadOnlyList<BatchRenameItem> items, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RegexRenameAsync(string srcDir, string srcReg, string newReg, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> MoveAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RecursiveMoveAsync(string srcPath, string destDir, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> CopyAsync(IReadOnlyList<string> srcPaths, string destDir, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<FsManageResp>> MoveBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<FsManageResp>> CopyBatchAsync(MoveCopyReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RemoveAsync(IReadOnlyList<string> paths, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> CopyToAsync(string srcPath, string destPath, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> RemoveEmptyDirectoryAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> PutStreamAsync(string path, Stream stream, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> PutFormAsync(string path, Stream stream, string fileName, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<byte[]>> ReadFileAsync(string path, long maxBytes, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<DirectUploadInfo>> GetDirectUploadInfoAsync(string path, long size, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<LinkResp>> LinkAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> AddOfflineDownloadAsync(OfflineDownloadReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<ArchiveMetaResp>> ArchiveMetaAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<ArchiveListResp>> ArchiveListAsync(string path, string? password, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<Stream>> OpenArchiveEntryAsync(string archivePath, string entryPath, string? password, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> ArchiveDecompressAsync(string archivePath, string destPath, string? password, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<DirsResp>> DirsAsync(string path, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp> OtherAsync(string path, string action, User user, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Resp<SearchResp>> SearchAsync(SearchReq req, User user, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
