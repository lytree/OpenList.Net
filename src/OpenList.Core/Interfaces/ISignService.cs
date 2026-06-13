namespace OpenList.Core.Interfaces;

public interface ISignService
{
    string Sign(string path);
    string SignArchive(string path);
    bool Verify(string sign, string path);
    bool VerifyArchive(string sign, string path);
}
