﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes.Suppression;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.ErrorLogger;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Telemetry;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeFixes
{
    using DiagnosticId = String;
    using LanguageKind = String;

    [Export(typeof(ICodeFixService)), Shared]
    internal partial class CodeFixService : ICodeFixService
    {
        private static readonly Comparison<DiagnosticData> s_diagnosticDataComparisonById =
            new((d1, d2) => DiagnosticId.CompareOrdinal(d1.Id, d2.Id));

        private readonly IDiagnosticAnalyzerService _diagnosticService;
        private readonly ImmutableArray<Lazy<CodeFixProvider, CodeChangeProviderMetadata>> _fixers;
        private readonly ImmutableDictionary<string, ImmutableArray<Lazy<CodeFixProvider, CodeChangeProviderMetadata>>> _fixersPerLanguageMap;

        private readonly ConditionalWeakTable<IReadOnlyList<AnalyzerReference>, ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>> _projectFixersMap = new();

        // Shared by project fixers and workspace fixers.
        private readonly ConditionalWeakTable<AnalyzerReference, ProjectCodeFixProvider> _analyzerReferenceToFixersMap = new();
        private readonly ConditionalWeakTable<AnalyzerReference, ProjectCodeFixProvider>.CreateValueCallback _createProjectCodeFixProvider = r => new ProjectCodeFixProvider(r);
        private readonly ImmutableDictionary<LanguageKind, Lazy<ImmutableArray<IConfigurationFixProvider>>> _configurationProvidersMap;
        private readonly ImmutableArray<Lazy<IErrorLoggerService>> _errorLoggers;

        private ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>>? _lazyWorkspaceFixersMap;
        private ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<CodeFixProvider, int>>>? _lazyFixerPriorityMap;

        private ImmutableDictionary<CodeFixProvider, ImmutableArray<DiagnosticId>> _fixerToFixableIdsMap = ImmutableDictionary<CodeFixProvider, ImmutableArray<DiagnosticId>>.Empty;
        private ImmutableDictionary<object, FixAllProviderInfo?> _fixAllProviderMap = ImmutableDictionary<object, FixAllProviderInfo?>.Empty;
        private ImmutableDictionary<CodeFixProvider, CodeChangeProviderMetadata?> _fixerToMetadataMap = ImmutableDictionary<CodeFixProvider, CodeChangeProviderMetadata?>.Empty;

        [ImportingConstructor]
        [SuppressMessage("RoslynDiagnosticsReliability", "RS0033:Importing constructor should be [Obsolete]", Justification = "Used in test code: https://github.com/dotnet/roslyn/issues/42814")]
        public CodeFixService(
            IDiagnosticAnalyzerService diagnosticAnalyzerService,
            [ImportMany] IEnumerable<Lazy<IErrorLoggerService>> loggers,
            [ImportMany] IEnumerable<Lazy<CodeFixProvider, CodeChangeProviderMetadata>> fixers,
            [ImportMany] IEnumerable<Lazy<IConfigurationFixProvider, CodeChangeProviderMetadata>> configurationProviders)
        {
            _diagnosticService = diagnosticAnalyzerService;
            _errorLoggers = loggers.ToImmutableArray();

            _fixers = fixers.ToImmutableArray();
            _fixersPerLanguageMap = _fixers.ToPerLanguageMapWithMultipleLanguages();

            _configurationProvidersMap = GetConfigurationProvidersPerLanguageMap(configurationProviders);
        }

        public async Task<FirstDiagnosticResult> GetMostSevereFixableDiagnosticAsync(
            Document document, TextSpan range, CodeActionOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!document.IsOpen())
            {
                return default;
            }

            using var _ = ArrayBuilder<DiagnosticData>.GetInstance(out var diagnostics);
            using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var linkedToken = linkedTokenSource.Token;

            // This flag is used by SuggestedActionsSource to track what solution is was
            // last able to get "full results" for.
            var isFullResult = await _diagnosticService.TryAppendDiagnosticsForSpanAsync(
                document, range, diagnostics, cancellationToken: linkedToken).ConfigureAwait(false);

            var errorDiagnostics = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            var otherDiagnostics = diagnostics.Where(d => d.Severity != DiagnosticSeverity.Error);

            // Kick off a task that will determine there's an Error Diagnostic with a fixer
            var errorDiagnosticsTask = Task.Run(
                () => GetFirstDiagnosticWithFixAsync(document, errorDiagnostics, range, options, linkedToken),
                linkedToken);

            // Kick off a task that will determine if any non-Error Diagnostic has a fixer
            var otherDiagnosticsTask = Task.Run(
                () => GetFirstDiagnosticWithFixAsync(document, otherDiagnostics, range, options, linkedToken),
                linkedToken);

            // If the error diagnostics task happens to complete with a non-null result before
            // the other diagnostics task, we can cancel the other task.
            var diagnostic = await errorDiagnosticsTask.ConfigureAwait(false)
                ?? await otherDiagnosticsTask.ConfigureAwait(false);
            linkedTokenSource.Cancel();

            return new FirstDiagnosticResult(partialResult: !isFullResult,
                                   hasFix: diagnostic != null,
                                   diagnostic: diagnostic);
        }

        private async Task<DiagnosticData?> GetFirstDiagnosticWithFixAsync(
            Document document,
            IEnumerable<DiagnosticData> severityGroup,
            TextSpan range,
            CodeActionOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var diagnostic in severityGroup)
            {
                if (!range.IntersectsWith(diagnostic.GetTextSpan()))
                {
                    continue;
                }

                if (await ContainsAnyFixAsync(document, diagnostic, options, cancellationToken).ConfigureAwait(false))
                {
                    return diagnostic;
                }
            }

            return null;
        }

        public async IAsyncEnumerable<CodeFixCollection> StreamFixesAsync(
            Document document,
            TextSpan range,
            CodeActionRequestPriority priority,
            CodeActionOptions options,
            Func<string, IDisposable?> addOperationScope,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // REVIEW: this is the first and simplest design. basically, when ctrl+. is pressed, it asks diagnostic service to give back
            // current diagnostics for the given span, and it will use that to get fixes. internally diagnostic service will either return cached information
            // (if it is up-to-date) or synchronously do the work at the spot.
            //
            // this design's weakness is that each side don't have enough information to narrow down works to do. it will most likely always do more works than needed.
            // sometimes way more than it is needed. (compilation)

            // group diagnostics by their diagnostics span
            // invariant: later code gathers & runs CodeFixProviders for diagnostics with one identical diagnostics span (that gets set later as CodeFixCollection's TextSpan)
            // order diagnostics by span.
            var aggregatedDiagnostics = new SortedDictionary<TextSpan, List<DiagnosticData>>();

            // For 'CodeActionPriorityRequest.Normal' or 'CodeActionPriorityRequest.Low', we do not compute suppression/configuration fixes,
            // those fixes have a dedicated request priority 'CodeActionPriorityRequest.Lowest'.
            // Hence, for Normal or Low priority, we only need to execute analyzers which can report at least one fixable diagnostic
            // that can have a non-suppression/configuration fix.
            // Note that for 'CodeActionPriorityRequest.High', we only run compiler analyzer, which always has fixable diagnostics,
            // so we can pass in null. 
            var shouldIncludeDiagnostic = priority is CodeActionRequestPriority.Normal or CodeActionRequestPriority.Low
                ? GetFixableDiagnosticFilter(document)
                : null;

            // We only need to compute suppression/configuration fixes when request priority is
            // 'CodeActionPriorityRequest.Lowest' or 'CodeActionPriorityRequest.None'.
            var includeSuppressionFixes = priority is CodeActionRequestPriority.Lowest or CodeActionRequestPriority.None;

            var diagnostics = await _diagnosticService.GetDiagnosticsForSpanAsync(
                document, range, shouldIncludeDiagnostic, includeSuppressionFixes, priority, addOperationScope, cancellationToken).ConfigureAwait(false);
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.IsSuppressed)
                    continue;

                var list = aggregatedDiagnostics.GetOrAdd(diagnostic.GetTextSpan(), static _ => new List<DiagnosticData>());
                list.Add(diagnostic);
            }

            if (aggregatedDiagnostics.Count == 0)
                yield break;

            // Order diagnostics by DiagnosticId so the fixes are in a deterministic order.
            foreach (var (_, diagnosticList) in aggregatedDiagnostics)
                diagnosticList.Sort(s_diagnosticDataComparisonById);

            // 'CodeActionRequestPriority.Lowest' is used when the client only wants suppression/configuration fixes.
            if (priority != CodeActionRequestPriority.Lowest)
            {
                await foreach (var collection in StreamFixesAsync(
                    document, aggregatedDiagnostics, fixAllForInSpan: false,
                    priority, options, addOperationScope, cancellationToken).ConfigureAwait(false))
                {
                    yield return collection;
                }
            }

            // TODO (https://github.com/dotnet/roslyn/issues/4932): Don't restrict CodeFixes in Interactive
            if (document.Project.Solution.Workspace.Kind != WorkspaceKind.Interactive && includeSuppressionFixes)
            {
                // Ensure that we do not register duplicate configuration fixes.
                using var _2 = PooledHashSet<string>.GetInstance(out var registeredConfigurationFixTitles);
                foreach (var (span, diagnosticList) in aggregatedDiagnostics)
                {
                    await foreach (var codeFixCollection in StreamConfigurationFixesAsync(
                        document, span, diagnosticList, registeredConfigurationFixTitles, options, cancellationToken).ConfigureAwait(false))
                    {
                        yield return codeFixCollection;
                    }
                }
            }

            yield break;

            // Local functions
            Func<string, bool> GetFixableDiagnosticFilter(Document document)
            {
                var hasWorkspaceFixers = TryGetWorkspaceFixersMap(document, out var workspaceFixersMap);
                var projectFixersMap = GetProjectFixers(document.Project);

                return id =>
                {
                    if (hasWorkspaceFixers && workspaceFixersMap!.Value.ContainsKey(id))
                        return true;

                    return projectFixersMap.ContainsKey(id);
                };
            }
        }

        public async Task<CodeFixCollection?> GetDocumentFixAllForIdInSpanAsync(
            Document document, TextSpan range, string diagnosticId, CodeActionOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var diagnostics = (await _diagnosticService.GetDiagnosticsForSpanAsync(document, range, diagnosticId, includeSuppressedDiagnostics: false, cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
            if (diagnostics.Count == 0)
                return null;

            using var resultDisposer = ArrayBuilder<CodeFixCollection>.GetInstance(out var result);
            var spanToDiagnostics = new SortedDictionary<TextSpan, List<DiagnosticData>>
            {
                { range, diagnostics },
            };

            await foreach (var collection in StreamFixesAsync(
                document, spanToDiagnostics, fixAllForInSpan: true, CodeActionRequestPriority.None,
                options, addOperationScope: static _ => null, cancellationToken).ConfigureAwait(false))
            {
                // TODO: Just get the first fix for now until we have a way to config user's preferred fix
                // https://github.com/dotnet/roslyn/issues/27066
                return collection;
            }

            return null;
        }

        public async Task<Document> ApplyCodeFixesForSpecificDiagnosticIdAsync(Document document, string diagnosticId, IProgressTracker progressTracker, CodeActionOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tree = await document.GetRequiredSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var textSpan = new TextSpan(0, tree.Length);

            var fixCollection = await GetDocumentFixAllForIdInSpanAsync(
                document, textSpan, diagnosticId, options, cancellationToken).ConfigureAwait(false);
            if (fixCollection == null)
            {
                return document;
            }

            var fixAllService = document.Project.Solution.Workspace.Services.GetRequiredService<IFixAllGetFixesService>();

            var solution = await fixAllService.GetFixAllChangedSolutionAsync(
                new FixAllContext(fixCollection.FixAllState, progressTracker, cancellationToken)).ConfigureAwait(false);

            return solution.GetDocument(document.Id) ?? throw new NotSupportedException(FeaturesResources.Removal_of_document_not_supported);
        }

        private bool TryGetWorkspaceFixersMap(Document document, [NotNullWhen(true)] out Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>? fixerMap)
        {
            if (_lazyWorkspaceFixersMap == null)
            {
                var workspaceFixersMap = GetFixerPerLanguageMap(document.Project.Solution.Workspace);
                Interlocked.CompareExchange(ref _lazyWorkspaceFixersMap, workspaceFixersMap, null);
            }

            return _lazyWorkspaceFixersMap.TryGetValue(document.Project.Language, out fixerMap);
        }

        private bool TryGetWorkspaceFixersPriorityMap(Document document, [NotNullWhen(true)] out Lazy<ImmutableDictionary<CodeFixProvider, int>>? fixersPriorityMap)
        {
            if (_lazyFixerPriorityMap == null)
            {
                var fixersPriorityByLanguageMap = GetFixerPriorityPerLanguageMap(document.Project.Solution.Workspace);
                Interlocked.CompareExchange(ref _lazyFixerPriorityMap, fixersPriorityByLanguageMap, null);
            }

            return _lazyFixerPriorityMap.TryGetValue(document.Project.Language, out fixersPriorityMap);
        }

        private bool TryGetWorkspaceFixer(
            Lazy<CodeFixProvider, CodeChangeProviderMetadata> lazyFixer,
            Workspace workspace,
            bool logExceptionWithInfoBar,
            [NotNullWhen(returnValue: true)] out CodeFixProvider? fixer)
        {
            try
            {
                fixer = lazyFixer.Value;
                return true;
            }
            catch (Exception ex)
            {
                // Gracefully handle exceptions in creating fixer instance.
                // Log exception and show info bar, if needed.
                if (logExceptionWithInfoBar)
                {
                    var errorReportingService = workspace.Services.GetRequiredService<IErrorReportingService>();
                    var message = lazyFixer.Metadata.Name != null
                        ? string.Format(FeaturesResources.Error_creating_instance_of_CodeFixProvider_0, lazyFixer.Metadata.Name)
                        : FeaturesResources.Error_creating_instance_of_CodeFixProvider;

                    errorReportingService.ShowGlobalErrorInfo(
                        message,
                        TelemetryFeatureName.CodeFixProvider,
                        ex,
                        new InfoBarUI(
                            WorkspacesResources.Show_Stack_Trace,
                            InfoBarUI.UIKind.HyperLink,
                            () => errorReportingService.ShowDetailedErrorInfo(ex), closeAfterAction: true));

                    foreach (var errorLogger in _errorLoggers)
                    {
                        errorLogger.Value.LogException(this, ex);
                    }
                }

                fixer = null;
                return false;
            }
        }

        private async IAsyncEnumerable<CodeFixCollection> StreamFixesAsync(
            Document document,
            SortedDictionary<TextSpan, List<DiagnosticData>> spanToDiagnostics,
            bool fixAllForInSpan,
            CodeActionRequestPriority priority,
            CodeActionOptions options,
            Func<string, IDisposable?> addOperationScope,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasAnySharedFixer = TryGetWorkspaceFixersMap(document, out var fixerMap);

            var projectFixersMap = GetProjectFixers(document.Project);
            var hasAnyProjectFixer = projectFixersMap.Any();

            if (!hasAnySharedFixer && !hasAnyProjectFixer)
                yield break;

            // TODO (https://github.com/dotnet/roslyn/issues/4932): Don't restrict CodeFixes in Interactive
            var isInteractive = document.Project.Solution.Workspace.Kind == WorkspaceKind.Interactive;

            // gather CodeFixProviders for all distinct diagnostics found for current span
            using var _1 = ArrayBuilder<CodeFixProvider>.GetInstance(out var allFixers);
            using var _2 = PooledDictionary<CodeFixProvider, List<(TextSpan range, List<DiagnosticData> diagnostics)>>.GetInstance(out var fixerToRangesAndDiagnostics);

            foreach (var (range, diagnostics) in spanToDiagnostics)
            {
                foreach (var diagnosticId in diagnostics.Select(d => d.Id).Distinct())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Prioritize NuGet based project code fixers over VSIX based workspace code fixers.
                    if (hasAnyProjectFixer && projectFixersMap.TryGetValue(diagnosticId, out var projectFixers))
                    {
                        Debug.Assert(!isInteractive);
                        AddAllFixers(projectFixers, range, diagnostics);
                    }

                    if (hasAnySharedFixer && fixerMap!.Value.TryGetValue(diagnosticId, out var workspaceFixers))
                    {
                        if (isInteractive)
                        {
                            AddAllFixers(workspaceFixers.WhereAsArray(IsInteractiveCodeFixProvider), range, diagnostics);
                        }
                        else
                        {
                            AddAllFixers(workspaceFixers, range, diagnostics);
                        }
                    }
                }
            }

            // Now, sort the fixers so that the ones that are ordered before others get their chance to run first.
            if (allFixers.Count >= 2 && TryGetWorkspaceFixersPriorityMap(document, out var fixersForLanguage))
                allFixers.Sort(new FixerComparer(allFixers, fixersForLanguage.Value));

            var extensionManager = document.Project.Solution.Workspace.Services.GetService<IExtensionManager>();

            // Run each CodeFixProvider to gather individual CodeFixes for reported diagnostics.
            // Ensure that no diagnostic has registered code actions from different code fix providers with same equivalance key.
            // This prevents duplicate registered code actions from NuGet and VSIX code fix providers.
            // See https://github.com/dotnet/roslyn/issues/18818 for details.
            var uniqueDiagosticToEquivalenceKeysMap = new Dictionary<Diagnostic, PooledHashSet<string?>>();

            // NOTE: For backward compatibility, we allow multiple registered code actions from the same code fix provider
            // to have the same equivalence key. See https://github.com/dotnet/roslyn/issues/44553 for details.
            // To ensure this, we track the fixer that first registered a code action to fix a diagnostic with a specific equivalence key.
            var diagnosticAndEquivalenceKeyToFixersMap = new Dictionary<(Diagnostic diagnostic, string? equivalenceKey), CodeFixProvider>();

            try
            {
                foreach (var fixer in allFixers)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (priority != CodeActionRequestPriority.None && priority != fixer.RequestPriority)
                        continue;

                    foreach (var (span, diagnostics) in fixerToRangesAndDiagnostics[fixer])
                    {
                        var codeFixCollection = await TryGetFixesOrConfigurationsAsync(
                            document, span, diagnostics, fixAllForInSpan, fixer,
                            hasFix: d => this.GetFixableDiagnosticIds(fixer, extensionManager).Contains(d.Id),
                            getFixes: dxs =>
                            {
                                var fixerName = fixer.GetType().Name;
                                var fixerMetadata = TryGetMetadata(fixer);

                                using (addOperationScope(fixerName))
                                using (RoslynEventSource.LogInformationalBlock(FunctionId.CodeFixes_GetCodeFixesAsync, fixerName, cancellationToken))
                                {
                                    if (fixAllForInSpan)
                                    {
                                        var primaryDiagnostic = dxs.First();
                                        return GetCodeFixesAsync(document, primaryDiagnostic.Location.SourceSpan, fixer, fixerMetadata, options,
                                            ImmutableArray.Create(primaryDiagnostic), uniqueDiagosticToEquivalenceKeysMap,
                                            diagnosticAndEquivalenceKeyToFixersMap, cancellationToken);
                                    }
                                    else
                                    {
                                        return GetCodeFixesAsync(document, span, fixer, fixerMetadata, options, dxs,
                                            uniqueDiagosticToEquivalenceKeysMap, diagnosticAndEquivalenceKeyToFixersMap, cancellationToken);
                                    }
                                }
                            },
                            options,
                            cancellationToken).ConfigureAwait(false);

                        if (codeFixCollection != null)
                        {
                            yield return codeFixCollection;

                            // Just need the first result if we are doing fix all in span
                            if (fixAllForInSpan)
                                yield break;
                        }
                    }
                }
            }
            finally
            {
                foreach (var pooledSet in uniqueDiagosticToEquivalenceKeysMap.Values)
                {
                    pooledSet.Free();
                }
            }

            yield break;

            void AddAllFixers(
                ImmutableArray<CodeFixProvider> fixers,
                TextSpan range,
                List<DiagnosticData> diagnostics)
            {
                foreach (var fixer in fixers)
                {
                    if (allFixers.Contains(fixer))
                        continue;

                    allFixers.Add(fixer);
                    fixerToRangesAndDiagnostics.GetOrAdd(fixer, static _ => new()).Add((range, diagnostics));
                }
            }
        }

        private CodeChangeProviderMetadata? TryGetMetadata(CodeFixProvider fixer)
        {
            return ImmutableInterlocked.GetOrAdd(
                ref _fixerToMetadataMap,
                fixer,
                static (fixer, fixers) =>
                {
                    foreach (var lazy in fixers)
                    {
                        if (lazy.IsValueCreated && lazy.Value == fixer)
                            return lazy.Metadata;
                    }

                    // Note: it feels very strange that we could ever not find a fixer in our list.  However, this
                    // occurs in testing scenarios.  I'm not sure if the tests represent a bogus potential input, or if
                    // this is something that can actually occur in practice and we want to keep working.
                    return null;
                },
                _fixers);
        }

        private static async Task<ImmutableArray<CodeFix>> GetCodeFixesAsync(
            Document document, TextSpan span, CodeFixProvider fixer, CodeChangeProviderMetadata? fixerMetadata, CodeActionOptions options,
            ImmutableArray<Diagnostic> diagnostics,
            Dictionary<Diagnostic, PooledHashSet<string?>> uniqueDiagosticToEquivalenceKeysMap,
            Dictionary<(Diagnostic diagnostic, string? equivalenceKey), CodeFixProvider> diagnosticAndEquivalenceKeyToFixersMap,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var fixesDisposer = ArrayBuilder<CodeFix>.GetInstance(out var fixes);
            var context = new CodeFixContext(document, span, diagnostics,
                // TODO: Can we share code between similar lambdas that we pass to this API in BatchFixAllProvider.cs, CodeFixService.cs and CodeRefactoringService.cs?
                (action, applicableDiagnostics) =>
                {
                    // Serialize access for thread safety - we don't know what thread the fix provider will call this delegate from.
                    lock (fixes)
                    {
                        // Filter out applicable diagnostics which already have a registered code action with same equivalence key.
                        applicableDiagnostics = FilterApplicableDiagnostics(applicableDiagnostics, action.EquivalenceKey,
                            fixer, uniqueDiagosticToEquivalenceKeysMap, diagnosticAndEquivalenceKeyToFixersMap);

                        if (!applicableDiagnostics.IsEmpty)
                        {
                            // Add the CodeFix Provider Name to the parent CodeAction's CustomTags.
                            // Always add a name even in cases of 3rd party fixers that do not export
                            // name metadata.
                            action.AddCustomTag(fixerMetadata?.Name ?? fixer.GetTypeDisplayName());

                            fixes.Add(new CodeFix(document.Project, action, applicableDiagnostics));
                        }
                    }
                },
                options,
                cancellationToken);

            var task = fixer.RegisterCodeFixesAsync(context) ?? Task.CompletedTask;
            await task.ConfigureAwait(false);
            return fixes.ToImmutable();

            static ImmutableArray<Diagnostic> FilterApplicableDiagnostics(
                ImmutableArray<Diagnostic> applicableDiagnostics,
                string? equivalenceKey,
                CodeFixProvider fixer,
                Dictionary<Diagnostic, PooledHashSet<string?>> uniqueDiagosticToEquivalenceKeysMap,
                Dictionary<(Diagnostic diagnostic, string? equivalenceKey), CodeFixProvider> diagnosticAndEquivalenceKeyToFixersMap)
            {
                using var disposer = ArrayBuilder<Diagnostic>.GetInstance(out var newApplicableDiagnostics);
                foreach (var diagnostic in applicableDiagnostics)
                {
                    if (!uniqueDiagosticToEquivalenceKeysMap.TryGetValue(diagnostic, out var equivalenceKeys))
                    {
                        // First code action registered to fix this diagnostic with any equivalenceKey.
                        // Record the equivalence key and the fixer that registered this action.
                        equivalenceKeys = PooledHashSet<string?>.GetInstance();
                        equivalenceKeys.Add(equivalenceKey);
                        uniqueDiagosticToEquivalenceKeysMap[diagnostic] = equivalenceKeys;
                        diagnosticAndEquivalenceKeyToFixersMap.Add((diagnostic, equivalenceKey), fixer);
                    }
                    else if (equivalenceKeys.Add(equivalenceKey))
                    {
                        // First code action registered to fix this diagnostic with the given equivalenceKey.
                        // Record the the fixer that registered this action.
                        diagnosticAndEquivalenceKeyToFixersMap.Add((diagnostic, equivalenceKey), fixer);
                    }
                    else if (diagnosticAndEquivalenceKeyToFixersMap[(diagnostic, equivalenceKey)] != fixer)
                    {
                        // Diagnostic already has a registered code action with same equivalence key from a different fixer.
                        // Note that we allow same fixer to register multiple such code actions with the same equivalence key
                        // for backward compatibility. See https://github.com/dotnet/roslyn/issues/44553 for details.
                        continue;
                    }

                    newApplicableDiagnostics.Add(diagnostic);
                }

                return newApplicableDiagnostics.Count == applicableDiagnostics.Length
                    ? applicableDiagnostics
                    : newApplicableDiagnostics.ToImmutable();
            }
        }

        private async IAsyncEnumerable<CodeFixCollection> StreamConfigurationFixesAsync(
            Document document,
            TextSpan diagnosticsSpan,
            IEnumerable<DiagnosticData> diagnostics,
            PooledHashSet<string> registeredConfigurationFixTitles,
            CodeActionOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_configurationProvidersMap.TryGetValue(document.Project.Language, out var lazyConfigurationProviders) ||
                lazyConfigurationProviders.Value == null)
            {
                yield break;
            }

            // append CodeFixCollection for each CodeFixProvider
            foreach (var provider in lazyConfigurationProviders.Value)
            {
                using (RoslynEventSource.LogInformationalBlock(FunctionId.CodeFixes_GetCodeFixesAsync, provider, cancellationToken))
                {
                    var codeFixCollection = await TryGetFixesOrConfigurationsAsync(
                        document, diagnosticsSpan, diagnostics, fixAllForInSpan: false, provider,
                        hasFix: d => provider.IsFixableDiagnostic(d),
                        getFixes: async dxs =>
                        {
                            var fixes = await provider.GetFixesAsync(document, diagnosticsSpan, dxs, cancellationToken).ConfigureAwait(false);
                            return fixes.WhereAsArray(f => registeredConfigurationFixTitles.Add(f.Action.Title));
                        },
                        options,
                        cancellationToken).ConfigureAwait(false);
                    if (codeFixCollection != null)
                        yield return codeFixCollection;
                }
            }
        }

        private async Task<CodeFixCollection?> TryGetFixesOrConfigurationsAsync<TCodeFixProvider>(
            Document document,
            TextSpan fixesSpan,
            IEnumerable<DiagnosticData> diagnosticsWithSameSpan,
            bool fixAllForInSpan,
            TCodeFixProvider fixer,
            Func<Diagnostic, bool> hasFix,
            Func<ImmutableArray<Diagnostic>, Task<ImmutableArray<CodeFix>>> getFixes,
            CodeActionOptions options,
            CancellationToken cancellationToken)
            where TCodeFixProvider : notnull
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allDiagnostics =
                await diagnosticsWithSameSpan.OrderByDescending(d => d.Severity)
                                             .ToDiagnosticsAsync(document.Project, cancellationToken).ConfigureAwait(false);
            var diagnostics = allDiagnostics.WhereAsArray(hasFix);
            if (diagnostics.Length <= 0)
            {
                // this can happen for suppression case where all diagnostics can't be suppressed
                return null;
            }

            var extensionManager = document.Project.Solution.Workspace.Services.GetRequiredService<IExtensionManager>();
            var fixes = await extensionManager.PerformFunctionAsync(fixer,
                 () => getFixes(diagnostics),
                defaultValue: ImmutableArray<CodeFix>.Empty).ConfigureAwait(false);

            if (fixes.IsDefaultOrEmpty)
                return null;

            // If the fix provider supports fix all occurrences, then get the corresponding FixAllProviderInfo and fix all context.
            var fixAllProviderInfo = extensionManager.PerformFunction(
                fixer, () => ImmutableInterlocked.GetOrAdd(ref _fixAllProviderMap, fixer, FixAllProviderInfo.Create), defaultValue: null);

            FixAllState? fixAllState = null;
            var supportedScopes = ImmutableArray<FixAllScope>.Empty;
            if (fixAllProviderInfo != null)
            {
                var codeFixProvider = (fixer as CodeFixProvider) ?? new WrapperCodeFixProvider((IConfigurationFixProvider)fixer, diagnostics.Select(d => d.Id));

                var diagnosticIds = diagnostics.Where(fixAllProviderInfo.CanBeFixed)
                                               .Select(d => d.Id)
                                               .ToImmutableHashSet();

                // When computing FixAll for unnecessary pragma suppression diagnostic,
                // we need to include suppressed diagnostics, as well as reported compiler and analyzer diagnostics.
                // A null value for 'diagnosticIdsForDiagnosticProvider' passed to 'FixAllDiagnosticProvider'
                // ensures the latter.
                ImmutableHashSet<string>? diagnosticIdsForDiagnosticProvider;
                bool includeSuppressedDiagnostics;
                if (diagnosticIds.Contains(IDEDiagnosticIds.RemoveUnnecessarySuppressionDiagnosticId))
                {
                    diagnosticIdsForDiagnosticProvider = null;
                    includeSuppressedDiagnostics = true;
                }
                else
                {
                    diagnosticIdsForDiagnosticProvider = diagnosticIds;
                    includeSuppressedDiagnostics = false;
                }

                var diagnosticProvider = fixAllForInSpan
                    ? new FixAllPredefinedDiagnosticProvider(allDiagnostics)
                    : (FixAllContext.DiagnosticProvider)new FixAllDiagnosticProvider(this, diagnosticIdsForDiagnosticProvider, includeSuppressedDiagnostics);

                fixAllState = new FixAllState(
                    fixAllProviderInfo.FixAllProvider,
                    document,
                    document.Project,
                    codeFixProvider,
                    FixAllScope.Document,
                    fixes[0].Action.EquivalenceKey,
                    diagnosticIds,
                    diagnosticProvider,
                    codeActionOptionsProvider: language =>
                    {
                        Contract.ThrowIfFalse(language == document.Project.Language);
                        return options;
                    });

                supportedScopes = fixAllProviderInfo.SupportedScopes;
            }

            return new CodeFixCollection(
                fixer, fixesSpan, fixes, fixAllState,
                supportedScopes, diagnostics.First());
        }

        /// <summary> Looks explicitly for an <see cref="AbstractSuppressionCodeFixProvider"/>.</summary>
        public CodeFixProvider? GetSuppressionFixer(string language, IEnumerable<string> diagnosticIds)
        {
            if (!_configurationProvidersMap.TryGetValue(language, out var lazyConfigurationProviders) ||
                lazyConfigurationProviders.Value.IsDefault)
            {
                return null;
            }

            // Explicitly looks for an AbstractSuppressionCodeFixProvider
            var fixer = lazyConfigurationProviders.Value.OfType<AbstractSuppressionCodeFixProvider>().FirstOrDefault();
            if (fixer == null)
            {
                return null;
            }

            return new WrapperCodeFixProvider(fixer, diagnosticIds);
        }

        private async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, ImmutableHashSet<string>? diagnosticIds, bool includeSuppressedDiagnostics, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Contract.ThrowIfNull(document);
            var solution = document.Project.Solution;
            var diagnostics = await _diagnosticService.GetDiagnosticsForIdsAsync(solution, null, document.Id, diagnosticIds, includeSuppressedDiagnostics, cancellationToken).ConfigureAwait(false);
            Contract.ThrowIfFalse(diagnostics.All(d => d.DocumentId != null));
            return await diagnostics.ToDiagnosticsAsync(document.Project, cancellationToken).ConfigureAwait(false);
        }

        private async Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, bool includeAllDocumentDiagnostics, ImmutableHashSet<string>? diagnosticIds, bool includeSuppressedDiagnostics, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Contract.ThrowIfNull(project);

            if (includeAllDocumentDiagnostics)
            {
                // Get all diagnostics for the entire project, including document diagnostics.
                var diagnostics = await _diagnosticService.GetDiagnosticsForIdsAsync(project.Solution, project.Id, documentId: null, diagnosticIds, includeSuppressedDiagnostics, cancellationToken).ConfigureAwait(false);
                return await diagnostics.ToDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Get all no-location diagnostics for the project, doesn't include document diagnostics.
                var diagnostics = await _diagnosticService.GetProjectDiagnosticsForIdsAsync(project.Solution, project.Id, diagnosticIds, includeSuppressedDiagnostics, cancellationToken).ConfigureAwait(false);
                Contract.ThrowIfFalse(diagnostics.All(d => d.DocumentId == null));
                return await diagnostics.ToDiagnosticsAsync(project, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<bool> ContainsAnyFixAsync(
            Document document, DiagnosticData diagnosticData, CodeActionOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workspaceFixers = ImmutableArray<CodeFixProvider>.Empty;
            var hasAnySharedFixer = TryGetWorkspaceFixersMap(document, out var fixerMap) && fixerMap.Value.TryGetValue(diagnosticData.Id, out workspaceFixers);
            var hasAnyProjectFixer = GetProjectFixers(document.Project).TryGetValue(diagnosticData.Id, out var projectFixers);

            // TODO (https://github.com/dotnet/roslyn/issues/4932): Don't restrict CodeFixes in Interactive
            if (hasAnySharedFixer && document.Project.Solution.Workspace.Kind == WorkspaceKind.Interactive)
            {
                workspaceFixers = workspaceFixers.WhereAsArray(IsInteractiveCodeFixProvider);
                hasAnySharedFixer = workspaceFixers.Any();
            }

            var hasConfigurationFixer =
                _configurationProvidersMap.TryGetValue(document.Project.Language, out var lazyConfigurationProviders) &&
                !lazyConfigurationProviders.Value.IsDefaultOrEmpty;

            if (!hasAnySharedFixer && !hasAnyProjectFixer && !hasConfigurationFixer)
            {
                return false;
            }

            var allFixers = ImmutableArray<CodeFixProvider>.Empty;
            if (hasAnySharedFixer)
            {
                allFixers = workspaceFixers;
            }

            if (hasAnyProjectFixer)
            {
                allFixers = allFixers.AddRange(projectFixers!);
            }

            var diagnostic = await diagnosticData.ToDiagnosticAsync(document.Project, cancellationToken).ConfigureAwait(false);

            if (hasConfigurationFixer)
            {
                foreach (var lazyConfigurationProvider in lazyConfigurationProviders!.Value)
                {
                    if (lazyConfigurationProvider.IsFixableDiagnostic(diagnostic))
                    {
                        return true;
                    }
                }
            }

            var fixes = new List<CodeFix>();
            var context = new CodeFixContext(document, diagnostic.Location.SourceSpan, ImmutableArray.Create(diagnostic),

                // TODO: Can we share code between similar lambdas that we pass to this API in BatchFixAllProvider.cs, CodeFixService.cs and CodeRefactoringService.cs?
                (action, applicableDiagnostics) =>
                {
                    // Serialize access for thread safety - we don't know what thread the fix provider will call this delegate from.
                    lock (fixes)
                    {
                        fixes.Add(new CodeFix(document.Project, action, applicableDiagnostics));
                    }
                },
                options,
                cancellationToken);

            var extensionManager = document.Project.Solution.Workspace.Services.GetRequiredService<IExtensionManager>();

            // we do have fixer. now let's see whether it actually can fix it
            foreach (var fixer in allFixers)
            {
                await extensionManager.PerformActionAsync(fixer, () => fixer.RegisterCodeFixesAsync(context) ?? Task.CompletedTask).ConfigureAwait(false);
                if (fixes.Count > 0)
                    return true;
            }

            return false;
        }

        private bool IsInteractiveCodeFixProvider(CodeFixProvider provider)
        {
            // TODO (https://github.com/dotnet/roslyn/issues/4932): Don't restrict CodeFixes in Interactive
            return provider is FullyQualify.AbstractFullyQualifyCodeFixProvider or
                   AddImport.AbstractAddImportCodeFixProvider;
        }

        private ImmutableArray<DiagnosticId> GetFixableDiagnosticIds(CodeFixProvider fixer, IExtensionManager? extensionManager)
        {
            // If we are passed a null extension manager it means we do not have access to a document so there is nothing to
            // show the user.  In this case we will log any exceptions that occur, but the user will not see them.
            if (extensionManager != null)
            {
                return extensionManager.PerformFunction(
                    fixer,
                    () => ImmutableInterlocked.GetOrAdd(ref _fixerToFixableIdsMap, fixer, f => GetAndTestFixableDiagnosticIds(f)),
                    defaultValue: ImmutableArray<DiagnosticId>.Empty);
            }

            try
            {
                return ImmutableInterlocked.GetOrAdd(ref _fixerToFixableIdsMap, fixer, f => GetAndTestFixableDiagnosticIds(f));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                foreach (var logger in _errorLoggers)
                {
                    logger.Value.LogException(fixer, e);
                }

                return ImmutableArray<DiagnosticId>.Empty;
            }
        }

        private static ImmutableArray<string> GetAndTestFixableDiagnosticIds(CodeFixProvider codeFixProvider)
        {
            var ids = codeFixProvider.FixableDiagnosticIds;
            if (ids.IsDefault)
            {
                throw new InvalidOperationException(
                    string.Format(
                        WorkspacesResources._0_returned_an_uninitialized_ImmutableArray,
                        codeFixProvider.GetType().Name + "." + nameof(CodeFixProvider.FixableDiagnosticIds)));
            }

            return ids;
        }

        private ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>> GetFixerPerLanguageMap(
            Workspace workspace)
        {
            var fixerMap = ImmutableDictionary.Create<LanguageKind, Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>>();
            var extensionManager = workspace.Services.GetService<IExtensionManager>();
            foreach (var (diagnosticId, lazyFixers) in _fixersPerLanguageMap)
            {
                var lazyMap = new Lazy<ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>>(() =>
                {
                    using var _ = PooledDictionary<DiagnosticId, ArrayBuilder<CodeFixProvider>>.GetInstance(out var mutableMap);

                    foreach (var lazyFixer in lazyFixers)
                    {
                        if (!TryGetWorkspaceFixer(lazyFixer, workspace, logExceptionWithInfoBar: true, out var fixer))
                        {
                            continue;
                        }

                        foreach (var id in this.GetFixableDiagnosticIds(fixer, extensionManager))
                        {
                            if (string.IsNullOrWhiteSpace(id))
                            {
                                continue;
                            }

                            var list = mutableMap.GetOrAdd(id, static _ => ArrayBuilder<CodeFixProvider>.GetInstance());
                            list.Add(fixer);
                        }
                    }

                    return mutableMap.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableAndFree());
                }, isThreadSafe: true);

                fixerMap = fixerMap.Add(diagnosticId, lazyMap);
            }

            return fixerMap;
        }

        private static ImmutableDictionary<LanguageKind, Lazy<ImmutableArray<IConfigurationFixProvider>>> GetConfigurationProvidersPerLanguageMap(
            IEnumerable<Lazy<IConfigurationFixProvider, CodeChangeProviderMetadata>> configurationProviders)
        {
            var configurationProvidersPerLanguageMap = configurationProviders.ToPerLanguageMapWithMultipleLanguages();

            var configurationFixerMap = ImmutableDictionary.CreateBuilder<LanguageKind, Lazy<ImmutableArray<IConfigurationFixProvider>>>();
            foreach (var (diagnosticId, lazyFixers) in configurationProvidersPerLanguageMap)
            {
                var lazyConfigurationFixers = new Lazy<ImmutableArray<IConfigurationFixProvider>>(() => GetConfigurationFixProviders(lazyFixers));
                configurationFixerMap.Add(diagnosticId, lazyConfigurationFixers);
            }

            return configurationFixerMap.ToImmutable();

            static ImmutableArray<IConfigurationFixProvider> GetConfigurationFixProviders(ImmutableArray<Lazy<IConfigurationFixProvider, CodeChangeProviderMetadata>> languageKindAndFixers)
            {
                using var builderDisposer = ArrayBuilder<IConfigurationFixProvider>.GetInstance(out var builder);
                var orderedLanguageKindAndFixers = ExtensionOrderer.Order(languageKindAndFixers);
                foreach (var languageKindAndFixersValue in orderedLanguageKindAndFixers)
                {
                    builder.Add(languageKindAndFixersValue.Value);
                }

                return builder.ToImmutable();
            }
        }

        private ImmutableDictionary<LanguageKind, Lazy<ImmutableDictionary<CodeFixProvider, int>>> GetFixerPriorityPerLanguageMap(Workspace workspace)
        {
            var languageMap = ImmutableDictionary.CreateBuilder<LanguageKind, Lazy<ImmutableDictionary<CodeFixProvider, int>>>();
            foreach (var (diagnosticId, lazyFixers) in _fixersPerLanguageMap)
            {
                var lazyMap = new Lazy<ImmutableDictionary<CodeFixProvider, int>>(() =>
                {
                    var priorityMap = ImmutableDictionary.CreateBuilder<CodeFixProvider, int>();

                    var fixers = ExtensionOrderer.Order(lazyFixers);
                    for (var i = 0; i < fixers.Count; i++)
                    {
                        if (!TryGetWorkspaceFixer(lazyFixers[i], workspace, logExceptionWithInfoBar: false, out var fixer))
                        {
                            continue;
                        }

                        priorityMap.Add(fixer, i);
                    }

                    return priorityMap.ToImmutable();
                }, isThreadSafe: true);

                languageMap.Add(diagnosticId, lazyMap);
            }

            return languageMap.ToImmutable();
        }

        private ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>> GetProjectFixers(Project project)
        {
            // TODO (https://github.com/dotnet/roslyn/issues/4932): Don't restrict CodeFixes in Interactive
            return project.Solution.Workspace.Kind == WorkspaceKind.Interactive
                ? ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>>.Empty
                : _projectFixersMap.GetValue(project.AnalyzerReferences, _ => ComputeProjectFixers(project));
        }

        private ImmutableDictionary<DiagnosticId, ImmutableArray<CodeFixProvider>> ComputeProjectFixers(Project project)
        {
            var extensionManager = project.Solution.Workspace.Services.GetService<IExtensionManager>();

            using var _ = PooledDictionary<DiagnosticId, ArrayBuilder<CodeFixProvider>>.GetInstance(out var builder);
            foreach (var reference in project.AnalyzerReferences)
            {
                var projectCodeFixerProvider = _analyzerReferenceToFixersMap.GetValue(reference, _createProjectCodeFixProvider);
                foreach (var fixer in projectCodeFixerProvider.GetExtensions(project.Language))
                {
                    var fixableIds = this.GetFixableDiagnosticIds(fixer, extensionManager);
                    foreach (var id in fixableIds)
                    {
                        if (string.IsNullOrWhiteSpace(id))
                            continue;

                        var list = builder.GetOrAdd(id, static _ => ArrayBuilder<CodeFixProvider>.GetInstance());
                        list.Add(fixer);
                    }
                }
            }

            return builder.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableAndFree());
        }

        private sealed class FixerComparer : IComparer<CodeFixProvider>
        {
            private readonly Dictionary<CodeFixProvider, int> _fixerToIndex;
            private readonly ImmutableDictionary<CodeFixProvider, int> _priorityMap;

            public FixerComparer(
                ArrayBuilder<CodeFixProvider> allFixers,
                ImmutableDictionary<CodeFixProvider, int> priorityMap)
            {
                _fixerToIndex = allFixers.Select((fixer, index) => (fixer, index)).ToDictionary(t => t.fixer, t => t.index);
                _priorityMap = priorityMap;
            }

            public int Compare([AllowNull] CodeFixProvider x, [AllowNull] CodeFixProvider y)
            {
                Contract.ThrowIfNull(x);
                Contract.ThrowIfNull(y);

                // If the fixers specify an explicit ordering between each other, then respect that.
                if (_priorityMap.TryGetValue(x, out var xOrder) &&
                    _priorityMap.TryGetValue(y, out var yOrder))
                {
                    var comparison = xOrder - yOrder;
                    if (comparison != 0)
                        return comparison;
                }

                // Otherwise, keep things in the same order that they were in the list (i.e. keep things stable).
                return _fixerToIndex[x] - _fixerToIndex[y];
            }
        }
    }
}
