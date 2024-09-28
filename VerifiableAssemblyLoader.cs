using System.IO;
using System;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using System.Collections.Generic;
using System.Linq;

namespace JitIlVerification;

public class VerifiableAssemblyLoader
{
    private static FieldInfo CecilCollectionItems { get; } = typeof(Collection<Instruction>).GetField("items", BindingFlags.NonPublic | BindingFlags.Instance);
    private static FieldInfo CecilCollectionSize { get; } = typeof(Collection<Instruction>).GetField("size", BindingFlags.NonPublic | BindingFlags.Instance);

    public bool IsCollectible => _loader.IsCollectible;

    public string? Name => _loader.Name;

    public event Func<AssemblyLoadContext, AssemblyName, Assembly?>? Resolving
    {
        add
        {
            _loader.Resolving += value;
        }
        remove
        {
            _loader.Resolving -= value;
        }
    }

    public event Func<Assembly, string, IntPtr>? ResolvingUnmanagedDll
    {
        add
        {
            _loader.ResolvingUnmanagedDll += value;
        }
        remove
        {
            _loader.ResolvingUnmanagedDll -= value;
        }
    }

    public event Action<AssemblyLoadContext>? Unloading
    {
        add
        {
            _loader.Unloading += value;
        }
        remove
        {
            _loader.Unloading -= value;
        }
    }

    private readonly VerifiableAssemblyLoadContext _loader;

    public VerifiableAssemblyLoader()
    {
        _loader = new VerifiableAssemblyLoadContext(this);
    }

    public VerifiableAssemblyLoader(bool isCollectible)
    {
        _loader = new VerifiableAssemblyLoadContext(this, isCollectible);
    }

    public VerifiableAssemblyLoader(string name, bool isCollectible)
    {
        _loader = new VerifiableAssemblyLoadContext(this, name, isCollectible);
    }

    public static void Verify(RuntimeMethodHandle handle)
    {
        var methodBase = MethodBase.GetMethodFromHandle(handle);
        if (methodBase is null)
        {
            throw new BadImageFormatException($"Unable to find method with handle {handle} to verify.");
        }

        var importer = new ILImporter(methodBase);
        importer.SanityChecks = false;
        importer.ReportVerificationError = (args, err) => {
            if (0 == args.Length)
            {
                throw new BadImageFormatException($"Unverifiable CIL encountered: {err}");
            }
            else
            {
                throw new BadImageFormatException($"Unverifiable CIL encountered: {err} ({string.Join(", ", (object[])args)})");
            }
        };
            
        importer.Verify();
    }

    public Assembly LoadFromAssemblyName(AssemblyName assemblyName)
    {
        return _loader.LoadFromAssemblyName(assemblyName);
    }

