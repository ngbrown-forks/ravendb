﻿using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Tests.Core.Utils.Entities;
using Tests.Infrastructure.Entities;
using Xunit;
using Xunit.Abstractions;

namespace FastTests.Sharding
{
    public class BasicShardedQueryTests : ShardedTestBase
    {
        public BasicShardedQueryTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void Queries_With_LoadDocument_Should_Work()
        {
            using (var store = GetShardedDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new Company
                    {
                        Name = "Acme Inc."
                    }, "Companies/1");

                    session.Store(new Company
                    {
                        Name = "Evil Corp"
                    }, "Companies/2");

                    session.Store(new Order
                    {
                        Company = "Companies/1",
                        Lines = new List<OrderLine>
                        {
                            new OrderLine{ PricePerUnit = (decimal)1.0, Quantity = 3 },
                            new OrderLine{ PricePerUnit = (decimal)1.5, Quantity = 3 }
                        }
                    }, "orders/1$Companies/1");
                    session.Store(new Order
                    {
                        Company = "Companies/1",
                        Lines = new List<OrderLine>
                        {
                            new OrderLine{ PricePerUnit = (decimal)1.0, Quantity = 5 },
                        }
                    }, "orders/2$Companies/1");
                    session.Store(new Order
                    {
                        Company = "Companies/2",
                        Lines = new List<OrderLine>
                        {
                            new OrderLine{ PricePerUnit = (decimal)3.0, Quantity = 6, Discount = (decimal)3.5},
                            new OrderLine{ PricePerUnit = (decimal)8.0, Quantity = 3, Discount = (decimal)3.5},
                            new OrderLine{ PricePerUnit = (decimal)1.8, Quantity = 2 }
                        }
                    }, "orders/3$Companies/2");
                    session.SaveChanges();
                }

                WaitForIndexing(store, sharded: true);

