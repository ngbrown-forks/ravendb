﻿//-----------------------------------------------------------------------
// <copyright file="SessionDocumentTimeSeries.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Raven.Client.Documents.Session.TimeSeries;
using Raven.Client.Util;

namespace Raven.Client.Documents.Session
{
    public class SessionDocumentTimeSeries : ISessionDocumentTimeSeries
    {
        private readonly AsyncSessionDocumentTimeSeries _asyncSessionTimeSeries;

        public SessionDocumentTimeSeries(InMemoryDocumentSessionOperations session, string documentId, string name)
        {
            _asyncSessionTimeSeries = new AsyncSessionDocumentTimeSeries(session, documentId, name);
        }

        public SessionDocumentTimeSeries(InMemoryDocumentSessionOperations session, object entity, string name)
        {
            _asyncSessionTimeSeries = new AsyncSessionDocumentTimeSeries(session, entity, name);
        }

        public void Append(DateTime timestamp, IEnumerable<double> values, string tag = null)
        {
            _asyncSessionTimeSeries.Append(timestamp, values, tag);
        }

        public void Append(DateTime timestamp, double value, string tag = null)
        {
            _asyncSessionTimeSeries.Append(timestamp, value, tag);
        }

        public IEnumerable<TimeSeriesEntry> Get(DateTime? from = null, DateTime? to = null, int start = 0, int pageSize = int.MaxValue)
        {
            return AsyncHelpers.RunSync(() => _asyncSessionTimeSeries.GetAsync(from, to, start, pageSize));
        }

        public void Remove(DateTime from, DateTime to)
        {
            _asyncSessionTimeSeries.Remove(from, to);
        }

        public void Remove(DateTime at)
        {
            _asyncSessionTimeSeries.Remove(at);
        }
    }
}