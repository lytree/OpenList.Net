using OpenList.Core.Drivers;

namespace OpenList.Tests;

public class DriverConfigTests
{
    [Test]
    public async Task DriverConfig_DefaultValues_ShouldBeCorrect()
    {
        var config = new DriverConfig();
        await Assert.That(config.Name).IsEmpty();
        await Assert.That(config.DefaultRoot).IsEmpty();
        await Assert.That(config.LocalSort).IsFalse();
        await Assert.That(config.OnlyProxy).IsFalse();
        await Assert.That(config.NoCache).IsFalse();
    }

    [Test]
    public async Task DriverItem_ShouldStoreValues()
    {
        var item = new DriverItem
        {
            Name = "host",
            Type = "string",
            Default = "localhost",
            Help = "Server host",
            Required = true
        };
        await Assert.That(item.Name).IsEqualTo("host");
        await Assert.That(item.Type).IsEqualTo("string");
        await Assert.That(item.Default).IsEqualTo("localhost");
        await Assert.That(item.Help).IsEqualTo("Server host");
        await Assert.That(item.Required).IsTrue();
    }

    [Test]
    public async Task DriverInfo_ShouldContainConfigAndItems()
    {
        var info = new DriverInfo();
        await Assert.That(info.Common).IsNotNull().And.IsEmpty();
        await Assert.That(info.Additional).IsNotNull().And.IsEmpty();
        await Assert.That(info.Config).IsNotNull();
    }
}
