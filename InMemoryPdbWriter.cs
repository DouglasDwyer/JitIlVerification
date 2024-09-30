using Mono.Cecil.Cil;
using System.IO;
using System.IO.Compression;

namespace Dwyer.JitIlVerification;

internal sealed class InMemoryPdbWriter : ISymbolWriter
{
    private readonly Stream _stream;
    private readonly PortablePdbWriter _writer;

    internal InMemoryPdbWriter(Stream stream, PortablePdbWriter writer)
    {
        _stream = stream;
        _writer = writer;
    }

    public ISymbolReaderProvider GetReaderProvider()
    {
        return new EmbeddedPortablePdbReaderProvider();
    }

    public void Write(MethodDebugInformation info)
    {
        _writer.Write(info);
    }

    public ImageDebugHeader GetDebugHeader()
    {
        ImageDebugHeader pdbDebugHeader = _writer.GetDebugHeader();

        var directory = new ImageDebugDirectory
        {
            Type = ImageDebugType.EmbeddedPortablePdb,
            MajorVersion = 0x0100,
            MinorVersion = 0x0100,
        };

        var data = new MemoryStream();

        var w = new BinaryWriter(data);
        w.Write((byte)0x4d);
        w.Write((byte)0x50);
        w.Write((byte)0x44);
        w.Write((byte)0x42);
        w.Write((int)_stream.Length);

        _stream.Position = 0;

        using (var compress_stream = new DeflateStream(data, CompressionMode.Compress, leaveOpen: true))
            _stream.CopyTo(compress_stream);

        directory.SizeOfData = (int)data.Length;

        var debugHeaderEntries = new ImageDebugHeaderEntry[pdbDebugHeader.Entries.Length + 1];
        for (int i = 0; i < pdbDebugHeader.Entries.Length; i++)
            debugHeaderEntries[i] = pdbDebugHeader.Entries[i];
        debugHeaderEntries[debugHeaderEntries.Length - 1] = new ImageDebugHeaderEntry(directory, data.ToArray());

        return new ImageDebugHeader(debugHeaderEntries);
    }

    public void Write()
    {
        _writer.Write();
    }

    public void Dispose()
    {
        _writer.Dispose();
    }
}