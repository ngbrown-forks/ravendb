﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Esprima.Ast;
using Lucene.Net.Search;
using Lucene.Net.Store;
using NuGet.Packaging;
using Raven.Client.Documents.Changes;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Documents.Handlers;
using Raven.Server.Documents.Includes;
using Raven.Server.Documents.Indexes.MapReduce.Static;
using Raven.Server.Documents.Indexes.Persistence.Lucene.Documents;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.AST;
using Raven.Server.Documents.Queries.Results;
using Raven.Server.Documents.Queries.Sorting.AlphaNumeric;
using Raven.Server.Documents.Queries.Timings;
using Raven.Server.Documents.ShardedHandlers.ShardedCommands;
using Raven.Server.Documents.Sharding;
using Raven.Server.Extensions;
using Raven.Server.Json;
using Raven.Server.NotificationCenter;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;
using Raven.Server.TrafficWatch;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Constants = Raven.Client.Constants;
using HttpMethod = Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpMethod;

namespace Raven.Server.Documents.ShardedHandlers
{
    public class ShardedQueriesHandler : ShardedRequestHandler
    {
        [RavenShardedAction("/databases/*/queries", "POST")]
        public Task Post()
        {
            return HandleQuery(HttpMethod.Post);
        }

        [RavenShardedAction("/databases/*/queries", "GET")]
        public Task Get()
        {
            return HandleQuery(HttpMethod.Get);
        }

        public async Task HandleQuery(HttpMethod httpMethod)
        {
            // TODO: Sharding - figure out how to report this
            var metadataOnly = GetBoolValueQueryString("metadataOnly", required: false) ?? false;
            using (var tracker = new RequestTimeTracker(HttpContext, Logger, null, "Query"))
            {
                try
                {
                    //using (var token = CreateTimeLimitedQueryToken())
                    {
                        var debug = GetStringQueryString("debug", required: false);
                        if (string.IsNullOrWhiteSpace(debug) == false)
                        {
                            throw new NotImplementedException("Not yet done");
                        }

                        // TODO: Sharding
                        //var diagnostics = GetBoolValueQueryString("diagnostics", required: false) ?? false;
                        
                        var indexQueryReader = new QueriesHandler.IndexQueryReader(GetStart(), GetPageSize(), HttpContext, RequestBodyStream(), 
                            ShardedContext.QueryMetadataCache, database: null);

                        using (ContextPool.AllocateOperationContext(out TransactionOperationContext context))
                        {
                            var indexQuery = await indexQueryReader.GetIndexQueryAsync(context, Method, tracker);
                            
                            if (TrafficWatchManager.HasRegisteredClients)
                                TrafficWatchQuery(indexQuery);
                            
                            var queryProcessor = new ShardedQueryProcessor(context, this, indexQuery);
                            
                            queryProcessor.Initialize();
                            await queryProcessor.ExecuteShardedOperations();
                            
                            var existingResultEtag = GetLongFromHeaders("If-None-Match");
                            if (existingResultEtag != null && indexQuery.Metadata.HasOrderByRandom == false)
                            {
                                if (existingResultEtag == queryProcessor.ResultsEtag)
                                {
                                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                                    return;
                                }
                            }
                            
                            // * For includes, we send the includes to all shards, then we merge them together. We do explicitly
                            //   support including from another shard, so we'll need to do that again for missing includes
                            //   That means also recording the include() call from JS on missing values that we'll need to rerun on
                            //   other shards
                            var includeTask = queryProcessor.HandleIncludes();
                            if (includeTask.IsCompleted == false)
                            {
                                await includeTask.AsTask();
                            }
                            
                            queryProcessor.MergeResults();
                            
                            // * For map/reduce - we need to re-run the reduce portion of the index again on the results
                            queryProcessor.ReduceResults();

                            // * For order by, we need to merge the results and compute the order again 
                            queryProcessor.OrderResults();

                            // * For map-reduce indexes we project the results after the reduce part 
                            queryProcessor.ProjectAfterMapReduce();

                            var result = queryProcessor.GetResult();
                            await using (var writer = new AsyncBlittableJsonTextWriter(context, ResponseBodyStream(), HttpContext.RequestAborted))
                            {
                                result.Timings = indexQuery.Timings?.ToTimings();
                                await writer.WriteDocumentQueryResultAsync(context, result, metadataOnly); 
                                await writer.OuterFlushAsync();
                            }


                            // * For JS projections and load clauses, we don't support calling load() on a
                            //   document that is not on the same shard
                        }

                    }
                }
                catch (Exception e)
                {
                    if (tracker.Query == null)
                    {
                        string errorMessage;
                        if (e is EndOfStreamException || e is ArgumentException)
                        {
                            errorMessage = "Failed: " + e.Message;
                        }
                        else
                        {
                            errorMessage = "Failed: " +
                                           HttpContext.Request.Path.Value +
                                           e.ToString();
                        }
                        tracker.Query = errorMessage;
                        if (TrafficWatchManager.HasRegisteredClients)
                            AddStringToHttpContext(errorMessage, TrafficWatchChangeType.Queries);
                    }
                    throw;
                }
            }
        }
        
