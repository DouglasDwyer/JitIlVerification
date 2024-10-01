#nullable enable

using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace Dwyer.JitIlVerification;

/// <summary>
/// Provides scoped assembly loading with the same semantics as <see cref="AssemblyLoadContext"/>.
/// Any assemblies loaded with this context will be checked for type/memory safety issues. Any unverifiable methods with throw
/// an exception upon invocation.
/// </summary>
public class VerifiableAssemblyLoader
{
    /// <summary>
    /// The internal array field that Cecil collections use, for efficient access.
    /// </summary>
    private static FieldInfo CecilCollectionItems { get; } = typeof(Collection<Instruction>).GetField("items", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// The internal size field that Cecil collections use, for efficient access.
    /// </summary>
    private static FieldInfo CecilCollectionSize { get; } = typeof(Collection<Instruction>).GetField("size", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Gets a value that indicates whether this <see cref="VerifiableAssemblyLoader"/> is collectible.
    /// </summary>
    public bool IsCollectible => _loader.IsCollectible;

    /// <summary>
    /// Get the name of the <see cref="VerifiableAssemblyLoader"/>.
    /// </summary>
    public string? Name => _loader.Name;

    /// <summary>
    /// Occurs when the resolution of an assembly fails when attempting to load into this assembly load context.
    /// </summary>
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

    /// <summary>
    /// Occurs when the resolution of a native library fails.
    /// </summary>
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

    /// <summary>
    /// Occurs when the <see cref="VerifiableAssemblyLoader"/> is unloaded.
    /// </summary>
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

    /// <summary>
    /// The inner object for scoped assembly loading.
    /// </summary>
    private readonly VerifiableAssemblyLoadContext _loader;

    /// <summary>
    /// Initializes a new instance of the <see cref="VerifiableAssemblyLoader"/> class.
    /// </summary>
    public VerifiableAssemblyLoader()
    {
        _loader = new VerifiableAssemblyLoadContext(this);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VerifiableAssemblyLoader"/> class
    /// with a value that indicates whether unloading is enabled.
    /// </summary>
    /// <param name="isCollectible">Whether this context should be able to unload.</param>
    public VerifiableAssemblyLoader(bool isCollectible)
    {
        _loader = new VerifiableAssemblyLoadContext(this, isCollectible);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="VerifiableAssemblyLoader"/> class
    /// with a name and a value that indicates whether unloading is enabled.
    /// </summary>
    /// <param name="name">The display name of the load context.</param>
    /// <param name="isCollectible">Whether this context should be able to unload.</param>
    public VerifiableAssemblyLoader(string name, bool isCollectible)
    {
        _loader = new VerifiableAssemblyLoadContext(this, name, isCollectible);
    }

    /// <summary>
    /// Invokes the CIL verifier for the given loaded method.
    /// </summary>
    /// <param name="handle">The method to verify.</param>
    /// <exception cref="BadImageFormatException">If the method was unverifiable.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Verify(RuntimeTypeHandle type, RuntimeMethodHandle handle)
    {
        var methodBase = MethodBase.GetMethodFromHandle(handle, type);
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

    /// <summary>
    /// Resolves and loads an assembly given its <see cref="AssemblyName">AssemblyName</see>.
    /// </summary>
    /// <param name="assemblyName">The object that describes the assembly to load.</param>
    /// <returns>The resolved assembly, or <c>null</c>.</returns>
    public Assembly LoadFromAssemblyName(AssemblyName assemblyName)
    {
        return _loader.LoadFromAssemblyName(assemblyName);
    }

    /// <summary>
    /// When overridden in a derived class, allows an assembly to be resolved based on its <see cref="AssemblyName">AssemblyName</see>.
    /// </summary>
    /// <param name="assemblyName">The object that describes the assembly to be resolved.</param>
    /// <returns>The resolved assembly, or <c>null</c>.</returns>
    protected virtual Assembly? Load(AssemblyName assemblyName)
    {
        var executingAssy = Assembly.GetExecutingAssembly();
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, executingAssy.GetName()))
        {
            return executingAssy;
        }

        return null;
    }

    /// <summary>
    /// Loads the assembly with a common object file format (COFF)-based image containing a managed assembly.
    /// </summary>
    /// <param name="assembly">A byte array that is a COFF-based image containing a managed assembly.</param>
    /// <returns>The loaded assembly.</returns>
    public Assembly LoadFromStream(Stream assembly)
    {
        return LoadFromStream(assembly, null);
    }

    /// <summary>
    /// Loads the assembly with a common object file format (COFF)-based image containing a managed assembly, optionally including symbols for the assembly.
    /// </summary>
    /// <param name="assembly">A byte array that is a COFF-based image containing a managed assembly.</param>
    /// <param name="assemblySymbols">A byte array that contains the raw bytes representing the symbols for the assembly.</param>
    /// <returns>The loaded assembly.</returns>
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

        return _loader.LoadFromStream(output);
    }

    /// <summary>
    /// Loads the contents of an assembly file on the specified path.
    /// </summary>
    /// <param name="assemblyPath">The fully qualified path of the file to load.</param>
    /// <returns>The loaded assembly.</returns>
    public Assembly LoadFromAssemblyPath(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        return LoadFromStream(stream);
    }

    /// <summary>
    /// Sets the root path where the optimization profiles for this load context are stored.
    /// </summary>
    /// <param name="directoryPath">The full path to the directory where the optimization profiles are stored.</param>
    public void SetProfileOptimizationRoot(string directoryPath)
    {
        _loader.SetProfileOptimizationRoot(directoryPath);
    }

    /// <summary>
    /// Starts the profile optimization for the specified profile.
    /// </summary>
    /// <param name="profile">The name of the optimization profile.</param>
    public void StartProfileOptimization(string? profile)
    {
        _loader.StartProfileOptimization(profile);
    }

    /// <summary>
    /// Initiates an unload of this <see cref="VerifiableAssemblyLoader"/>.
    /// </summary>
    public void Unload()
    {
        _loader.Unload();
    }

    /// <summary>
    /// Adds verification guards to all methods in the assembly. The first time that
    /// any method is invoked, it will call the verifier on itself.
    /// </summary>
    /// <param name="assembly">The assembly to instrument.</param>
    protected virtual void InstrumentAssembly(AssemblyDefinition assembly)
    {
        var methods = GetAllMethods(assembly).ToArray();
        for (int id = 0; id < methods.Length; id++)
        {
            var method = methods[id];
            var guardField = CreateGuardField(method.Method, id, method.References);
            InsertGuardCall(method.Method, guardField);
        }
    }

    /// <summary>
    /// Allows derived class to load an unmanaged library by name.
    /// </summary>
    /// <param name="unmanagedDllName">Name of the unmanaged library. Typically this is the filename without its path or extensions.</param>
    /// <returns>A handle to the loaded library, or <see cref="IntPtr.Zero"/>.</returns>
    protected virtual IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        return IntPtr.Zero;
    }

    /// <summary>
    /// Loads an unmanaged library from the specified path.
    /// </summary>
    /// <param name="unmanagedDllPath">The path to the unmanaged library.</param>
    /// <returns>The OS handle for the loaded native library.</returns>
    protected IntPtr LoadUnmanagedDllFromPath(string unmanagedDllPath)
    {
        return _loader.LoadUnmanagedDllFromPath(unmanagedDllPath);
    }

    /// <summary>
    /// Loads the Cecil definition for the provided assembly, to allow for in-memory editing.
    /// </summary>
    /// <param name="assembly">A stream containing the assembly to load.</param>
    /// <param name="assemblySymbols">The debug symbols associated with the assembly, if any.</param>
    /// <returns>The loaded assembly definition.</returns>
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

    /// <summary>
    /// Writes the provided assembly definition (along with any symbols) to a memory stream.
    /// </summary>
    /// <param name="assyDef">The Cecil assembly definition to write.</param>
    /// <param name="target">
    /// The stream to which the assembly should be stored. The capacity of the input memory stream will
    /// be used to set the initial output buffer size, which may avoid extra allocation.
    /// </param>
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

    /// <summary>
    /// Gets a list of all methods in the assembly, along with imported type references
    /// for their respective modules.
    /// </summary>
    /// <param name="assembly">The assembly definition over which to iterate.</param>
    /// <returns>An enumerable containing methods and imported references.</returns>
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

    /// <summary>
    /// Gets all methods associated with the given type (including methods in nested types).
    /// </summary>
    /// <param name="type">The type over which to iterate.</param>
    /// <returns>All methods contained in the type.</returns>
    private static IEnumerable<MethodDefinition> GetAllMethods(TypeDefinition type)
    {
        return type.Methods
            .Concat(type.NestedTypes.SelectMany(GetAllMethods));
    }

    /// <summary>
    /// Creates a guard type with a static constructor. The static constructor will invoke verification
    /// on the given method when run. The guard type will also contain a field, which may be accessed
    /// to guarantee that the static constructor has run (or failed).
    /// </summary>
    /// <param name="method">The method that should be verified by the guard.</param>
    /// <param name="id">A unique ID to give the guard type.</param>
    /// <param name="references">Imported type references that will be used to define the guard.</param>
    /// <returns>The field to access in order to provoke verification.</returns>
    private static FieldDefinition CreateGuardField(MethodDefinition method, int id, ImportedReferences references)
    {
        var guardType = new TypeDefinition("JitIlVerification.Guard", $"{method.Name}_{id}",
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class | Mono.Cecil.TypeAttributes.AutoLayout
            | Mono.Cecil.TypeAttributes.Abstract | Mono.Cecil.TypeAttributes.Sealed, references.ObjectType);

        method.Module.Types.Add(guardType);

        var guardCctor = new MethodDefinition(".cctor", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.HideBySig
            | Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName | Mono.Cecil.MethodAttributes.Static, references.VoidType);

        var il = guardCctor.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldtoken, method.DeclaringType));
        il.Append(il.Create(OpCodes.Ldtoken, method));
        il.Append(il.Create(OpCodes.Call, references.VerifyMethod));
        il.Append(il.Create(OpCodes.Ret));

