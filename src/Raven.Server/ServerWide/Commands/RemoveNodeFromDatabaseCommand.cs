﻿using System.Collections.Generic;
using Raven.Client.ServerWide;
using Sparrow.Json.Parsing;

namespace Raven.Server.ServerWide.Commands
{
    public class RemoveNodeFromDatabaseCommand : UpdateDatabaseCommand
    {
        public string NodeTag;
        public string DatabaseId;

        public RemoveNodeFromDatabaseCommand()
        {
        }

        public RemoveNodeFromDatabaseCommand(string databaseName, string databaseId, string uniqueRequestId) : base(databaseName, uniqueRequestId)
        {
            DatabaseId = databaseId;
        }

        public override string UpdateDatabaseRecord(DatabaseRecord record, long etag)
        {
            record.Topology.RemoveFromTopology(NodeTag);
            record.DeletionInProgress?.Remove(NodeTag);

            if (DatabaseId == null)
                return null;

            if (record.UnusedDatabaseIds == null)
                record.UnusedDatabaseIds = new HashSet<string>();

            record.UnusedDatabaseIds.Add(DatabaseId);

            return null;
        }

        public override void FillJson(DynamicJsonValue json)
        {
            json[nameof(NodeTag)] = NodeTag;
            json[nameof(RaftCommandIndex)] = RaftCommandIndex;
            json[nameof(DatabaseId)] = DatabaseId;
        }
    }
}