        /// <summary>
        /// TrafficWatchQuery writes query data to httpContext
        /// </summary>
        /// <param name="indexQuery"></param>
        private void TrafficWatchQuery(IndexQueryServerSide indexQuery)
        {
            var sb = new StringBuilder();
            // append stringBuilder with the query
            sb.Append(indexQuery.Query);
            // if query got parameters append with parameters
            if (indexQuery.QueryParameters != null && indexQuery.QueryParameters.Count > 0)
                sb.AppendLine().Append(indexQuery.QueryParameters);
            AddStringToHttpContext(sb.ToString(), TrafficWatchChangeType.Queries);
        }

        /// <summary>
        /// A struct that we use to hold state and break the process
        /// of handling a sharded query into distinct steps
        /// </summary>
        public class ShardedQueryProcessor : IComparer<BlittableJsonReaderObject>, IDisposable
        {
            private readonly TransactionOperationContext _context;
            private readonly ShardedQueriesHandler _parent;
            private readonly IndexQueryServerSide _query;
            private readonly Dictionary<int, ShardedQueryCommand> _commands;
            private readonly HashSet<int> _filteredShardIndexes;
            private readonly ShardedQueryResult _result;
            private static readonly DynamicJsonValue DummyDynamicJsonValue = new();
            public const string OrderByMetadataField = "@order-by-fields";

            // User should also be able to define a query parameter ("Sharding.Context") which is an array
            // that contains the ids whose shards the query should be limited to. Advanced: Optimization if user
            // want to run a query and knows what shards it is on. Such as:
            // from Orders where State = $state and User = $user where all the orders are on the same share as the user

            // - TODO: Sharding - etag handling!
            public ShardedQueryProcessor(TransactionOperationContext context,ShardedQueriesHandler parent,IndexQueryServerSide query)
            {
                _context = context;
                _result = new ShardedQueryResult();
                _parent = parent;
                _query = query;
                _commands = new Dictionary<int, ShardedQueryCommand>();
                if (_query.QueryParameters != null && _query.QueryParameters.TryGet("Sharding.Context", out BlittableJsonReaderArray filter))
                {
                    _filteredShardIndexes = new HashSet<int>();
                    foreach (object item in filter)
                    {
                        _filteredShardIndexes.Add(_parent.ShardedContext.GetShardIndex(_context, item.ToString()));
                    }
                }
                else
                {
                    _filteredShardIndexes = null;
                }
            }

            public long ResultsEtag => _result.ResultEtag;

