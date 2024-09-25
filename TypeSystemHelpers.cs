// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Internal.TypeSystem;
using System;

namespace AssemblyILVerification;

internal static class TypeSystemHelpers
{
    /// <summary>
    /// Returns the "reduced type" based on the definition in the ECMA-335 standard (I.8.7).
    /// </summary>
    internal static Type GetReducedType(this Type type)
    {
        if (type == null)
            return null;

        var category = type.UnderlyingType.Category;

        switch (category)
        {
            case TypeFlags.Byte:
                return type.Context.GetWellKnownType(WellKnownType.SByte);
            case TypeFlags.UInt16:
                return type.Context.GetWellKnownType(WellKnownType.Int16);
            case TypeFlags.UInt32:
                return type.Context.GetWellKnownType(WellKnownType.Int32);
            case TypeFlags.UInt64:
                return type.Context.GetWellKnownType(WellKnownType.Int64);
            case TypeFlags.UIntPtr:
                return type.Context.GetWellKnownType(WellKnownType.IntPtr);

            default:
                return type.UnderlyingType; //Reduced type is type itself
        }
    }

    /// <summary>
    /// Returns the "verification type" based on the definition in the ECMA-335 standard (I.8.7).
    /// </summary>
    internal static Type GetVerificationType(this Type type)
    {
        if (type == null)
            return null;

        if (type.IsByRef)
        {
            var parameterVerificationType = GetVerificationType(type.GetElementType());
            return parameterVerificationType.MakeByRefType();
        }
        else
        {
            var reducedType = GetReducedType(type);
            switch (Type.GetTypeCode(reducedType))
            {
                case TypeCode.Boolean:
                    return typeof(sbyte);

                case TypeCode.Char:
                    return typeof(short);

                default:
                    return reducedType; // Verification type is reduced type
            }
        }
    }

    /// <summary>
    /// Returns the "intermediate type" based on the definition in the ECMA-335 standard (I.8.7).
    /// </summary>
    internal static Type GetIntermediateType(this Type type)
    {
        var verificationType = GetVerificationType(type);

        if (verificationType == null)
            return null;

        switch (Type.GetTypeCode(verificationType))
        {
            case TypeCode.SByte:
            case TypeCode.Int16:
            case TypeCode.Int32:
                return typeof(int);
            case TypeCode.Single:
            case TypeCode.Double:
                return typeof(double);
            default:
                return verificationType;
        }
    }
}