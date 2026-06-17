using OpenList.Core.Models;

namespace OpenList.Tests;

public class ObjItemTests
{
    [Test]
    public async Task ObjItem_Properties_ShouldGetSet()
    {
        var item = new ObjItem
        {
            Name = "test.txt",
            Size = 1024,
            IsFolder = false,
            Path = "/root/test.txt",
            ID = "id1",
            HashInfo = "abc123",
            Modified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        await Assert.That(item.GetName()).IsEqualTo("test.txt");
        await Assert.That(item.GetSize()).IsEqualTo(1024);
        await Assert.That(item.IsDir()).IsFalse();
        await Assert.That(item.GetPath()).IsEqualTo("/root/test.txt");
        await Assert.That(item.GetID()).IsEqualTo("id1");
        await Assert.That(item.GetHash()).IsEqualTo("abc123");
        await Assert.That(item.GetModTime()).IsEqualTo(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task ObjItem_GetCreateTime_DefaultsToModified()
    {
        var modified = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var item = new ObjItem { Name = "test.txt", Modified = modified };

        await Assert.That(item.GetCreateTime()).IsEqualTo(modified);
    }

    [Test]
    public async Task ObjItem_GetCreateTime_UsesCTime_WhenSet()
    {
        var modified = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var ctime = new DateTime(2024, 6, 14, 10, 0, 0, DateTimeKind.Utc);
        var item = new ObjItem { Name = "test.txt", Modified = modified, CTime = ctime };

        await Assert.That(item.GetCreateTime()).IsEqualTo(ctime);
    }

    [Test]
    public async Task ObjItem_IsFolder_ShouldReflectIsDir()
    {
        var file = new ObjItem { Name = "file.txt", IsFolder = false };
        var dir = new ObjItem { Name = "dir", IsFolder = true };

        await Assert.That(file.IsDir()).IsFalse();
        await Assert.That(dir.IsDir()).IsTrue();
    }

    [Test]
    public async Task ObjItem_Mask_ShouldStoreFlags()
    {
        var item = new ObjItem { Name = "virtual", Mask = ObjMask.Virtual | ObjMask.NoRename };
        await Assert.That(item.Mask.HasFlag(ObjMask.Virtual)).IsTrue();
        await Assert.That(item.Mask.HasFlag(ObjMask.NoRename)).IsTrue();
        await Assert.That(item.Mask.HasFlag(ObjMask.NoRemove)).IsFalse();
    }

    [Test]
    public async Task ObjWrapName_ShouldDelegateToInner()
    {
        var inner = new ObjItem
        {
            Name = "original.txt",
            Size = 512,
            IsFolder = false,
            Path = "/root/original.txt",
            Modified = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        var wrapped = new ObjWrapName { Name = "renamed.txt", Inner = inner };

        await Assert.That(wrapped.GetName()).IsEqualTo("renamed.txt");
        await Assert.That(wrapped.GetSize()).IsEqualTo(512);
        await Assert.That(wrapped.IsDir()).IsFalse();
        await Assert.That(wrapped.GetPath()).IsEqualTo("/root/original.txt");
        await Assert.That(wrapped.GetModTime()).IsEqualTo(inner.GetModTime());
    }

    [Test]
    public async Task ObjThumb_ShouldHaveThumbnail()
    {
        var thumb = new ObjThumb
        {
            Name = "image.jpg",
            Size = 1024,
            Thumbnail = "/thumbs/image.png"
        };

        await Assert.That(thumb.Thumbnail).IsEqualTo("/thumbs/image.png");
        await Assert.That(thumb.GetName()).IsEqualTo("image.jpg");
    }

    [Test]
    public async Task ObjectURL_ShouldHaveUrl()
    {
        var objUrl = new ObjectURL
        {
            Name = "file.txt",
            Url = "https://example.com/file.txt"
        };

        await Assert.That(objUrl.Url).IsEqualTo("https://example.com/file.txt");
    }

    [Test]
    public async Task ObjThumbURL_ShouldHaveBoth()
    {
        var thumbUrl = new ObjThumbURL
        {
            Name = "video.mp4",
            Thumbnail = "/thumbs/video.png",
            Url = "https://example.com/video.mp4"
        };

        await Assert.That(thumbUrl.Thumbnail).IsEqualTo("/thumbs/video.png");
        await Assert.That(thumbUrl.Url).IsEqualTo("https://example.com/video.mp4");
    }

    [Test]
    public async Task ObjTypeHelper_GetThumb_ShouldReturnThumbnail()
    {
        var thumb = new ObjThumb { Name = "img.jpg", Thumbnail = "/thumb.png" };
        var thumbUrl = new ObjThumbURL { Name = "vid.mp4", Thumbnail = "/thumb2.png" };
        var plain = new ObjItem { Name = "doc.txt" };

        await Assert.That(ObjTypeHelper.GetThumb(thumb)).IsEqualTo("/thumb.png");
        await Assert.That(ObjTypeHelper.GetThumb(thumbUrl)).IsEqualTo("/thumb2.png");
        await Assert.That(ObjTypeHelper.GetThumb(plain)).IsNull();
    }
}