            public void Initialize()
            {
                if (_query.Metadata.IsGraph)
                {
                    // * Graph queries - not supported for sharding
                    throw new NotSupportedException("Graph queries aren't supported for sharding");
                }

                // now we have the index query, we need to process that and decide how to handle this.
                // There are a few different modes to handle:
                var queryTemplate = _query.ToJson(_context);
                if (_query.Metadata.IsCollectionQuery)
                {
                    // * For collection queries that specify ids, we can turn that into a set of loads that 
                    //   will hit the known servers

                    (List<string> ids, string _) = _query.ExtractIdsFromQuery(_parent.ServerStore, _parent.ShardedContext.DatabaseName);
                    if (ids != null)
                    {
                        GenerateLoadByIdQueries(ids, _commands);
                        return;
                    }
                }
                
                // * For paging queries, we modify the limits on the query to include all the results from all
                //   shards if there is an offset. But if there isn't an offset, we can just get the limit from
                //   each node and then merge them
                RewriteQueryForPaging(ref queryTemplate);

                // * If we have a projection in a map-reduce index,
                //   the shards will send the query result and the orchestrator will re-reduce and apply the projection
                //   in that case we must send the query without the projection
                RewriteQueryForProjection(ref queryTemplate);

                // * For collection queries that specify startsWith by id(), we need to send to all shards
                // * For collection queries without any where clause, we need to send to all shards
                // * For indexes, we sent to all shards
                for (int i = 0; i < _parent.ShardedContext.NumberOfShardNodes; i++)
                {
                    if (_filteredShardIndexes?.Contains(i) == false)
                        continue;

                    _commands[i] = new ShardedQueryCommand(_parent, queryTemplate, _query.Metadata.IndexName);
                }
            }

            private void RewriteQueryForPaging(ref BlittableJsonReaderObject queryTemplate)
            {
                if (_query.Offset is null or <= 0)
                    return;

                var clone = _query.Metadata.Query.ShallowCopy();
                clone.Offset = null; // sharded queries has to start from 0 on all nodes
                clone.Limit = new ValueExpression("__raven_limit", ValueTokenType.Parameter);
                queryTemplate.Modifications = new DynamicJsonValue(queryTemplate) { [nameof(IndexQuery.Query)] = clone.ToString() };
                DynamicJsonValue modifiedArgs;
                if (queryTemplate.TryGet(nameof(IndexQuery.QueryParameters), out BlittableJsonReaderObject args))
                {
                    modifiedArgs = new DynamicJsonValue(args);
                    args.Modifications = modifiedArgs;
                }
                else
                {
                    queryTemplate.Modifications[nameof(IndexQuery.QueryParameters)] = modifiedArgs = new DynamicJsonValue();
                }

                int limit = ((_query.Limit ?? 0) + (_query.Offset ?? 0)) * _parent.ShardedContext.NumberOfShardNodes;
                if (limit < 0) // overflow
                    limit = int.MaxValue;
                modifiedArgs["__raven_limit"] = limit;

                queryTemplate = _context.ReadObject(queryTemplate, "modified-query");
            }

            private void RewriteQueryForProjection(ref BlittableJsonReaderObject queryTemplate)
            {
                var query = _query.Metadata.Query;
                if (query.Select?.Count > 0 == false &&
                    query.SelectFunctionBody.FunctionText == null)
                    return;

                if (_query.Metadata.IndexName == null || _parent.ShardedContext.IsMapReduceIndex(_query.Metadata.IndexName) == false)
                    return;

                if (query.Load is { Count: > 0 })
                {
                    // https://issues.hibernatingrhinos.com/issue/RavenDB-17887
                    throw new NotSupportedException("Loading a document inside a projection from a map-reduce index isn't supported");
                }

                var clone = query.ShallowCopy();
                clone.Select = null;
                clone.SelectFunctionBody = default;
                clone.DeclaredFunctions = null;

                queryTemplate.Modifications = new DynamicJsonValue(queryTemplate)
                {
                    [nameof(IndexQuery.Query)] = clone.ToString()
                };

                queryTemplate = _context.ReadObject(queryTemplate, "modified-query");
            }

