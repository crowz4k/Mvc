// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matchers;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Mvc.Routing
{
    internal class ActionConstraintEndpointSelector : EndpointSelector
    {
        private static readonly IReadOnlyList<Endpoint> EmptyEndpoints = Array.Empty<Endpoint>();

        private readonly CompositeEndpointDataSource _dataSource;
        private readonly ActionConstraintCache _actionConstraintCache;
        private readonly ILogger _logger;

        public ActionConstraintEndpointSelector(
            CompositeEndpointDataSource dataSource,
            ActionConstraintCache actionConstraintCache,
            ILoggerFactory loggerFactory)
        {
            _dataSource = dataSource;
            _actionConstraintCache = actionConstraintCache;
            _logger = loggerFactory.CreateLogger<EndpointSelector>();
        }

        public override Task SelectAsync(
            HttpContext httpContext,
            IEndpointFeature feature,
            CandidateSet candidateSet)
        {
            if (httpContext == null)
            {
                throw new ArgumentNullException(nameof(httpContext));
            }

            if (feature == null)
            {
                throw new ArgumentNullException(nameof(feature));
            }

            if (candidateSet == null)
            {
                throw new ArgumentNullException(nameof(candidateSet));
            }

            var finalMatches = EvaluateActionConstraints(httpContext, candidateSet);

            if (finalMatches == null || finalMatches.Count == 0)
            {
                return Task.CompletedTask;
            }
            else if (finalMatches.Count == 1)
            {
                var index = finalMatches[0].index;

                var endpoint = candidateSet[index].Endpoint;
                var values = candidateSet[index].Values;

                feature.Endpoint = endpoint;
                feature.Invoker = (endpoint as MatcherEndpoint)?.Invoker;
                feature.Values = values;

                return Task.CompletedTask;
            }
            else
            {
                var endpointNames = string.Join(
                    Environment.NewLine,
                    finalMatches.Select(a => candidateSet[a.index].Endpoint.DisplayName));

                Log.MatchAmbiguous(_logger, httpContext, candidateSet, finalMatches);

                var message = Core.Resources.FormatDefaultActionSelector_AmbiguousActions(
                    Environment.NewLine,
                    string.Join(Environment.NewLine, endpointNames));
                throw new AmbiguousActionException(message);
            }
        }

        private IReadOnlyList<(int index, ActionSelectorCandidate candidate)> EvaluateActionConstraints(
            HttpContext httpContext,
            CandidateSet candidateSet)
        {
            var items = new List<(int index, ActionSelectorCandidate candidate)>();

            // We want to execute a group at a time (based on score) so keep track of the score that we've seen.
            int? score = null;

            // Perf: Avoid allocations
            for (var i = 0; i < candidateSet.Count; i++)
            {
                ref var candidate = ref candidateSet[i];
                if (candidate.IsValidCandidate)
                {
                    if (score != null && score != candidate.Score)
                    {
                        // This is the end of a group.
                        var matches = EvaluateActionConstraintsCore(httpContext, candidateSet, items, startingOrder: null);
                        if (matches.Count > 0)
                        {
                            return matches;
                        }

                        // If we didn't find matches, then reset.
                        items.Clear();
                    }

                    score = candidate.Score;

                    // If we get here, this is either the first endpoint or the we just (unsuccessfully)
                    // executed constraints for a group.
                    //
                    // So keep adding constraints.
                    var endpoint = candidate.Endpoint;
                    var actionDescriptor = endpoint.Metadata.GetMetadata<ActionDescriptor>();

                    IReadOnlyList<IActionConstraint> constraints = Array.Empty<IActionConstraint>();
                    if (actionDescriptor != null)
                    {
                        constraints = _actionConstraintCache.GetActionConstraints(httpContext, actionDescriptor);
                    }

                    // Capture the index. We need this later to look up the endpoint/route values.
                    items.Add((i, new ActionSelectorCandidate(actionDescriptor, constraints)));
                }
            }

            // Handle residue
            return EvaluateActionConstraintsCore(httpContext, candidateSet, items, startingOrder: null);
        }

        private IReadOnlyList<(int index, ActionSelectorCandidate candidate)> EvaluateActionConstraintsCore(
            HttpContext httpContext,
            CandidateSet candidateSet,
            IReadOnlyList<(int index, ActionSelectorCandidate candidate)> items,
            int? startingOrder)
        {
            // Find the next group of constraints to process. This will be the lowest value of
            // order that is higher than startingOrder.
            int? order = null;

            // Perf: Avoid allocations
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var constraints = item.candidate.Constraints;
                if (constraints != null)
                {
                    for (var j = 0; j < constraints.Count; j++)
                    {
                        var constraint = constraints[j];
                        if ((startingOrder == null || constraint.Order > startingOrder) &&
                            (order == null || constraint.Order < order))
                        {
                            order = constraint.Order;
                        }
                    }
                }
            }

            // If we don't find a next then there's nothing left to do.
            if (order == null)
            {
                return items;
            }

            // Since we have a constraint to process, bisect the set of endpoints into those with and without a
            // constraint for the current order.
            var endpointsWithConstraint = new List<(int index, ActionSelectorCandidate candidate)>();
            var endpointsWithoutConstraint = new List<(int index, ActionSelectorCandidate candidate)>();

            var constraintContext = new ActionConstraintContext();
            constraintContext.Candidates = items.Select(i => i.candidate).ToArray();

            // Perf: Avoid allocations
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var isMatch = true;
                var foundMatchingConstraint = false;

                var constraints = item.candidate.Constraints;
                if (constraints != null)
                {
                    constraintContext.CurrentCandidate = item.candidate;
                    for (var j = 0; j < constraints.Count; j++)
                    {
                        var constraint = constraints[j];
                        if (constraint.Order == order)
                        {
                            foundMatchingConstraint = true;

                            // Before we run the constraint, we need to initialize the route values.
                            // In global routing, the route values are per-endpoint.
                            constraintContext.RouteContext = new RouteContext(httpContext)
                            {
                                RouteData = new RouteData(candidateSet[item.index].Values),
                            };
                            if (!constraint.Accept(constraintContext))
                            {
                                isMatch = false;
                                break;
                            }
                        }
                    }
                }

                if (isMatch && foundMatchingConstraint)
                {
                    endpointsWithConstraint.Add(item);
                }
                else if (isMatch)
                {
                    endpointsWithoutConstraint.Add(item);
                }
            }

            // If we have matches with constraints, those are better so try to keep processing those
            if (endpointsWithConstraint.Count > 0)
            {
                var matches = EvaluateActionConstraintsCore(httpContext, candidateSet, endpointsWithConstraint, order);
                if (matches?.Count > 0)
                {
                    return matches;
                }
            }

            // If the set of matches with constraints can't work, then process the set without constraints.
            if (endpointsWithoutConstraint.Count == 0)
            {
                return null;
            }
            else
            {
                return EvaluateActionConstraintsCore(httpContext, candidateSet, endpointsWithoutConstraint, order);
            }
        }

        private static class Log
        {
            private static readonly Action<ILogger, PathString, IEnumerable<string>, Exception> _matchAmbiguous = LoggerMessage.Define<PathString, IEnumerable<string>>(
                LogLevel.Error,
                new EventId(1, "MatchAmbiguous"),
                "Request matched multiple endpoints for request path '{Path}'. Matching endpoints: {AmbiguousEndpoints}");

            public static void MatchAmbiguous(
                ILogger logger,
                HttpContext httpContext,
                CandidateSet candidateSet,
                IEnumerable<(int index, ActionSelectorCandidate candidate)> candidates)
            {
                if (logger.IsEnabled(LogLevel.Error))
                {
                    _matchAmbiguous(
                        logger,
                        httpContext.Request.Path,
                        candidates.Select(e => candidateSet[e.index].Endpoint.DisplayName),
                        null);
                }
            }
        }
    }
}
