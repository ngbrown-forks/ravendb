﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Server.Documents.Handlers.Processors.Databases;
using Raven.Server.ServerWide.Context;
using Raven.Server.Web;
using Sparrow.Json;

namespace Raven.Server.Documents.Handlers.Processors.Revisions;

internal abstract class AbstractRevisionsHandlerProcessorForPostRevisionsConfiguration<TRequestHandler> : AbstractHandlerProcessorForUpdateDatabaseConfiguration<BlittableJsonReaderObject, TRequestHandler>
    where TRequestHandler : RequestHandler
{
    protected AbstractRevisionsHandlerProcessorForPostRevisionsConfiguration([NotNull] TRequestHandler requestHandler)
        : base(requestHandler)
    {
    }

    protected override Task<(long Index, object Result)> OnUpdateConfiguration(TransactionOperationContext context, string databaseName, BlittableJsonReaderObject configuration, string raftRequestId)
    {
        return RequestHandler.ServerStore.ModifyDatabaseRevisions(context, databaseName, configuration, raftRequestId);
    }

    protected override void OnBeforeUpdateConfiguration(ref BlittableJsonReaderObject configuration, JsonOperationContext context)
    {
        if (configuration == null ||
            configuration.TryGet(nameof(RevisionsConfiguration.Collections), out BlittableJsonReaderObject collections) == false ||
            collections?.Count > 0 == false)
            return;

        var uniqueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prop = new BlittableJsonReaderObject.PropertyDetails();

        for (var i = 0; i < collections.Count; i++)
        {
            collections.GetPropertyByIndex(i, ref prop);

            if (uniqueKeys.Add(prop.Name) == false)
            {
                throw new InvalidOperationException("Cannot have two different revision configurations on the same collection. " +
                                                    $"Collection name : '{prop.Name}'");
            }
        }
    }
}