            private static HashSet<string> GetAllIdentifiers(NodeCollection nodeCollection, bool ancestorIsReturnStatement, Nodes currentAncestor)
            {
                HashSet<string> result = null;

                var alreadyAdded = false;
                foreach (var node in nodeCollection)
                {
                    if (alreadyAdded == false && ancestorIsReturnStatement && node is Identifier i && currentAncestor == Nodes.Property)
                    {
                        result ??= new HashSet<string>();
                        result.Add(i.Name);
                        alreadyAdded = true;
                    }

                    ancestorIsReturnStatement |= node.Type == Nodes.ReturnStatement;
                    var identifiers = GetAllIdentifiers(node.ChildNodes, ancestorIsReturnStatement, node.Type);
                    if (identifiers != null)
                    {
                        result ??= new HashSet<string>();
                        result.AddRange(identifiers);
                    }
                }

                return result;
            }

            private void GenerateLoadByIdQueries(IEnumerable<string> ids, Dictionary<int, ShardedQueryCommand> cmds)
            {
                var shards = ShardLocator.GroupIdsByShardIndex(ids, _parent.ShardedContext, _context);
                var sb = new StringBuilder();
                sb.Append("from '").Append(_query.Metadata.CollectionName).AppendLine("'")
                    .AppendLine("where id() in ($list)");
                if (_query.Metadata.Includes?.Length > 0)
                {
                    sb.Append("include ").AppendJoin(", ", _query.Metadata.Includes).AppendLine();
                }

                foreach (var (shardId, list) in shards)
                {
                    if (_filteredShardIndexes?.Contains(shardId) == false)
                        continue;
                    //TODO: have a way to turn the _query into a json file and then we'll modify that, instead of building it manually
                    var q = new DynamicJsonValue
                    {
                        [nameof(IndexQuery.QueryParameters)] = new DynamicJsonValue
                        {
                            ["list"] = list,
                        }, 
                        [nameof(IndexQuery.Query)] = sb.ToString(),
                    };
                    cmds[shardId] = new ShardedQueryCommand(_parent, _context.ReadObject(q, "query"), null);
                }
            }

            public async Task ExecuteShardedOperations()
            {
                var tasks = new List<Task>();

                foreach (var (shardIndex, cmd) in _commands)
                {
                    var task = _parent.ShardedContext.RequestExecutors[shardIndex].ExecuteAsync(cmd, cmd.Context);
                    tasks.Add(task);
                }

                await Task.WhenAll(tasks);

                foreach (var (_, cmd) in _commands)
                {
                    _result.ResultEtag = Hashing.Combine(_result.ResultEtag, cmd.Result.ResultEtag);
                    _result.IsMapReduce = cmd.Result.IsMapReduce;
                }
            }
            
            public ValueTask HandleIncludes()
            {
                HashSet<string> missing = null;
                foreach (var (_, cmd) in _commands)
                {
                    if (cmd.Result.Includes == null || cmd.Result.Includes.Count == 0)
                        continue;
                    _result.Includes ??= new List<BlittableJsonReaderObject>();
                    foreach (var id in cmd.Result.Includes.GetPropertyNames())
                    {
                        if (cmd.Result.Includes.TryGet(id, out BlittableJsonReaderObject include) && include != null)
                        {
                            _result.Includes.Add(include);
                        }
                        else
                        {
                            (missing ??= new HashSet<string>()).Add(id);
                        }
                    }
                }

                if (missing == null || missing.Count == 0)
                {
                    return ValueTask.CompletedTask;
                }
                return HandleIncludesAsync(missing);
            }

            private async ValueTask HandleIncludesAsync(HashSet<string> missing)
            {
                var includeCommands = new Dictionary<int, ShardedQueryCommand>();
                try
                {
                    GenerateLoadByIdQueries(missing, includeCommands);
                    var tasks = new List<Task>();
                    foreach (var (shardIndex, cmd) in includeCommands)
                    {
                        tasks.Add(_parent.ShardedContext.RequestExecutors[shardIndex].ExecuteAsync(cmd, cmd.Context));
                    }

                    await Task.WhenAll(tasks);
                    foreach (var (_, cmd) in includeCommands)
                    {
                        foreach (BlittableJsonReaderObject result in cmd.Result.Results)
                        {
                            _result.Includes.Add(result);
                        }
                    }
                }
                finally
                {
                    foreach (var (var, cmd) in includeCommands)
                    {
                        cmd.Dispose();
                    }
                }
            }

