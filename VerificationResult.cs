// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace DouglasDwyer.JitIlVerification;

internal class VerificationResult
{
    public VerifierError Code { get; internal set; }
    public RuntimeTypeHandle Type { get; internal set; }
    public RuntimeMethodHandle Method { get; internal set; }
    public string Message { get; internal set; }
    public object[] Args { get; internal set; }
    public ErrorArgument[] ErrorArguments { get; set; }

    public T GetArgumentValue<T>(string name)
    {
        TryGetArgumentValue<T>(name, out T argumentValue);
        return argumentValue;
    }

    public bool TryGetArgumentValue<T>(string name, out T argumentValue)
    {
        argumentValue = default;

        for (int i = 0; i < ErrorArguments.Length; i++)
        {
            if (ErrorArguments[i].Name == name)
            {
                argumentValue = (T)ErrorArguments[i].Value;
                return true;
            }
        }

        return false;
    }
}

internal class ErrorArgument
{
    public ErrorArgument() { }

    public ErrorArgument(string name, object value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public object Value { get; }

    public override string ToString()
    {
        return $"{Name}: {Value}";
    }
}
