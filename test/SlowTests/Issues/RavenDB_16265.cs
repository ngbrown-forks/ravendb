﻿using System.IO;
using FastTests;
using FastTests.Server.Documents;
using Orders;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_16265 : RavenTestBase
    {
        public RavenDB_16265(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void ShouldAddCompressedFlagToPageWhenNumberOfOverFlowPagesIsSame_AfterCompressionFromNotCompressed()
        {
            var path = NewDataPath();
            if (Directory.Exists(path))
                Directory.Delete(path, true);
            using (var store = GetDocumentStore(new Options
            {
                Path = path,
                RunInMemory = false
            }))
            {
                var extension = "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";
                var firstName = "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";
                var homePhone = "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";
                var lastName = "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";

                using (var session = store.OpenSession())
                {
                    session.Store(new Employee()
                    {
                        Extension = extension,
                        FirstName = firstName,
                        HomePhone = homePhone,
                        LastName = lastName
                    }, "Employee/322");
                    session.SaveChanges();
                }
                var executor = store.GetRequestExecutor();
                using (var _ = executor.ContextPool.AllocateOperationContext(out var ctx))
                {
                    var cmd = new DocCompression.GetDocumentSize("Employee/322");
                    executor.Execute(cmd, ctx);
                    Assert.True(cmd.Result.ActualSize <= cmd.Result.AllocatedSize);
                }
                var record = store.Maintenance.Server.Send(new GetDatabaseRecordOperation(store.Database));
                record.DocumentsCompression = new DocumentsCompressionConfiguration(true, "Employees");
                store.Maintenance.Server.Send(new UpdateDatabaseOperation(record, record.Etag));

                using (var session = store.OpenSession())
                {
                    var emp = session.Load<Employee>("Employee/322");
                    emp.Title = "Patched";
                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var emp = session.Load<Employee>("Employee/322");
                    Assert.Equal("Patched", emp.Title);
                    Assert.Equal(extension, emp.Extension);
                    Assert.Equal(homePhone, emp.HomePhone);
                    Assert.Equal(lastName, emp.LastName);
                    Assert.Equal(firstName, emp.FirstName);
                }
            }
        }
    }
}