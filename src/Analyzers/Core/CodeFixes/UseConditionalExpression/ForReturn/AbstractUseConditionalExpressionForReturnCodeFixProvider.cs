﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.UseConditionalExpression.UseConditionalExpressionCodeFixHelpers;

namespace Microsoft.CodeAnalysis.UseConditionalExpression
{
    internal abstract class AbstractUseConditionalExpressionForReturnCodeFixProvider<
        TStatementSyntax,
        TIfStatementSyntax,
        TExpressionSyntax,
        TConditionalExpressionSyntax>
        : AbstractUseConditionalExpressionCodeFixProvider<TStatementSyntax, TIfStatementSyntax, TExpressionSyntax, TConditionalExpressionSyntax>
        where TStatementSyntax : SyntaxNode
        where TIfStatementSyntax : TStatementSyntax
        where TExpressionSyntax : SyntaxNode
        where TConditionalExpressionSyntax : TExpressionSyntax
    {
        private readonly Func<IReturnOperation?, bool> _returnIsRef;

        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(IDEDiagnosticIds.UseConditionalExpressionForReturnDiagnosticId);

        protected AbstractUseConditionalExpressionForReturnCodeFixProvider()
            => _returnIsRef = IsRef;

        protected abstract bool IsRef(IReturnOperation? returnOperation);

        public override Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            context.RegisterCodeFix(
                new MyCodeAction(c => FixAsync(context.Document, context.Diagnostics.First(), c)),
                context.Diagnostics);
            return Task.CompletedTask;
        }

        protected override async Task FixOneAsync(
            Document document, Diagnostic diagnostic,
            SyntaxEditor editor, CancellationToken cancellationToken)
        {
            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            var ifStatement = (TIfStatementSyntax)diagnostic.AdditionalLocations[0].FindNode(cancellationToken);

            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var ifOperation = (IConditionalOperation)semanticModel.GetOperation(ifStatement)!;

            if (!UseConditionalExpressionForReturnHelpers.TryMatchPattern(
                    syntaxFacts, ifOperation, _returnIsRef,
                    out var trueStatement, out var falseStatement,
                    out var trueReturn, out var trueThrow,
                    out var falseReturn, out var falseThrow))
            {
                return;
            }

            var conditionalExpression = await CreateConditionalExpressionAsync(
                document, ifOperation,
                trueStatement, falseStatement,
                trueReturn?.ReturnedValue ?? trueThrow?.Exception,
                falseReturn?.ReturnedValue ?? falseThrow?.Exception,
                IsRef((trueReturn ?? falseReturn)!), cancellationToken).ConfigureAwait(false);

            var generatorInternal = document.GetRequiredLanguageService<SyntaxGeneratorInternal>();
            var returnStatement = trueReturn?.Kind == OperationKind.YieldReturn || falseReturn?.Kind == OperationKind.YieldReturn
                ? (TStatementSyntax)generatorInternal.YieldReturnStatement(conditionalExpression)
                : (TStatementSyntax)editor.Generator.ReturnStatement(conditionalExpression);

            returnStatement = returnStatement.WithTriviaFrom(ifStatement);

            editor.ReplaceNode(
                ifStatement,
                WrapWithBlockIfAppropriate(ifStatement, returnStatement));

            // if the if-statement had no 'else' clause, then we were using the following statement
            // as the 'false' statement.  If so, remove it explicitly.
            if (ifOperation.WhenFalse == null)
            {
                editor.RemoveNode(falseStatement.Syntax, GetRemoveOptions(syntaxFacts, falseStatement.Syntax));
            }
        }

        private class MyCodeAction : CustomCodeActions.DocumentChangeAction
        {
            public MyCodeAction(Func<CancellationToken, Task<Document>> createChangedDocument)
                : base(AnalyzersResources.Convert_to_conditional_expression, createChangedDocument, IDEDiagnosticIds.UseConditionalExpressionForReturnDiagnosticId)
            {
            }
        }
    }
}
