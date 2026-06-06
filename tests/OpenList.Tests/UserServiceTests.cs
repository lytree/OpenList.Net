using OpenList.Infrastructure.Services;

namespace OpenList.Tests;

public class UserServiceTests
{
    [Test]
    public async Task StaticHash_ShouldReturnConsistentResult()
    {
        var hash1 = UserService.StaticHash("test");
        var hash2 = UserService.StaticHash("test");
        await Assert.That(hash1).IsEqualTo(hash2);
    }

    [Test]
    public async Task StaticHash_ShouldReturnDifferentResultsForDifferentInputs()
    {
        var hash1 = UserService.StaticHash("password1");
        var hash2 = UserService.StaticHash("password2");
        await Assert.That(hash1).IsNotEqualTo(hash2);
    }

    [Test]
    public async Task StaticHash_ShouldReturn64CharHexString()
    {
        var hash = UserService.StaticHash("test");
        await Assert.That(hash).HasLength().EqualTo(64);
        // Verify all characters are hex
        foreach (var c in hash)
        {
            await Assert.That(char.IsAsciiHexDigit(c)).IsTrue();
        }
    }

    [Test]
    public async Task HashPwd_ShouldReturnConsistentResult()
    {
        var staticHash = UserService.StaticHash("test");
        var salt = "abc123";
        var hash1 = UserService.HashPwd(staticHash, salt);
        var hash2 = UserService.HashPwd(staticHash, salt);
        await Assert.That(hash1).IsEqualTo(hash2);
    }

    [Test]
    public async Task HashPwd_DifferentSaltsShouldProduceDifferentHashes()
    {
        var staticHash = UserService.StaticHash("test");
        var hash1 = UserService.HashPwd(staticHash, "salt1");
        var hash2 = UserService.HashPwd(staticHash, "salt2");
        await Assert.That(hash1).IsNotEqualTo(hash2);
    }

    [Test]
    public async Task TwoHashPwd_ShouldMatchManualTwoStepHash()
    {
        var password = "mypassword";
        var salt = "testsalt";
        var twoHash = UserService.TwoHashPwd(password, salt);
        var manualHash = UserService.HashPwd(UserService.StaticHash(password), salt);
        await Assert.That(twoHash).IsEqualTo(manualHash);
    }
}
