﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.PersistentStorage;

namespace Microsoft.CodeAnalysis.Host
{
    internal interface IChecksummedPersistentStorageService : IPersistentStorageService
    {
        ValueTask<IChecksummedPersistentStorage> GetStorageAsync(SolutionKey solutionKey, bool checkBranchId, CancellationToken cancellationToken);
    }
}
