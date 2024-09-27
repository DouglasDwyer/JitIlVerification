namespace JitIlVerification;

//
// This duplicates types from System.Reflection.Metadata to avoid layering issues, and
// because of the System.Reflection.Metadata constructors are not public anyway.
//

public enum ILExceptionRegionKind
{
    Catch = 0,
    Filter = 1,
    Finally = 2,
    Fault = 4,
}