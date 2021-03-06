namespace Nancy.Routing
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.IO;
    using Extensions;
    using Responses;
    using Responses.Negotiation;

    /// <summary>
    /// Default route invoker implementation.
    /// </summary>
    public class DefaultRouteInvoker : IRouteInvoker
    {
        private readonly IEnumerable<IResponseProcessor> processors;

        private readonly IDictionary<Type, Func<dynamic, NancyContext, Response>> invocationStrategies;

        public DefaultRouteInvoker(IEnumerable<IResponseProcessor> processors)
        {
            this.processors = processors;

            this.invocationStrategies =
                new Dictionary<Type, Func<dynamic, NancyContext, Response>>
                {
                    { typeof (Response), ProcessAsRealResponse },
                    { typeof (Object), ProcessAsNegotiator },
                };
        }

        /// <summary>
        /// Invokes the specified <paramref name="route"/> with the provided <paramref name="parameters"/>.
        /// </summary>
        /// <param name="route">The route that should be invoked.</param>
        /// <param name="parameters">The parameters that the route should be invoked with.</param>
        /// <param name="context">The context of the route that is being invoked.</param>
        /// <returns>A <see cref="Response"/> intance that represents the result of the invoked route.</returns>
        public Response Invoke(Route route, DynamicDictionary parameters, NancyContext context)
        {
            var result = route.Invoke(parameters);

            if (result == null)
            {
                context.WriteTraceLog(sb => sb.AppendLine("[DefaultRouteInvoker] Invocation of route returned null"));
                result = new Response();
            }

            return this.InvokeRouteWithStrategy(result, context);
        }

        private Response InvokeRouteWithStrategy(dynamic result, NancyContext context)
        {
            var isResponse =
                (CastResultToResponse(result) != null);

            return (isResponse)
                ? ProcessAsRealResponse(result, context)
                : this.ProcessAsNegotiator(result, context);
        }

        private static Response CastResultToResponse(dynamic result)
        {
            try
            {
                return (Response)result;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        private IEnumerable<Tuple<IResponseProcessor, ProcessorMatch>> GetCompatibleProcessorsByHeader(string acceptHeader, dynamic model, NancyContext context)
        {
            var compatibleProcessors = this.processors
                .Select(processor => Tuple.Create(processor, (ProcessorMatch)processor.CanProcess(acceptHeader, model, context)))
                .Where(x => x.Item2.ModelResult != MatchResult.NoMatch)
                .Where(x => x.Item2.RequestedContentTypeResult != MatchResult.NoMatch)
                .ToList();

            return compatibleProcessors.Any() ?
                compatibleProcessors :
                null;
        }

        private static Response ProcessAsRealResponse(dynamic routeResult, NancyContext context)
        {
            context.WriteTraceLog(sb => sb.AppendLine("[DefaultRouteInvoker] Processing as real response"));

            return (Response)routeResult;
        }

        private static Response NegotiateResponse(IEnumerable<Tuple<string, IEnumerable<Tuple<IResponseProcessor, ProcessorMatch>>>> compatibleHeaders, object model, Negotiator negotiator, NancyContext context)
        {
            foreach (var compatibleHeader in compatibleHeaders)
            {
                var prioritizedProcessors = compatibleHeader.Item2
                    .OrderByDescending(x => x.Item2.ModelResult)
                    .ThenByDescending(x => x.Item2.RequestedContentTypeResult);

                foreach (var prioritizedProcessor in prioritizedProcessors)
                {
                    var processorType = prioritizedProcessor.Item1.GetType();
                    context.WriteTraceLog(sb => sb.AppendFormat("[DefaultRouteInvoker] Invoking processor: {0}\n", processorType));

                    var response =
                        SafeInvokeResponseProcessor(prioritizedProcessor.Item1, compatibleHeader.Item1, negotiator.NegotiationContext.GetModelForMediaRange(compatibleHeader.Item1), context);

                    if (response != null)
                    {
                        return response;
                    }
                }
            }

            return null;
        }

        private Response ProcessAsNegotiator(object routeResult, NancyContext context)
        {
            context.WriteTraceLog(sb => sb.AppendLine("[DefaultRouteInvoker] Processing as negotiation"));

            var negotiator =
                GetNegotiator(routeResult, context);

            context.WriteTraceLog(sb =>
                                      {
                                          var allowableFormats = negotiator.NegotiationContext
                                              .PermissableMediaRanges
                                              .Select(mr => mr.ToString())
                                              .Aggregate((t1, t2) => t1 + ", " + t2);

                                          var acceptFormants = context.Request.Headers["accept"]
                                                                              .Aggregate((t1, t2) => t1 + ", " + t2);

                                          sb.AppendFormat("[DefaultRouteInvoker] Accept header: {0}\n", acceptFormants);
                                          sb.AppendFormat("[DefaultRouteInvoker] Acceptable media ranges: {0}\n", allowableFormats);
                                      });

            var compatibleHeaders =
                this.GetCompatibleHeaders(context, negotiator);

            if (!compatibleHeaders.Any())
            {
                context.WriteTraceLog(sb => sb.AppendLine("[DefaultRouteInvoker] Unable to negotiate response - no headers compatible"));

                return new NotAcceptableResponse();
            }

            var response =
                NegotiateResponse(compatibleHeaders, routeResult, negotiator, context);

            if (response == null)
            {
                context.WriteTraceLog(sb => sb.AppendLine("[DefaultRouteInvoker] Unable to negotiate response - no processors returned valid response"));

                response = new NotAcceptableResponse();
            }

            if (compatibleHeaders.Count() > 1)
            {
                response.WithHeader("Vary", "Accept");
            }

            AddLinkHeaders(context, compatibleHeaders, response);

            if (!(response is NotAcceptableResponse))
            {
                AddNegotiatedHeaders(negotiator, response);
            }

            return response;
        }

        private static void AddNegotiatedHeaders(Negotiator negotiator, Response response)
        {
            foreach (var header in negotiator.NegotiationContext.Headers)
            {
                response.Headers[header.Key] = header.Value;
            }
        }

        private static void AddLinkHeaders(NancyContext context, IEnumerable<Tuple<string, IEnumerable<Tuple<IResponseProcessor, ProcessorMatch>>>> compatibleHeaders, Response response)
        {
            var linkProcessors = compatibleHeaders
                .SelectMany(m => m.Item2)
                .SelectMany(p => p.Item1.ExtensionMappings)
                .Where(map => !map.Item2.Matches(response.ContentType))
                .Distinct()
                .ToArray();

            if (!linkProcessors.Any())
            {
                return;
            }

            var baseUrl = context.Request.Url.BasePath + "/" + Path.GetFileNameWithoutExtension(context.Request.Url.Path);

            var links = linkProcessors.Select(lp => string.Format("<{0}.{1}>; rel=\"{2}\"", baseUrl, lp.Item1, lp.Item2))
                                      .Aggregate((lp1, lp2) => lp1 + "," + lp2);

            response.Headers["Link"] = links;
        }

        private Tuple<string, IEnumerable<Tuple<IResponseProcessor, ProcessorMatch>>>[] GetCompatibleHeaders(NancyContext context, Negotiator negotiator)
        {
            List<Tuple<string, decimal>> acceptHeaders;
            
            if (negotiator.NegotiationContext.PermissableMediaRanges.Any(mr => mr.IsWildcard))
            {
                acceptHeaders = context.Request.Headers
                    .Accept.Where(header => header.Item2 > 0m)
                    .ToList();
            }
            else
            {
                acceptHeaders = negotiator.NegotiationContext
                                          .PermissableMediaRanges
                                          .Where(header => context.Request.Headers.Accept.Any(mr => header.Matches(mr.Item1) && mr.Item2 > 0m))
                                          .Select(header => new Tuple<string, decimal>(header, 1.0m))
                                          .ToList();
            }

            return (from header in acceptHeaders
                    let compatibleProcessors = (IEnumerable<Tuple<IResponseProcessor, ProcessorMatch>>)GetCompatibleProcessorsByHeader(header.Item1, negotiator.NegotiationContext.GetModelForMediaRange(header.Item1), context)
                    where compatibleProcessors != null
                    select new Tuple<string, IEnumerable<Tuple<IResponseProcessor, ProcessorMatch>>>(
                        header.Item1,
                        compatibleProcessors
                    )).ToArray();
        }

        private static Response SafeInvokeResponseProcessor(IResponseProcessor responseProcessor, MediaRange mediaRange, object model, NancyContext context)
        {
            try
            {
                return responseProcessor.Process(mediaRange, model, context);
            }
            catch (Exception e)
            {
                context.WriteTraceLog(sb => sb.AppendFormat("[DefaultRouteInvoker] Processor threw {0} exception: {1}", e.GetType(), e.Message));
            }

            return null;
        }

        private static Negotiator GetNegotiator(object routeResult, NancyContext context)
        {
            var negotiator = routeResult as Negotiator;

            if (negotiator == null)
            {
                context.WriteTraceLog(sb => sb.AppendFormat("[DefaultRouteInvoker] Wrapping result of type {0} in negotiator\n", routeResult.GetType()));

                negotiator = new Negotiator(context);
                negotiator.WithModel(routeResult);
            }

            return negotiator;
        }
    }
}