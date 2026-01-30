// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This is a polyfill for the IsExternalInit type which is required for init-only properties
// and records in C# 9.0+ when targeting .NET Standard 2.0.

// ReSharper disable once CheckNamespace
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
