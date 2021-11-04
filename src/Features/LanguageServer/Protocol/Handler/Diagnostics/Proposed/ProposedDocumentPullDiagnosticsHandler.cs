﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.Diagnostics.Proposed;

using DocumentDiagnosticReport = SumType<FullDocumentDiagnosticReport, UnchangedDocumentDiagnosticReport>;

// A document diagnostic partial report is defined as having the first literal send = DocumentDiagnosticReport (aka the sumtype of changed / unchanged) followed
// by n DocumentDiagnosticPartialResult literals.
// See https://github.com/microsoft/vscode-languageserver-node/blob/main/protocol/src/common/proposed.diagnostics.md#textDocument_diagnostic
using DocumentDiagnosticPartialReport = SumType<SumType<FullDocumentDiagnosticReport, UnchangedDocumentDiagnosticReport>, DocumentDiagnosticPartialResult>;

internal class ProposedDocumentPullDiagnosticsHandler : AbstractPullDiagnosticHandler<DocumentDiagnosticParams, DocumentDiagnosticPartialReport, DocumentDiagnosticReport?>
{
    private readonly IDiagnosticAnalyzerService _analyzerService;

    public ProposedDocumentPullDiagnosticsHandler(
        IDiagnosticService diagnosticService,
        IDiagnosticAnalyzerService analyzerService)
        : base(diagnosticService)
    {
        _analyzerService = analyzerService;
    }

    public override string Method => ProposedMethods.TextDocumentDiagnostic;

    public override TextDocumentIdentifier? GetTextDocumentIdentifier(DocumentDiagnosticParams diagnosticsParams) => diagnosticsParams.TextDocument;

    protected override DiagnosticTag[] ConvertTags(DiagnosticData diagnosticData)
    {
        return ConvertTags(diagnosticData, potentialDuplicate: false);
    }

    protected override DocumentDiagnosticPartialReport CreateReport(TextDocumentIdentifier identifier, VisualStudio.LanguageServer.Protocol.Diagnostic[]? diagnostics, string? resultId)
    {
        // We will only report once for document pull, so we only need to return the first literal send = DocumentDiagnosticReport.
        var report = diagnostics == null
            ? new DocumentDiagnosticReport(new UnchangedDocumentDiagnosticReport(resultId))
            : new DocumentDiagnosticReport(new FullDocumentDiagnosticReport(resultId, diagnostics));
        return report;
    }

    protected override DocumentDiagnosticReport? CreateReturn(BufferedProgress<DocumentDiagnosticPartialReport> progress)
    {
        // We only ever report one result for document diagnostics, which is the first DocumentDiagnosticReport.
        var progressValues = progress.GetValues();
        if (progressValues != null && progressValues.Length > 0)
        {
            return progressValues.Single().First;
        }

        return null;
    }

    protected override Task<ImmutableArray<DiagnosticData>> GetDiagnosticsAsync(RequestContext context, Document document, Option2<DiagnosticMode> diagnosticMode, CancellationToken cancellationToken)
    {
        return _analyzerService.GetDiagnosticsForSpanAsync(document, range: null, cancellationToken: cancellationToken);
    }

    protected override ImmutableArray<Document> GetOrderedDocuments(RequestContext context)
    {
        return DocumentPullDiagnosticHandler.GetRequestedDocument(context);
    }

    protected override PreviousResult[]? GetPreviousResults(DocumentDiagnosticParams diagnosticsParams)
    {
        if (diagnosticsParams.PreviousResultId != null && diagnosticsParams.TextDocument != null)
        {
            return new PreviousResult[]
            {
                new PreviousResult(diagnosticsParams.PreviousResultId, diagnosticsParams.TextDocument)
            };
        }

        return null;
    }
}
