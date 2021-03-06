﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.Operations.ControlFlow;

namespace Microsoft.CodeAnalysis.Operations.DataFlow.ParameterValidationAnalysis
{
    using ParameterValidationAnalysisData = IDictionary<AbstractLocation, ParameterValidationAbstractValue>;
    using ParameterValidationAnalysisDomain = MapAbstractDomain<AbstractLocation, ParameterValidationAbstractValue>;

    /// <summary>
    /// Dataflow analysis to track <see cref="ParameterValidationAbstractValue"/> of <see cref="AbstractLocation"/>/<see cref="IOperation"/> instances.
    /// </summary>
    internal partial class ParameterValidationAnalysis : ForwardDataFlowAnalysis<ParameterValidationAnalysisData, ParameterValidationBlockAnalysisResult, ParameterValidationAbstractValue>
    {
        public static readonly ParameterValidationAnalysisDomain ParameterValidationAnalysisDomainInstance = new ParameterValidationAnalysisDomain(ParameterValidationAbstractValueDomain.Default);

        private ParameterValidationAnalysis(ParameterValidationAnalysisDomain analysisDomain, ParameterValidationDataFlowOperationVisitor operationVisitor)
            : base(analysisDomain, operationVisitor)
        {
        }

        public static ImmutableDictionary<IParameterSymbol, SyntaxNode> GetOrComputeHazardousParameterUsages(
            IOperation topmostBlock,
            Compilation compilation,
            ISymbol owningSymbol,
            bool pessimisticAnalysis = true)
        {
            Debug.Assert(topmostBlock != null);

            var wellKnownTypeProvider = WellKnownTypeProvider.GetOrCreate(compilation);
            ParameterValidationResultWithHazardousUsages GetOrComputeLocationAnalysisResultForInvokedMethod(IBlockOperation methodTopmostBlock, IMethodSymbol method)
            {
                // getOrComputeLocationAnalysisResultOpt = null, so we do interprocedural analysis only one level down.
                Debug.Assert(methodTopmostBlock != null);
                return GetOrComputeResult(methodTopmostBlock, method, wellKnownTypeProvider, getOrComputeLocationAnalysisResultOpt: null, pessimisticAnalysis: pessimisticAnalysis);
            };

            ParameterValidationResultWithHazardousUsages result = GetOrComputeResult(topmostBlock, owningSymbol, wellKnownTypeProvider, GetOrComputeLocationAnalysisResultForInvokedMethod, pessimisticAnalysis);
            return result.HazardousParameterUsages;
        }

        private static ParameterValidationResultWithHazardousUsages GetOrComputeResult(
            IOperation topmostBlock,
            ISymbol owningSymbol,
            WellKnownTypeProvider wellKnownTypeProvider,
            Func<IBlockOperation, IMethodSymbol, ParameterValidationResultWithHazardousUsages> getOrComputeLocationAnalysisResultOpt,
            bool pessimisticAnalysis)
        {
            var cfg = ControlFlowGraph.Create(topmostBlock);
            var nullAnalysisResult = NullAnalysis.NullAnalysis.GetOrComputeResult(cfg, owningSymbol, wellKnownTypeProvider);
            var pointsToAnalysisResult = PointsToAnalysis.PointsToAnalysis.GetOrComputeResult(cfg, owningSymbol, wellKnownTypeProvider, nullAnalysisResult);
            var copyAnalysisResult = CopyAnalysis.CopyAnalysis.GetOrComputeResult(cfg, owningSymbol, wellKnownTypeProvider, nullAnalysisResultOpt: nullAnalysisResult, pointsToAnalysisResultOpt: pointsToAnalysisResult);
            // Do another null analysis pass to improve the results from PointsTo and Copy analysis.
            nullAnalysisResult = NullAnalysis.NullAnalysis.GetOrComputeResult(cfg, owningSymbol, wellKnownTypeProvider, copyAnalysisResult, pointsToAnalysisResultOpt: pointsToAnalysisResult);
            return GetOrComputeResult(cfg, owningSymbol, wellKnownTypeProvider, nullAnalysisResult, pointsToAnalysisResult, getOrComputeLocationAnalysisResultOpt, pessimisticAnalysis);
        }

        private static ParameterValidationResultWithHazardousUsages GetOrComputeResult(
            ControlFlowGraph cfg,
            ISymbol owningSymbol,
            WellKnownTypeProvider wellKnownTypeProvider,
            DataFlowAnalysisResult<NullAnalysis.NullBlockAnalysisResult, NullAnalysis.NullAbstractValue> nullAnalysisResult,
            DataFlowAnalysisResult<PointsToAnalysis.PointsToBlockAnalysisResult, PointsToAnalysis.PointsToAbstractValue> pointsToAnalysisResult,
            Func<IBlockOperation, IMethodSymbol, ParameterValidationResultWithHazardousUsages> getOrComputeLocationAnalysisResultOpt,
            bool pessimisticAnalysis)
        {
            var operationVisitor = new ParameterValidationDataFlowOperationVisitor(ParameterValidationAbstractValueDomain.Default,
                owningSymbol, wellKnownTypeProvider, getOrComputeLocationAnalysisResultOpt, nullAnalysisResult, pointsToAnalysisResult, pessimisticAnalysis);
            var analysis = new ParameterValidationAnalysis(ParameterValidationAnalysisDomainInstance, operationVisitor);
            var analysisResult = analysis.GetOrComputeResultCore(cfg, cacheResult: true);

            var newOperationVisitor = new ParameterValidationDataFlowOperationVisitor(ParameterValidationAbstractValueDomain.Default,
                    owningSymbol, wellKnownTypeProvider, getOrComputeLocationAnalysisResultOpt, nullAnalysisResult, pointsToAnalysisResult, pessimisticAnalysis, trackHazardousParameterUsages: true);
            var resultBuilder = new DataFlowAnalysisResultBuilder<ParameterValidationAnalysisData>();
            foreach (var block in cfg.Blocks)
            {
                var data = ParameterValidationAnalysisDomainInstance.Clone(analysisResult[block].InputData);
                data = Flow(newOperationVisitor, block, data);
            }

            return new ParameterValidationResultWithHazardousUsages(analysisResult, newOperationVisitor.HazardousParameterUsages);
        }

        internal override ParameterValidationBlockAnalysisResult ToResult(BasicBlock basicBlock, DataFlowAnalysisInfo<ParameterValidationAnalysisData> blockAnalysisData) => new ParameterValidationBlockAnalysisResult(basicBlock, blockAnalysisData);
    }
}