            public void OrderResults()
            {
                if (_query.Metadata.OrderBy?.Length > 0)
                {
                    _result.Results.Sort(this);
                }

                if (_query.Offset > 0)
                {
                    _result.Results.RemoveRange(0, _query.Offset ?? 0);
                    if (_query.Limit != null && _result.Results.Count > _query.Limit )
                    {
                        _result.Results.RemoveRange(_query.Limit.Value, _result.Results.Count - _query.Limit.Value);
                    }
                }
            }

            public void MergeResults()
            {
                _result.Results = new List<BlittableJsonReaderObject>();
                foreach (var (_, cmd) in _commands)
                {
                    _result.TotalResults += cmd.Result.TotalResults;
                    _result.IsStale |= cmd.Result.IsStale;
                    _result.SkippedResults += cmd.Result.SkippedResults;
                    _result.IndexName = cmd.Result.IndexName;
                    _result.IncludedPaths = cmd.Result.IncludedPaths;
                    if (_result.IndexTimestamp < cmd.Result.IndexTimestamp)
                    {
                        _result.IndexTimestamp = cmd.Result.IndexTimestamp;
                    }

                    if (_result.LastQueryTime < cmd.Result.LastQueryTime)
                    {
                        _result.LastQueryTime = cmd.Result.LastQueryTime;
                    }

                    foreach (BlittableJsonReaderObject item in cmd.Result.Results)
                    {
                        _result.Results.Add(item);
                    }
                }
            }

            [ThreadStatic]
            private static Random Random;

            public int Compare(BlittableJsonReaderObject x, BlittableJsonReaderObject y)
            {
                foreach (var order in _query.Metadata.OrderBy)
                {
                    var cmp = CompareField(order, GetOrderByFields(x), GetOrderByFields(y));
                    if (cmp != 0)
                        return order.Ascending ? cmp : -cmp;
                }

                BlittableJsonReaderObject GetOrderByFields(BlittableJsonReaderObject original)
                {
                    if (_result.IsMapReduce)
                        return original;

                    if (original.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) == false)
                        throw new InvalidOperationException($"Missing metadata in document. Unable to find {Constants.Documents.Metadata.Key} in {original}");

                    if (metadata.TryGet(OrderByMetadataField, out BlittableJsonReaderObject orderByFields) == false)
                        throw new InvalidOperationException($"Unable to find {OrderByMetadataField} in metadata: {metadata}");

                    return orderByFields;
                }

                return 0;
            }

            private static int CompareField(OrderByField order, BlittableJsonReaderObject x, BlittableJsonReaderObject y)
            {
                switch (order.OrderingType)
                {
                    case OrderByFieldType.Implicit:
                    case OrderByFieldType.String:
                        {
                            x.TryGet(order.Name, out string xVal);
                            y.TryGet(order.Name, out string yVal);
                            return string.Compare(xVal, yVal, StringComparison.OrdinalIgnoreCase);
                        }
                    case OrderByFieldType.Long:
                        {
                            var hasX = x.TryGetWithoutThrowingOnError(order.Name, out long xLng);
                            var hasY = y.TryGetWithoutThrowingOnError(order.Name, out long yLng);
                            if (hasX == false && hasY == false)
                                return 0;
                            if (hasX == false)
                                return 1;
                            if (hasY == false)
                                return -1;
                            return xLng.CompareTo(yLng);
                        }
                    case OrderByFieldType.Double:
                        {
                            var hasX = x.TryGetWithoutThrowingOnError(order.Name, out double xDbl);
                            var hasY = y.TryGetWithoutThrowingOnError(order.Name, out double yDbl);
                            if (hasX == false && hasY == false)
                                return 0;
                            if (hasX == false)
                                return 1;
                            if (hasY == false)
                                return -1;
                            return xDbl.CompareTo(yDbl);
                        }
                    case OrderByFieldType.AlphaNumeric:
                        {
                            x.TryGet(order.Name, out string xVal);
                            y.TryGet(order.Name, out string yVal);
                            if(xVal == null && yVal == null)
                                return 0;
                            if (xVal== null)
                                return 1;
                            if (yVal== null)
                                return -1;
                            return AlphaNumericFieldComparator.StringAlphanumComparer.Instance.Compare(xVal, yVal);
                        }
                    case OrderByFieldType.Random:
                        return (Random ??= new Random()).Next(0, int.MaxValue);
                 
                    case OrderByFieldType.Custom:
                        throw new NotSupportedException("Custom sorting is not supported in sharding as of yet");
                    case OrderByFieldType.Score:
                    case OrderByFieldType.Distance:
                    default:
                        throw new ArgumentException("Unknown OrderingType: " + order.OrderingType);
                }
            }

