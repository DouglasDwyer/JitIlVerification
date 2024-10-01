using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;

namespace DouglasDwyer.JitIlVerification;

internal sealed class InMemoryPdbWriterProvider : ISymbolWriterProvider
{
    public ISymbolWriter GetSymbolWriter(ModuleDefinition module, string fileName)
    {
        throw new NotSupportedException();
    }

    public ISymbolWriter GetSymbolWriter(ModuleDefinition module, Stream symbolStream)
    {
        var pdb_writer = (PortablePdbWriter)new PortablePdbWriterProvider().GetSymbolWriter(module, symbolStream);
        return new InMemoryPdbWriter(symbolStream, pdb_writer);
    }
}