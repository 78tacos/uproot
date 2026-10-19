using System.IO;
using System.IO.Compression;
using Uproot.Core;

namespace Uproot.Setup;

/// <summary>
/// The release setup carries: Uproot.exe and uproot.com, as the framework-dependent zip
/// publish.ps1 makes. Read straight out of the zip into Program Files, never unpacked anywhere
/// else first.
/// </summary>
internal sealed class Payload : IDisposable
{
    private const string ResourceName = "Uproot.Setup.payload.zip";

    private readonly Stream _stream;
    private readonly ZipArchive _zip;

    private Payload(Stream stream)
    {
        _stream = stream;
        _zip = new ZipArchive(stream, ZipArchiveMode.Read);
    }

    /// <summary>Opens the release this setup carries.</summary>
    /// <exception cref="InvalidDataException">The setup carries no release, or an incomplete one.</exception>
    public static Payload Open()
    {
        var stream = typeof(Payload).Assembly.GetManifestResourceStream(ResourceName)
                     ?? throw new InvalidDataException("This setup was built without Uproot inside it. Build it with publish.ps1.");

        var payload = new Payload(stream);

        foreach (var name in new[] { Installation.AppFile, Installation.CliFile })
        {
            if (payload._zip.GetEntry(name) is null)
            {
                payload.Dispose();
                throw new InvalidDataException($"This setup's copy of Uproot has no {name}.");
            }
        }

        return payload;
    }

    /// <summary>The files to install, read from the zip as the install copies them.</summary>
    public IReadOnlyList<InstallSource> Files =>
    [
        new(Installation.AppFile, () => _zip.GetEntry(Installation.AppFile)!.Open()),
        new(Installation.CliFile, () => _zip.GetEntry(Installation.CliFile)!.Open())
    ];

    public void Dispose()
    {
        _zip.Dispose();
        _stream.Dispose();
    }
}
