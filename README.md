# JitIlVerification

This project implements the verification of .NET assemblies at runtime. Verified assemblies are guaranteed to have valid Common Intermediate Language (CIL) bytecode, and cannot directly cause memory unsafety or undefined behavior. 

This project is a fork of the [Microsoft.ILVerification](https://github.com/dotnet/runtime/tree/main/src/coreclr/tools/ILVerify) library, which verifies assemblies by loading them (and all of their dependencies) from disk. The original library functions mainly as a compile-time static analysis tool. It is unsuited for verification of assemblies that a deployed application is loading, because the system libraries or other dependencies may not be known or available on disk. The main contribution of `JitIlVerification` is to integrate Microsoft's verification library with the C# runtime type system, so that assembly validation can occur at runtime.

### Why use this

The original .NET runtime for Windows came with CIL verification. Whenever an assembly was loaded, if the assembly had partial/low trust (because it was loaded from an untrusted source, like the web) the runtime would verify the assembly to ensure that its CIL was valid. In .NET Core, however, [this functionality has been removed](https://github.com/dotnet/runtime/issues/32648). The .NET Core runtime will accept and load invalid or unsafe CIL. This makes it impossible to sandbox C# assemblies or load code from an untrusted source, since that code could have undefined behavior. This library re-adds runtime CIL verification.

### How to use this

`JitIlVerification` defines a single public type - the `VerifiableAssemblyLoader`. This is a drop-in replacement for a [`System.Runtime.AssemblyLoadContext`](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.loader.assemblyloadcontext?view=net-8.0), but any assemblies loaded with the `VerifiableAssemblyLoader` will be checked for invalid CIL. If an invalid method from the assembly is called, an exception will immediately be thrown.

### How it works

- Whenever an assembly is loaded with `VerifiableAssemblyLoader`, the assembly bytecode is modified using `Mono.Cecil`. Guard instructions are inserted at the beginning of every CIL method.
- The assembly is loaded normally by the .NET Core runtime.
- When one of the guard instructions is hit for the first time, it passes the declaring method handle to the `ILVerification` algorithm. The algorithm loads the method bytecode using reflection and verifies it using the runtime type system.
- If the method was verifiable, then it will run successfully. Otherwise, any attempt to call the method will throw an exception.