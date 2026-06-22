using OpenList.Core.Models;

namespace OpenList.Tests;

/// <summary>
/// Smoke test verifying the skeleton compiles and basic types are wired correctly.
/// Real tests will be added per-module in Phase 2+.
/// </summary>
public class SmokeTests
{
    [Test]
    public async Task Resp_Ok_Returns200()
    {
        var resp = Resp<string>.Ok("hello");
        await Assert.That(resp.Code).IsEqualTo(200);
        await Assert.That(resp.Data).IsEqualTo("hello");
    }

    [Test]
    public async Task Resp_Fail_ReturnsErrorCode()
    {
        var resp = Resp<string>.Fail("boom", 500);
        await Assert.That(resp.Code).IsEqualTo(500);
        await Assert.That(resp.Message).IsEqualTo("boom");
    }
}
