using OpenList.Core.Models;

namespace OpenList.Tests;

public class ArgsTests
{
    [Test]
    public async Task PageReq_Validate_ShouldCorrectInvalidValues()
    {
        var page = new PageReq { Page = -1, PerPage = -5 };
        page.Validate();

        await Assert.That(page.Page).IsEqualTo(1);
        await Assert.That(page.PerPage).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task PageReq_Validate_ShouldAcceptValidValues()
    {
        var page = new PageReq { Page = 5, PerPage = 20 };
        page.Validate();

        await Assert.That(page.Page).IsEqualTo(5);
        await Assert.That(page.PerPage).IsEqualTo(20);
    }

    [Test]
    public async Task PageReq_Validate_ZeroPerPage_ShouldReset()
    {
        var page = new PageReq { Page = 1, PerPage = 0 };
        page.Validate();

        await Assert.That(page.PerPage).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Link_DefaultValues()
    {
        var link = new Link();
        await Assert.That(link.URL).IsNull();
        await Assert.That(link.Header).IsNull();
        await Assert.That(link.Expiration).IsNull();
        await Assert.That(link.Concurrency).IsEqualTo(0);
        await Assert.That(link.PartSize).IsEqualTo(0);
    }

    [Test]
    public async Task Link_WithValues()
    {
        var link = new Link
        {
            URL = "https://example.com/file",
            Header = new Dictionary<string, string> { { "Authorization", "Bearer token" } },
            Expiration = TimeSpan.FromMinutes(30),
            Concurrency = 4,
            PartSize = 1024
        };

        await Assert.That(link.URL).IsEqualTo("https://example.com/file");
        await Assert.That(link.Header).HasCount().EqualTo(1);
        await Assert.That(link.Expiration).IsEqualTo(TimeSpan.FromMinutes(30));
    }

    [Test]
    public async Task ListArgs_DefaultValues()
    {
        var args = new ListArgs();
        await Assert.That(args.ReqPath).IsEmpty();
        await Assert.That(args.Refresh).IsFalse();
        await Assert.That(args.WithStorageDetails).IsFalse();
    }

    [Test]
    public async Task LinkArgs_DefaultValues()
    {
        var args = new LinkArgs();
        await Assert.That(args.IP).IsEmpty();
        await Assert.That(args.Type).IsEmpty();
        await Assert.That(args.Redirect).IsFalse();
    }

    [Test]
    public async Task ArchiveMetaResult_DefaultValues()
    {
        var meta = new ArchiveMetaResult();
        await Assert.That(meta.Comment).IsEmpty();
        await Assert.That(meta.IsEncrypted).IsFalse();
        await Assert.That(meta.Content).IsNotNull().And.IsEmpty();
    }

    [Test]
    public async Task ArchiveContentEntry_ShouldStoreChildren()
    {
        var entry = new ArchiveContentEntry
        {
            Name = "root",
            IsDir = true,
            Children =
            [
                new ArchiveContentEntry { Name = "child1.txt", Size = 100 },
                new ArchiveContentEntry { Name = "child2.txt", Size = 200 }
            ]
        };

        await Assert.That(entry.Children).HasCount().EqualTo(2);
        await Assert.That(entry.Children[0].Name).IsEqualTo("child1.txt");
    }

    [Test]
    public async Task StorageDetails_FreeSpace_Calculated()
    {
        var details = new StorageDetails { TotalSpace = 1000, UsedSpace = 400 };
        await Assert.That(details.FreeSpace).IsEqualTo(600);
    }

    [Test]
    public async Task WrongArchivePasswordException_ShouldHaveMessage()
    {
        var ex = new WrongArchivePasswordException();
        await Assert.That(ex.Message).IsEqualTo("wrong archive password");
    }
}
