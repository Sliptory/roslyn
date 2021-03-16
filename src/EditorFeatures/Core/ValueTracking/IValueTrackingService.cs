﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;

namespace Microsoft.CodeAnalysis.ValueTracking
{
    internal interface IValueTrackingService : IWorkspaceService
    {
        Task<ImmutableArray<ValueTrackedItem>> TrackValueSourceAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken);
        Task TrackValueSourceAsync(Solution solution, ISymbol symbol, ValueTrackingProgressCollector progressCollector, CancellationToken cancellationToken);

        Task<ImmutableArray<ValueTrackedItem>> TrackValueSourceAsync(Solution solution, ValueTrackedItem previousTrackedItem, CancellationToken cancellationToken);
        Task TrackValueSourceAsync(Solution solution, ValueTrackedItem previousTrackedItem, ValueTrackingProgressCollector progressCollector, CancellationToken cancellationToken);
    }
}