            public void ReduceResults()
            {
                if (_query.Metadata.IsDynamic)
                {
                    if (_query.Metadata.IsGroupBy == false)
                    {
                        return;
                    }

                    ReduceDynamicResults();
                }
                else if (_result.IsMapReduce)
                {
                    ReduceMapReduceIndexResults();
                }
            }

            private void ReduceMapReduceIndexResults()
            {
                var compiled = _parent.ShardedContext.GetCompiledMapReduceIndex(_result.IndexName, _context);
                if (compiled == null)
                    throw new IndexDoesNotExistException($"Index {_result.IndexName} doesn't exist");

                var reducingFunc = compiled.Reduce;
                var blittableToDynamicWrapper = new ReduceMapResultsOfStaticIndex.DynamicIterationOfAggregationBatchWrapper();
                blittableToDynamicWrapper.InitializeForEnumeration(_result.Results);

                var results = new List<object>();
                IPropertyAccessor propertyAccessor = null;
                foreach (var output in reducingFunc(blittableToDynamicWrapper))
                {
                    propertyAccessor ??= PropertyAccessor.Create(output.GetType(), output);
                    results.Add(output);
                }

                if (propertyAccessor == null)
                {
                    // TODO: test this
                    _result.Results.Clear();
                    return;
                }

                var objects = new AggregatedAnonymousObjects(results, propertyAccessor, _context, djv =>
                {
                    // TODO: handle metadata merge
                    // TODO: score, distance

                    djv[Constants.Documents.Metadata.Key] = DummyDynamicJsonValue;
                });

                _result.Results = objects.GetOutputsToStore().ToList();
            }

            private void ReduceDynamicResults()
            {
                var grouping = new Dictionary<GroupKey, List<BlittableJsonReaderObject>>();
                foreach (BlittableJsonReaderObject item in _result.Results)
                {
                    var parts = new List<object>();
                    foreach (var fld in _query.Metadata.GroupBy)
                    {
                        if (item.TryGet(fld.Name, out object val) == false)
                        {
                            throw new InvalidOperationException("Unable to reduce sharded query, missing field in projection: " + fld.Name);
                        }

                        parts.Add(val);
                    }

                    grouping.GetOrAdd(new GroupKey(parts)).Add(item);
                }
                
                _result.Results.Clear();
                
                foreach (var (key, items) in grouping)
                {
                    //TODO: handle metadata merge
                    var result = new DynamicJsonValue();
                    for (int i = 0; i < _query.Metadata.GroupBy.Length; i++)
                    {
                        var groupByField = _query.Metadata.GroupBy[i];
                        result[groupByField.Alias ?? groupByField.Name] = key[i];
                    }

                    foreach (var fld in _query.Metadata.SelectFields)
                    {
                        switch (fld.AggregationOperation)
                        {
                            // Count & Sum needs to be summed, since we are aggregating results here
                            case AggregationOperation.Count:
                            case AggregationOperation.Sum:
                                double val = 0;
                                foreach (var item in items)
                                {
                                    if (item.TryGet(fld.Name, out double v))
                                        val += v;
                                }

                                result[fld.Alias ?? fld.Name] = val;
                                break;
                            case AggregationOperation.None:
                                break;
                            default:
                                throw new ArgumentException("Not supported " + fld.AggregationOperation);
                        }
                    }

                    _result.Results.Add(_context.ReadObject(result, "reduced-result"));
                }
            }


