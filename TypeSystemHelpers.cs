// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace JitIlVerification;

internal static class TypeSystemHelpers
{
    /// <summary>
    /// Returns the "reduced type" based on the definition in the ECMA-335 standard (I.8.7).
    /// </summary>
    internal static Type GetReducedType(this Type type)
    {
        if (type == null)
            return null;

        var underlying = type.IsEnum ? type.GetEnumUnderlyingType() : type;

        switch(Type.GetTypeCode(type))
        {
            case TypeCode.Byte:
                return typeof(sbyte);
            case TypeCode.UInt16:
                return typeof(sbyte);
            case TypeCode.UInt32:
                return typeof(sbyte);
            case TypeCode.UInt64:
                return typeof(sbyte);
            default:
                return underlying;
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