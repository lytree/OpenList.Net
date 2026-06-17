using OpenList.Core.Models;

namespace OpenList.Tests;

public class ObjTypeHelperTests
{
    [Test]
    public async Task GetObjType_VideoExtensions_ShouldReturnVideo()
    {
        await Assert.That(ObjTypeHelper.GetObjType("movie.mp4", false)).IsEqualTo((int)ObjType.Video);
        await Assert.That(ObjTypeHelper.GetObjType("clip.mkv", false)).IsEqualTo((int)ObjType.Video);
        await Assert.That(ObjTypeHelper.GetObjType("video.avi", false)).IsEqualTo((int)ObjType.Video);
        await Assert.That(ObjTypeHelper.GetObjType("film.mov", false)).IsEqualTo((int)ObjType.Video);
        await Assert.That(ObjTypeHelper.GetObjType("stream.webm", false)).IsEqualTo((int)ObjType.Video);
    }

    [Test]
    public async Task GetObjType_AudioExtensions_ShouldReturnAudio()
    {
        await Assert.That(ObjTypeHelper.GetObjType("song.mp3", false)).IsEqualTo((int)ObjType.Audio);
        await Assert.That(ObjTypeHelper.GetObjType("track.flac", false)).IsEqualTo((int)ObjType.Audio);
        await Assert.That(ObjTypeHelper.GetObjType("music.wav", false)).IsEqualTo((int)ObjType.Audio);
        await Assert.That(ObjTypeHelper.GetObjType("audio.aac", false)).IsEqualTo((int)ObjType.Audio);
    }

    [Test]
    public async Task GetObjType_ImageExtensions_ShouldReturnImage()
    {
        await Assert.That(ObjTypeHelper.GetObjType("photo.jpg", false)).IsEqualTo((int)ObjType.Image);
        await Assert.That(ObjTypeHelper.GetObjType("pic.png", false)).IsEqualTo((int)ObjType.Image);
        await Assert.That(ObjTypeHelper.GetObjType("image.gif", false)).IsEqualTo((int)ObjType.Image);
        await Assert.That(ObjTypeHelper.GetObjType("avatar.webp", false)).IsEqualTo((int)ObjType.Image);
    }

    [Test]
    public async Task GetObjType_TextExtensions_ShouldReturnText()
    {
        await Assert.That(ObjTypeHelper.GetObjType("readme.md", false)).IsEqualTo((int)ObjType.Text);
        await Assert.That(ObjTypeHelper.GetObjType("data.json", false)).IsEqualTo((int)ObjType.Text);
        await Assert.That(ObjTypeHelper.GetObjType("script.py", false)).IsEqualTo((int)ObjType.Text);
        await Assert.That(ObjTypeHelper.GetObjType("code.cs", false)).IsEqualTo((int)ObjType.Text);
    }

    [Test]
    public async Task GetObjType_UnknownExtension_ShouldReturnUnknown()
    {
        await Assert.That(ObjTypeHelper.GetObjType("file.xyz", false)).IsEqualTo((int)ObjType.Unknown);
        await Assert.That(ObjTypeHelper.GetObjType("archive.zip", false)).IsEqualTo((int)ObjType.Unknown);
        await Assert.That(ObjTypeHelper.GetObjType("binary.exe", false)).IsEqualTo((int)ObjType.Unknown);
    }

    [Test]
    public async Task GetObjType_Directory_ShouldAlwaysReturnFolder()
    {
        await Assert.That(ObjTypeHelper.GetObjType("mydir", true)).IsEqualTo((int)ObjType.Folder);
        await Assert.That(ObjTypeHelper.GetObjType("video.mp4", true)).IsEqualTo((int)ObjType.Folder);
    }

    [Test]
    public async Task GetObjType_CaseInsensitive_ShouldWork()
    {
        await Assert.That(ObjTypeHelper.GetObjType("FILE.MP4", false)).IsEqualTo((int)ObjType.Video);
        await Assert.That(ObjTypeHelper.GetObjType("Photo.JPG", false)).IsEqualTo((int)ObjType.Image);
        await Assert.That(ObjTypeHelper.GetObjType("README.MD", false)).IsEqualTo((int)ObjType.Text);
    }

    [Test]
    public async Task GetObjType_NoExtension_ShouldReturnUnknown()
    {
        await Assert.That(ObjTypeHelper.GetObjType("noext", false)).IsEqualTo((int)ObjType.Unknown);
    }
}