            public void ProjectAfterMapReduce()
            {
                if (_result.IsMapReduce == false
                    || (_query.Metadata.Query.Select == null || _query.Metadata.Query.Select.Count == 0)
                    && _query.Metadata.Query.SelectFunctionBody.FunctionText == null)
                    return;

                var fieldsToFetch = new FieldsToFetch(_query, null);
                var retriever = new ShardedMapReduceResultRetriever(_parent.ShardedContext.ScriptRunnerCache, _query, null, fieldsToFetch, null, _context, false, null, null, null,
                    _parent.ShardedContext.IdentitySeparator);

                var currentResults = _result.Results;
                _result.Results = new List<BlittableJsonReaderObject>();

                foreach (var data in currentResults)
                {
                    (Document document, _) = retriever.GetProjectionFromDocument(new Document
                    {
                        Data = data
                    }, null, null, fieldsToFetch, _context, null, CancellationToken.None);

                    if (document == null)
                        continue;

                    var result = _context.ReadObject(document.Data, "modified-map-reduce-result");
                    _result.Results.Add(result);
                }
            }

            private class GroupKey
            {
                private readonly List<object> _items;
                private readonly int _hash;

                public object this[int i] => _items[i];
                public GroupKey(List<object> items)
                {
                    _items = items;
                    _hash = items.Count;
                    foreach (object item in items)
                    {
                        _hash = Hashing.Combine(_hash, item?.GetHashCode() ?? 0);
                    }
                }

                public override int GetHashCode()
                {
                    return _hash;
                }

                public override bool Equals(object? obj)
                {
                    if (obj is GroupKey other)
                    {
                        if (other._items.Count != _items.Count)
                            return false;
                        
                        for (int i = 0; i < _items.Count; i++)
                        {
                            if (object.Equals(_items[i], other._items[i]) == false)
                                return false;
                        }

                        return true;
                    }

                    return false;
                }
            }


            public ShardedQueryResult GetResult()
            {
                return _result;
            }

            public void Dispose()
            {
                foreach (var (_, cmd) in _commands)
                {
                    cmd.Dispose();
                }
            }
        }

        public class ShardedMapReduceResultRetriever : QueryResultRetrieverBase
        {
            public ShardedMapReduceResultRetriever(ScriptRunnerCache scriptRunnerCache, IndexQueryServerSide query, QueryTimingsScope queryTimings, FieldsToFetch fieldsToFetch, DocumentsStorage documentsStorage, JsonOperationContext context, bool reduceResults, IncludeDocumentsCommand includeDocumentsCommand, IncludeCompareExchangeValuesCommand includeCompareExchangeValuesCommand, IncludeRevisionsCommand includeRevisionsCommand, char identitySeparator) : base(scriptRunnerCache, query, queryTimings, fieldsToFetch, documentsStorage, context, reduceResults, includeDocumentsCommand, includeCompareExchangeValuesCommand, includeRevisionsCommand, identitySeparator)
            {
            }

            public override (Document Document, List<Document> List) Get(Lucene.Net.Documents.Document input, ScoreDoc scoreDoc, IState state, CancellationToken token)
            {
                throw new NotImplementedException();
            }

            public override bool TryGetKey(Lucene.Net.Documents.Document document, IState state, out string key)
            {
                throw new NotImplementedException();
            }

            protected override Document DirectGet(Lucene.Net.Documents.Document input, string id, DocumentFields fields, IState state)
            {
                throw new NotImplementedException();
            }

            protected override Document LoadDocument(string id)
            {
                throw new NotImplementedException();
            }

            protected override long? GetCounter(string docId, string name)
            {
                throw new NotImplementedException();
            }

            protected override DynamicJsonValue GetCounterRaw(string docId, string name)
            {
                throw new NotImplementedException();
            }
        }
    }
}