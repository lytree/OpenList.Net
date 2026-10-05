using OpenList.Core.Drivers;
using OpenList.Core.Drivers.Base;
using OpenList.Drivers.FtpSftpSmb;
using OpenList.Drivers.S3;
using OpenList.Drivers.WebDAV;
using OpenList.Vfs;

namespace OpenList.Tests;

/// <summary>
/// Tests for Phase 8 driver configuration schemas and metadata.
/// These tests verify driver discovery, config validation, and capability
/// declarations without requiring live storage servers.
/// </summary>
public class DriverTests
{
    [Test]
    public async Task S3Driver_DeclaresCorrectMetadata()
    {
        var driver = new S3Driver();
        await Assert.That(driver.DriverName).IsEqualTo("S3");
        await Assert.That(driver.ReadOnly).IsFalse();
        await Assert.That(driver.IsCloudDriver).IsFalse();
        await Assert.That(driver).IsAssignableTo<IFileSystemUploader>();
        await Assert.That(driver).IsAssignableTo<IFileSystemLinkProvider>();
    }

    [Test]
    public async Task S3Driver_ConfigSchema_HasRequiredFields()
    {
        var driver = new S3Driver();
        var names = driver.ConfigItems.Select(c => c.Name).ToList();
        await Assert.That(names).Contains("endpoint");
        await Assert.That(names).Contains("access_key_id");
        await Assert.That(names).Contains("secret_access_key");
        await Assert.That(names).Contains("bucket");

        var endpoint = driver.ConfigItems.First(c => c.Name == "endpoint");
        await Assert.That(endpoint.Required).IsTrue();
    }

    [Test]
    public async Task S3Driver_InitAsync_RejectsMissingEndpoint()
    {
        var driver = new S3Driver();
        var config = new DriverConfig { Addition = "{}" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task S3Driver_InitAsync_RejectsMissingBucket()
    {
        var driver = new S3Driver();
        var config = new DriverConfig
        {
            Addition = """{"endpoint":"https://s3.amazonaws.com","access_key_id":"x","secret_access_key":"y"}"""
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task WebDavDriver_DeclaresCorrectMetadata()
    {
        var driver = new WebDavDriver();
        await Assert.That(driver.DriverName).IsEqualTo("WebDAV");
        await Assert.That(driver.ReadOnly).IsFalse();
        await Assert.That(driver).IsAssignableTo<IFileSystemUploader>();
        await Assert.That(driver).IsAssignableTo<IFileSystemLinkProvider>();
    }

    [Test]
    public async Task WebDavDriver_ConfigSchema_HasRequiredFields()
    {
        var driver = new WebDavDriver();
        var names = driver.ConfigItems.Select(c => c.Name).ToList();
        await Assert.That(names).Contains("endpoint");
        var endpoint = driver.ConfigItems.First(c => c.Name == "endpoint");
        await Assert.That(endpoint.Required).IsTrue();
    }

    [Test]
    public async Task WebDavDriver_InitAsync_RejectsMissingEndpoint()
    {
        var driver = new WebDavDriver();
        var config = new DriverConfig { Addition = "{}" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task WebDavDriver_GetLink_ReturnsDirectUrl()
    {
        var driver = new WebDavDriver();
        await driver.InitAsync(new DriverConfig
        {
            Addition = """{"endpoint":"https://dav.example.com","root_folder_path":"/photos"}"""
        });

        var link = await driver.GetLinkAsync("/photos/cat.jpg");
        await Assert.That(link).IsNotNull();
        await Assert.That(link!.ToString()).Contains("dav.example.com");
        await Assert.That(link.ToString()).Contains("cat.jpg");
    }

    [Test]
    public async Task FtpDriver_DeclaresCorrectMetadata()
    {
        var driver = new FtpDriver();
        await Assert.That(driver.DriverName).IsEqualTo("FTP");
        await Assert.That(driver.ReadOnly).IsFalse();
        await Assert.That(driver).IsAssignableTo<IFileSystemUploader>();
    }

    [Test]
    public async Task FtpDriver_ConfigSchema_HasRequiredFields()
    {
        var driver = new FtpDriver();
        var names = driver.ConfigItems.Select(c => c.Name).ToList();
        await Assert.That(names).Contains("host");
        var host = driver.ConfigItems.First(c => c.Name == "host");
        await Assert.That(host.Required).IsTrue();
    }

    [Test]
    public async Task FtpDriver_InitAsync_RejectsMissingHost()
    {
        var driver = new FtpDriver();
        var config = new DriverConfig { Addition = "{}" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task SftpDriver_DeclaresCorrectMetadata()
    {
        var driver = new SftpDriver();
        await Assert.That(driver.DriverName).IsEqualTo("SFTP");
        await Assert.That(driver.ReadOnly).IsFalse();
        await Assert.That(driver).IsAssignableTo<IFileSystemUploader>();
    }

    [Test]
    public async Task SftpDriver_ConfigSchema_HasRequiredFields()
    {
        var driver = new SftpDriver();
        var names = driver.ConfigItems.Select(c => c.Name).ToList();
        await Assert.That(names).Contains("host");
        await Assert.That(names).Contains("username");
        await Assert.That(names).Contains("private_key");
    }

    [Test]
    public async Task SftpDriver_InitAsync_RejectsMissingHost()
    {
        var driver = new SftpDriver();
        var config = new DriverConfig { Addition = "{}" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task SftpDriver_InitAsync_RejectsMissingUsername()
    {
        var driver = new SftpDriver();
        var config = new DriverConfig { Addition = """{"host":"sftp.example.com"}""" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task SmbDriver_DeclaresCorrectMetadata()
    {
        var driver = new SmbDriver();
        await Assert.That(driver.DriverName).IsEqualTo("SMB");
        await Assert.That(driver.ReadOnly).IsTrue();
    }

    [Test]
    public async Task SmbDriver_ConfigSchema_HasRequiredFields()
    {
        var driver = new SmbDriver();
        var names = driver.ConfigItems.Select(c => c.Name).ToList();
        await Assert.That(names).Contains("host");
        await Assert.That(names).Contains("share");
        var share = driver.ConfigItems.First(c => c.Name == "share");
        await Assert.That(share.Required).IsTrue();
    }

    [Test]
    public async Task SmbDriver_InitAsync_RejectsMissingHost()
    {
        var driver = new SmbDriver();
        var config = new DriverConfig { Addition = "{}" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task SmbDriver_InitAsync_RejectsMissingShare()
    {
        var driver = new SmbDriver();
        var config = new DriverConfig { Addition = """{"host":"smb.example.com"}""" };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await driver.InitAsync(config));
    }

    [Test]
    public async Task SmbDriver_InitAsync_WithValidConfig_CreatesSystem()
    {
        var driver = new SmbDriver();
        var config = new DriverConfig
        {
            Addition = """{"host":"smb.example.com","share":"shared","username":"u","password":"p"}"""
        };
        await driver.InitAsync(config);
        await Assert.That(driver.System).IsNotNull();
    }
}
