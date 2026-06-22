namespace OpenList.Core.Interfaces;

/// <summary>
/// Sign service: generates and verifies signed URLs for /d/, /p/, /ad/, /ap/, /ae/ routes.
/// Mirrors OpenList Go <c>server/common/sign.go</c>.
/// </summary>
public interface ISignService
{
    string Sign(string path, long? expiration = null);
    bool Verify(string path, string sign);
}