                using (var session = store.OpenSession())
                {
                    var complexLinqQuery =
                        (from o in session.Query<Order>()
                         let TotalSpentOnOrder =
                             (Func<Order, decimal>)(order =>
                                 order.Lines.Sum(l => l.PricePerUnit * l.Quantity - l.Discount))
                         select new
                         {
                             OrderId = o.Id,
                             TotalMoneySpent = TotalSpentOnOrder(o),
                             CompanyName = session.Load<Company>(o.Company).Name
                         }).ToList();

                    Assert.NotEmpty(complexLinqQuery);
                    Assert.Equal(3, complexLinqQuery.Count);
                    Assert.DoesNotContain(complexLinqQuery, item => item == null);

                    foreach (var item in complexLinqQuery)
                    {
                        Assert.True((string)item.CompanyName == "Acme Inc." || (string)item.CompanyName == "Evil Corp");
                    }
                }
            }
        }

        [Fact]
        public void Query_With_Customize()
        {
            using (var store = GetShardedDocumentStore())
            {
                new DogsIndex().Execute(store);

                using (var session = store.OpenSession())
                {
                    session.Store(new Dog { Name = "Snoopy", Breed = "Beagle", Color = "White", Age = 6, IsVaccinated = true }, "dogs/1");
                    session.Store(new Dog { Name = "Brian", Breed = "Labrador", Color = "White", Age = 12, IsVaccinated = false }, "dogs/2");
                    session.Store(new Dog { Name = "Django", Breed = "Jack Russel", Color = "Black", Age = 3, IsVaccinated = true }, "dogs/3");
                    session.Store(new Dog { Name = "Beethoven", Breed = "St. Bernard", Color = "Brown", Age = 1, IsVaccinated = false }, "dogs/4");
                    session.Store(new Dog { Name = "Scooby Doo", Breed = "Great Dane", Color = "Brown", Age = 0, IsVaccinated = false }, "dogs/5");
                    session.Store(new Dog { Name = "Old Yeller", Breed = "Black Mouth Cur", Color = "White", Age = 2, IsVaccinated = true }, "dogs/6");
                    session.Store(new Dog { Name = "Benji", Breed = "Mixed", Color = "White", Age = 0, IsVaccinated = false }, "dogs/7");
                    session.Store(new Dog { Name = "Lassie", Breed = "Collie", Color = "Brown", Age = 6, IsVaccinated = true }, "dogs/8");

                    session.SaveChanges();
                }

                using (var session = store.OpenSession())
                {
                    var queryResult = session.Query<DogsIndex.Result, DogsIndex>()
                        .Customize(x => x.WaitForNonStaleResults())
                        .OrderBy(x => x.Name, OrderingType.AlphaNumeric)
                        .Where(x => x.Age > 2)
                        .ToList();

                    Assert.Equal(queryResult[0].Name, "Brian");
                    Assert.Equal(queryResult[1].Name, "Django");
                    Assert.Equal(queryResult[2].Name, "Lassie");
                    Assert.Equal(queryResult[3].Name, "Snoopy");
                }
            }
        }

        [Fact]
        public void Simple_Projection_With_Order_By()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapIndex());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Grisha", Age = 1 }, "users/1");
                    session.Store(new User { Name = "Igal", Age = 2 }, "users/2");
                    session.Store(new User { Name = "Egor", Age = 3 }, "users/3");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = session.Query<UserMapIndex.Result, UserMapIndex>()
                        .OrderBy(x => x.Name)
                        .As<User>()
                        .Select(x => new
                        {
                            x.Age
                        })
                        .ToList();

                    Assert.Equal(3, queryResult.Count);
                    Assert.Equal(3, queryResult[0].Age);
                    Assert.Equal(1, queryResult[1].Age);
                    Assert.Equal(2, queryResult[2].Age);
                }
            }
        }

        [Fact]
        public void Simple_Projection_With_Order_By2()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapIndex());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Grisha", Age = 1 }, "users/1");
                    session.Store(new User { Name = "Igal", Age = 2 }, "users/2");
                    session.Store(new User { Name = "Egor", Age = 3 }, "users/3");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = (from user in session.Query<User, UserMapIndex>()
                        let age = user.Age
                        orderby user.Name
                        select new AgeResult
                        {
                            Age = age
                        })
                        .ToList();

                    Assert.Equal(3, queryResult.Count);
                    Assert.Equal(3, queryResult[0].Age);
                    Assert.Equal(1, queryResult[1].Age);
                    Assert.Equal(2, queryResult[2].Age);
                }
            }
        }

        [Fact]
        public void Simple_Projection_With_Order_By_And_Raw_Query()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapIndex());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Grisha", Age = 1 }, "users/1");
                    session.Store(new User { Name = "Igal", Age = 2 }, "users/2");
                    session.Store(new User { Name = "Egor", Age = 3 }, "users/3");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = (session.Advanced.RawQuery<AgeResult>(@$"from index {new UserMapIndex().IndexName} as user
order by user.Name
select {{
    Age: user.Age
}}
")).ToList();

                    Assert.Equal(3, queryResult.Count);
                    Assert.Equal(3, queryResult[0].Age);
                    Assert.Equal(1, queryResult[1].Age);
                    Assert.Equal(2, queryResult[2].Age);
                }
            }
        }

        [Fact]
        public void Simple_Map_Reduce()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapReduce());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Jane", Count = 1 }, "users/1");
                    session.Store(new User { Name = "Jane", Count = 2 }, "users/2");
                    session.Store(new User { Name = "Jane", Count = 3 }, "users/3");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = session.Query<UserMapReduce.Result, UserMapReduce>()
                        .ToList();

                    Assert.Equal(1, queryResult.Count);
                    Assert.Equal(6, queryResult[0].Sum);
                }
            }
        }

        [Fact]
        public void Simple_Map_Reduce_With_Order_By()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new OrderMapIndex());

                using (var session = store.OpenSession())
                {
                    session.Store(new Order
                    {
                        Freight = 20m,
                        Lines = new List<OrderLine>
                        {
                            new()
                            {
                                Discount = 0.2m
                            },
                            new()
                            {
                                Discount = 0.4m
                            }
                        }
                    });
                    session.Store(new Order
                    {
                        Freight = 10m,
                        Lines = new List<OrderLine>
                        {
                            new()
                            {
                                Discount = 0.3m
                            },
                            new()
                            {
                                Discount = 0.5m
                            }
                        }
                    });
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = session.Advanced.RawQuery<OrderLine>(
                            @"
declare function project(o) {
    return o.Lines;
}

from index 'OrderMapIndex' as o
order by o.Freight
                    select project(o)")
                        .ToList();

                    Assert.Equal(4, queryResult.Count);
                    Assert.Equal(0.3m, queryResult[0].Discount);
                    Assert.Equal(0.5m, queryResult[1].Discount);
                    Assert.Equal(0.2m, queryResult[2].Discount);
                    Assert.Equal(0.4m, queryResult[3].Discount);
                }
            }
        }

        [Fact]
        public void Simple_Map_Reduce_With_Order_By_And_Projection()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapReduce());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Grisha", Count = 1 }, "users/1");
                    session.Store(new User { Name = "Igal", Count = 2 }, "users/2");
                    session.Store(new User { Name = "Egor", Count = 3 }, "users/3");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = session.Query<UserMapReduce.Result, UserMapReduce>()
                        .OrderBy(x => x.Name)
                        .Select(x => new
                        {
                            x.Sum
                        })
                        .ToList();

                    Assert.Equal(3, queryResult.Count);
                    Assert.Equal(3, queryResult[0].Sum);
                    Assert.Equal(1, queryResult[1].Sum);
                    Assert.Equal(2, queryResult[2].Sum);
                }
            }
        }

        [Fact]
        public void Simple_Map_Reduce_With_Order_By_And_Projection2()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapReduce());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Grisha", Count = 1 }, "users/1");
                    session.Store(new User { Name = "Igal", Count = 2 }, "users/2");
                    session.Store(new User { Name = "Egor", Count = 3 }, "users/3");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = (from user in session.Query<UserMapReduce.Result, UserMapReduce>()
                            let sum = user.Sum + 1
                            let name = user.Name + "_" + user.Name
                            orderby user.Sum
                            select new
                            {
                                Sum = sum,
                                Name = name
                            })
                        .ToList();

                    Assert.Equal(3, queryResult.Count);
                    Assert.Equal(2, queryResult[0].Sum);
                    Assert.Equal("Grisha_Grisha", queryResult[0].Name);
                    Assert.Equal(3, queryResult[1].Sum);
                    Assert.Equal("Igal_Igal", queryResult[1].Name);
                    Assert.Equal(4, queryResult[2].Sum);
                    Assert.Equal("Egor_Egor", queryResult[2].Name);
                }
            }
        }

        [Fact]
        public void Simple_Map_Reduce_With_Order_By_Projecting_New_Fields()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapReduce());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Grisha", Count = 1 }, "users/1");
                    session.Store(new User { Name = "Igal", Count = 2 }, "users/2");
                    session.Store(new User { Name = "Egor", Count = 3 }, "users/3");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var queryResult = (from user in session.Query<UserMapReduce.Result, UserMapReduce>()
                            let sum = user.Sum
                            orderby user.Sum
                            select new
                            {
                                Sum = sum
                            })
                        .ToList();

                    Assert.Equal(3, queryResult.Count);
                    Assert.Equal(1, queryResult[0].Sum);
                    Assert.Equal(2, queryResult[1].Sum);
                    Assert.Equal(3, queryResult[2].Sum);
                }
            }
        }

        [Fact]
        public void Query_An_Index_That_Doesnt_Exist()
        {
            using (var store = GetShardedDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    Assert.Throws<IndexDoesNotExistException>(() => session.Query<UserMapReduce.Result, UserMapReduce>().ToList());
                }
            }
        }

        [Fact]
        public void Map_Reduce_Projection_With_Load_Not_Supported()
        {
            using (var store = GetShardedDocumentStore())
            {
                store.ExecuteIndex(new UserMapReduce());

                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Grisha", Count = 1 }, "users/1");
                    session.SaveChanges();

                    WaitForIndexing(store, sharded: true);

                    var exception = Assert.Throws<RavenException>(() => (from user in session.Query<UserMapReduce.Result, UserMapReduce>()
                            let anotherUser = RavenQuery.Load<User>(user.Name)
                            select new
                            {
                                Name = anotherUser.Name
                            })
                        .ToList());

                    Assert.Contains(nameof(NotSupportedException), exception.Message);
                }
            }
        }

        private class AgeResult
        {
            public int Age { get; set; }
        }

        public class Dog
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Breed { get; set; }
            public string Color { get; set; }
            public int Age { get; set; }
            public bool IsVaccinated { get; set; }
        }

        public class DogsIndex : AbstractIndexCreationTask<Dog>
        {
            public class Result
            {
                public string Name { get; set; }
                public int Age { get; set; }
                public bool IsVaccinated { get; set; }
            }

            public DogsIndex()
            {
                Map = dogs => from dog in dogs
                              select new
                              {
                                  dog.Name,
                                  dog.Age,
                                  dog.IsVaccinated
                              };

                //TODO: remove when we have the fields from the index
                StoreAllFields(FieldStorage.Yes);
            }
        }

        public class UserMapIndex : AbstractIndexCreationTask<User>
        {
            public class Result
            {
                public string Name;
            }

            public UserMapIndex()
            {
                Map = users =>
                    from user in users
                    select new Result
                    {
                        Name = user.Name
                    };

                //TODO: remove when we have the fields from the index
                StoreAllFields(FieldStorage.Yes);
            }
        }

        public class OrderMapIndex : AbstractIndexCreationTask<Order>
        {
            public class Result
            {
                public decimal Freight;
            }

            public OrderMapIndex()
            {
                Map = orders =>
                    from order in orders
                    select new Result
                    {
                        Freight = order.Freight
                    };

                //TODO: remove when we have the fields from the index
                StoreAllFields(FieldStorage.Yes);
            }
        }

        public class UserMapReduce : AbstractIndexCreationTask<User, UserMapReduce.Result>
        {
            public class Result
            {
                public string Name;
                public int Sum;
            }

            public UserMapReduce()
            {
                Map = users =>
                    from user in users
                    select new Result
                    {
                        Name = user.Name,
                        Sum = user.Count
                    };

                Reduce = results =>
                    from result in results
                    group result by result.Name
                    into g
                    select new Result
                    {
                        Name = g.Key,
                        Sum = g.Sum(x => x.Sum)
                    };

                //TODO: remove when we have the fields from the index
                StoreAllFields(FieldStorage.Yes);
            }
        }
    }
}