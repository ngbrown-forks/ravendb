﻿using System;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Raven.Client.Http;
using Raven.Server.Web;
using Sparrow.Json;

namespace Raven.Server.Documents.Handlers.Processors;

internal abstract class AbstractHandlerProxyActionProcessor<TRequestHandler, TOperationContext> : AbstractHandlerProxyProcessor<TRequestHandler, TOperationContext>
    where TRequestHandler : RequestHandler
    where TOperationContext : JsonOperationContext
{
    protected AbstractHandlerProxyActionProcessor([NotNull] TRequestHandler requestHandler, [NotNull] JsonContextPoolBase<TOperationContext> contextPool)
        : base(requestHandler, contextPool)
    {
    }

    protected abstract ValueTask ExecuteForCurrentNodeAsync();

    protected abstract Task ExecuteForRemoteNodeAsync(RavenCommand command);

    protected virtual RavenCommand CreateCommandForNode(string nodeTag) => throw new NotSupportedException($"Processor '{GetType().Name}' does not support creating commands.");

    public override async ValueTask ExecuteAsync()
    {
        if (IsCurrentNode(out var nodeTag))
        {
            await ExecuteForCurrentNodeAsync();
        }
        else
        {
            var command = CreateCommandForNode(nodeTag);
            await ExecuteForRemoteNodeAsync(command);
        }

        RequestHandler.NoContentStatus();
    }
}