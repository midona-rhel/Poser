using System.IO;

namespace Poser.Documents.Files;

/// <summary>File operations behind the atomic same-directory stores; tests
/// substitute it to inject failures.</summary>
internal interface IAtomicFileSystem
{
    Stream OpenRead(string path);
    Stream CreateNew(string path);
    void FlushToDisk(Stream stream);
    bool Exists(string path);
    void Replace(string source, string destination, string backup);
    void Move(string source, string destination);
    void Delete(string path);
}

internal sealed class SystemAtomicFileSystem : IAtomicFileSystem
{
    public Stream OpenRead(string path) => new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4096,
        FileOptions.SequentialScan);

    public Stream CreateNew(string path) => new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 4096,
        FileOptions.SequentialScan);

    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);
    public bool Exists(string path) => File.Exists(path);
    public void Replace(string source, string destination, string backup) =>
        File.Replace(source, destination, backup);
    public void Move(string source, string destination) => File.Move(source, destination);
    public void Delete(string path) => File.Delete(path);
}