    protected virtual Assembly? Load(AssemblyName assemblyName)
    {
        var executingAssy = Assembly.GetExecutingAssembly();
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, executingAssy.GetName()))
        {
            return executingAssy;
        }

        return null;
    }

    public Assembly LoadFromStream(Stream assembly)
    {
        return LoadFromStream(assembly, null);
    }

    public Assembly LoadFromStream(Stream assembly, Stream? assemblySymbols)
    {
        MemoryStream output;
        if (assembly is MemoryStream inputStream)
        {
            output = inputStream;
        }
        else
        {
            output = new MemoryStream();
            assembly.CopyTo(output);
            assembly = output;
            assembly.Position = 0;
        }

        var assyDef = LoadAssemblyDefinition(output, assemblySymbols);
        InstrumentAssembly(assyDef);
        StoreAssemblyDefinition(assyDef, ref output);

        File.WriteAllBytes("otherimpl.dll", output.ToArray());

        return _loader.LoadFromStream(output);
    }

    public Assembly LoadFromAssemblyPath(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        return LoadFromStream(stream);
    }

    public void SetProfileOptimizationRoot(string directoryPath)
    {
        _loader.SetProfileOptimizationRoot(directoryPath);
    }

    public void StartProfileOptimization(string? profile)
    {
        _loader.StartProfileOptimization(profile);
    }

    public void Unload()
    {
        _loader.Unload();
    }

    protected virtual void InstrumentAssembly(AssemblyDefinition assembly)
    {
        var methods = GetAllMethods(assembly).ToArray();
        var id = 0;
        foreach (var method in methods)
        {
            var guardField = CreateGuardField(method.Method, id, method.References);
            InsertGuardCall(method.Method, guardField);
            id++;
        }
    }

    protected virtual IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        return IntPtr.Zero;
    }

    protected IntPtr LoadUnmanagedDllFromPath(string unmanagedDllPath)
    {
        return _loader.LoadUnmanagedDllFromPath(unmanagedDllPath);
    }

    private static AssemblyDefinition LoadAssemblyDefinition(MemoryStream assembly, Stream? assemblySymbols)
    {
        AssemblyDefinition? assyDef = null;

        var initialPosition = assembly.Position;
        try
        {
            var readerParameters = new ReaderParameters();
            readerParameters.SymbolReaderProvider = new PortablePdbReaderProvider();
            readerParameters.SymbolStream = assemblySymbols;
            readerParameters.ReadSymbols = true;

            assyDef = AssemblyDefinition.ReadAssembly(assembly, readerParameters);
        }
        catch
        {
            // Assume that there was an error loading symbols; try again without them.
            assembly.Position = initialPosition;
            assyDef = AssemblyDefinition.ReadAssembly(assembly);
        }

        return assyDef;
    }

    private static void StoreAssemblyDefinition(AssemblyDefinition assyDef, ref MemoryStream target)
    {
        var writerParameters = new WriterParameters();
        writerParameters.SymbolWriterProvider = new InMemoryPdbWriterProvider();
        writerParameters.SymbolStream = new MemoryStream();
        writerParameters.WriteSymbols = true;

        target = new MemoryStream(target.Capacity);
        assyDef.Write(target, writerParameters);
        target.Position = 0;
    }

    private static IEnumerable<MethodToUpdate> GetAllMethods(AssemblyDefinition assembly)
    {
        return assembly.Modules
            .Select(x => (x, ImportReferences(x)))
            .SelectMany(x => x.x.Types.Select(y => (y, x.Item2)))
            .SelectMany(x => GetAllMethods(x.y).Select(y => new MethodToUpdate
            {
                Method = y,
                References = x.Item2
            }))
            .Where(x => x.Method.HasBody);
    }

    private static IEnumerable<MethodDefinition> GetAllMethods(TypeDefinition type)
    {
        return type.Methods
            .Concat(type.NestedTypes.SelectMany(GetAllMethods));
    }

    private static FieldDefinition CreateGuardField(MethodDefinition method, int id, ImportedReferences references)
    {
        var guardType = new TypeDefinition("JitIlVerification.Guard", $".MethodGuard_{id}",
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class | Mono.Cecil.TypeAttributes.AutoLayout
            | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed, references.ObjectType);
        method.Module.Types.Add(guardType);

        var guardCctor = new MethodDefinition(".cctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig
            | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName | Mono.Cecil.MethodAttributes.Static, references.VoidType);

        var il = guardCctor.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldtoken, method));
        il.Append(il.Create(OpCodes.Call, references.VerifyMethod));
        il.Append(il.Create(OpCodes.Ret));

        guardType.Methods.Add(guardCctor);

        var field = new FieldDefinition(".GuardField",
            Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.InitOnly, references.BoolType);
        guardType.Fields.Add(field);

        return field;
    }

    private static void InsertGuardCall(MethodDefinition method, FieldDefinition guardField)
    {
        const int opsToAdd = 2;

        var instructions = method.Body.Instructions;
        var items = (Instruction[])CecilCollectionItems.GetValue(instructions);
        var size = (int)CecilCollectionSize.GetValue(instructions);

        var il = method.Body.GetILProcessor();
        if (items.Length < size + opsToAdd)
        {
            var newItems = new Instruction[2 * (size + opsToAdd)];
            Array.Copy(items, 0, newItems, opsToAdd, size);
            items = newItems;
        }
        else
        {
            Array.Copy(items, 0, items, opsToAdd, size);
        }

        items[0] = il.Create(OpCodes.Ldsfld, guardField);
        items[1] = il.Create(OpCodes.Pop);

        CecilCollectionItems.SetValue(instructions, items);
        CecilCollectionSize.SetValue(instructions, size + opsToAdd);
    }

    private static ImportedReferences ImportReferences(ModuleDefinition module)
    {
        return new ImportedReferences
        {
            BoolType = module.ImportReference(typeof(bool)),
            ObjectType = module.ImportReference(typeof(object)),
            VerifyMethod = module.ImportReference(typeof(VerifiableAssemblyLoader).GetMethod(nameof(Verify))),
            VoidType = module.ImportReference(typeof(void))
        };
    }

    private class VerifiableAssemblyLoadContext : AssemblyLoadContext
    {
        public VerifiableAssemblyLoadContext(VerifiableAssemblyLoader parent) : base()
        {
            _parent = parent;
        }

        public VerifiableAssemblyLoadContext(VerifiableAssemblyLoader parent, bool isCollectible) : base(isCollectible)
        {
            _parent = parent;
        }

        public VerifiableAssemblyLoadContext(VerifiableAssemblyLoader parent, string name, bool isCollectible) : base(name, isCollectible)
        {
            _parent = parent;
        }

        private readonly VerifiableAssemblyLoader _parent;

        public new IntPtr LoadUnmanagedDllFromPath(string unmanagedDllPath)
        {
            return base.LoadUnmanagedDllFromPath(unmanagedDllPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var executingAssembly = Assembly.GetExecutingAssembly();
            if (AssemblyName.ReferenceMatchesDefinition(assemblyName, executingAssembly.GetName()))
            {
                return executingAssembly;
            }
            else
            {
                return _parent.Load(assemblyName);
            }
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            return _parent.LoadUnmanagedDll(unmanagedDllName);
        }
    }

    private struct MethodToUpdate
    {
        public MethodDefinition Method;
        public ImportedReferences References;
    }

    private class ImportedReferences
    {
        public TypeReference BoolType;
        public TypeReference ObjectType;
        public MethodReference VerifyMethod;
        public TypeReference VoidType;
    }
}