        guardType.Methods.Add(guardCctor);

        var field = new FieldDefinition(".GuardField",
            Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.InitOnly, references.BoolType);
        guardType.Fields.Add(field);

        return field;
    }

    /// <summary>
    /// Inserts a spurious field access at the beginning of the given method. The field should be a
    /// guard field, that will throw an exception if the guard's static constructor failed (thereby
    /// indicating the method was unverifiable).
    /// </summary>
    /// <param name="method">The method to modify.</param>
    /// <param name="guardField">The guard field that should be accessed.</param>
    private static void InsertGuardCall(MethodDefinition method, FieldDefinition guardField)
    {
        const int opsToAdd = 2;

        var instructions = method.Body.Instructions;
        var items = (Instruction[])CecilCollectionItems.GetValue(instructions)!;
        var size = (int)CecilCollectionSize.GetValue(instructions)!;

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

    /// <summary>
    /// Adds type references to the given module that are necessary for guard type implementations.
    /// </summary>
    /// <param name="module">The module where the types should be imported.</param>
    /// <returns>A set of type references that were imported.</returns>
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

    /// <summary>
    /// Implements the actual loading process for verifiable assemblies.
    /// </summary>
    private class VerifiableAssemblyLoadContext : AssemblyLoadContext
    {
        /// <summary>
        /// Creates a new context referencing the given parent loader.
        /// </summary>
        /// <param name="parent">The parent object.</param>
        public VerifiableAssemblyLoadContext(VerifiableAssemblyLoader parent) : base()
        {
            _parent = parent;
        }

        /// <summary>
        /// Creates a new context referencing the given parent loader.
        /// </summary>
        /// <param name="parent">The parent object.</param>
        /// <param name="isCollectible">Whether this context should be able to unload.</param>
        public VerifiableAssemblyLoadContext(VerifiableAssemblyLoader parent, bool isCollectible) : base(isCollectible)
        {
            _parent = parent;
        }

        /// <summary>
        /// Creates a new context referencing the given parent loader.
        /// </summary>
        /// <param name="parent">The parent object.</param>
        /// <param name="name">The display name of the load context.</param>
        /// <param name="isCollectible">Whether this context should be able to unload.</param>
        public VerifiableAssemblyLoadContext(VerifiableAssemblyLoader parent, string name, bool isCollectible) : base(name, isCollectible)
        {
            _parent = parent;
        }

        /// <summary>
        /// The parent loader object.
        /// </summary>
        private readonly VerifiableAssemblyLoader _parent;

        /// <summary>
        /// Allows derived class to load an unmanaged library by name.
        /// </summary>
        /// <param name="unmanagedDllName">Name of the unmanaged library. Typically this is the filename without its path or extensions.</param>
        /// <returns>A handle to the loaded library, or <see cref="IntPtr.Zero"/>.</returns>
        public new IntPtr LoadUnmanagedDllFromPath(string unmanagedDllPath)
        {
            return base.LoadUnmanagedDllFromPath(unmanagedDllPath);
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            return _parent.LoadUnmanagedDll(unmanagedDllName);
        }
    }

    /// <summary>
    /// Data necessary for adding a verifier hook to a method.
    /// </summary>
    private struct MethodToUpdate
    {
        /// <summary>
        /// The method that should be verified.
        /// </summary>
        public required MethodDefinition Method;

        /// <summary>
        /// Member references necessary for adding a verifier hook.
        /// </summary>
        public required ImportedReferences References;
    }

    /// <summary>
    /// Member references necessary for adding a verifier hook.
    /// </summary>
    private class ImportedReferences
    {
        /// <summary>
        /// The <see cref="bool"> type.
        /// </summary>
        public required TypeReference BoolType;

        /// <summary>
        /// The <see cref="object"> type.
        /// </summary>
        public required TypeReference ObjectType;

        /// <summary>
        /// The <see cref="Verify(RuntimeMethodHandle)"> method.
        /// </summary>
        public required MethodReference VerifyMethod;

        /// <summary>
        /// The <see cref="void"> type.
        /// </summary>
        public required TypeReference VoidType;
    }
}