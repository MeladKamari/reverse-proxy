// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ReverseProxy.Abstractions;
using Microsoft.ReverseProxy.Common;
using Microsoft.ReverseProxy.Configuration.DependencyInjection;
using Xunit;

namespace Microsoft.ReverseProxy.Service.Tests
{
    public class DynamicConfigBuilderTests
    {
        private const string TestAddress = "https://localhost:123/";

        private IDynamicConfigBuilder CreateConfigBuilder(IClustersRepo clusters, IRoutesRepo routes, ILoggerFactory loggerFactory, Action<IReverseProxyBuilder> configProxy = null)
        {
            var servicesBuilder = new ServiceCollection();
            servicesBuilder.AddOptions();
            var proxyBuilder = servicesBuilder.AddReverseProxy();
            configProxy?.Invoke(proxyBuilder);
            servicesBuilder.AddSingleton(clusters);
            servicesBuilder.AddSingleton(routes);
            servicesBuilder.AddSingleton<TestService>();
            servicesBuilder.AddDataProtection();
            servicesBuilder.AddSingleton(loggerFactory);
            servicesBuilder.AddLogging();
            servicesBuilder.AddRouting();
            var services = servicesBuilder.BuildServiceProvider();
            return services.GetRequiredService<IDynamicConfigBuilder>();
        }

        private class TestClustersRepo : IClustersRepo
        {
            public TestClustersRepo() { }

            public TestClustersRepo(IDictionary<string, Cluster> clusters) { Clusters = clusters; }

            public IDictionary<string, Cluster> Clusters { get; set; }

            public Task<IDictionary<string, Cluster>> GetClustersAsync(CancellationToken cancellation) => Task.FromResult(Clusters);

            public Task SetClustersAsync(IDictionary<string, Cluster> clusters, CancellationToken cancellation) =>
                throw new NotImplementedException();
        }

        private class TestRoutesRepo : IRoutesRepo
        {
            public TestRoutesRepo() { }

            public TestRoutesRepo(IList<ProxyRoute> routes) { Routes = routes; }

            public IList<ProxyRoute> Routes { get; set; }

            public Task<IList<ProxyRoute>> GetRoutesAsync(CancellationToken cancellation) => Task.FromResult(Routes);

            public Task SetRoutesAsync(IList<ProxyRoute> routes, CancellationToken cancellation) =>
                throw new NotImplementedException();
        }

        private class TestService
        {
            public int CallCount { get; set; }
        }

        private TestClustersRepo CreateOneCluster()
        {
            return new TestClustersRepo(new Dictionary<string, Cluster>
            {
                {
                    "cluster1", new Cluster
                    {
                        Id = "cluster1",
                        Destinations =
                        {
                            { "d1", new Destination { Address = TestAddress } }
                        }
                    }
                }
            });
        }

        [Fact]
        public void Constructor_Works()
        {
            CreateConfigBuilder(new TestClustersRepo(), new TestRoutesRepo(), NullLoggerFactory.Instance);
        }

        [Fact]
        public async Task BuildConfigAsync_NullInput_Works()
        {
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(new TestClustersRepo(), new TestRoutesRepo(), factory);

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);


            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.Empty(result.Routes);
        }

        [Fact]
        public async Task BuildConfigAsync_EmptyInput_Works()
        {
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(new TestClustersRepo(new Dictionary<string, Cluster>()), new TestRoutesRepo(new List<ProxyRoute>()), factory);
            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.Empty(result.Routes);
        }

        [Fact]
        public async Task BuildConfigAsync_OneCluster_Works()
        {
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(CreateOneCluster(), new TestRoutesRepo(), factory);

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            // Assert
            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(result);
            Assert.Single(result.Clusters);
            var cluster = result.Clusters["cluster1"];
            Assert.NotNull(cluster);
            Assert.Equal("cluster1", cluster.Id);
            Assert.Single(cluster.Destinations);
            var destination = cluster.Destinations["d1"];
            Assert.NotNull(destination);
            Assert.Equal(TestAddress, destination.Address);
        }

        [Fact]
        public async Task BuildConfigAsync_ValidRoute_Works()
        {
            var route1 = new ProxyRoute { RouteId = "route1", Match = { Hosts = new[] { "example.com" } }, Priority = 1, ClusterId = "cluster1" };
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(new TestClustersRepo(), new TestRoutesRepo(new[] { route1 }), factory);

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.Single(result.Routes);
            Assert.Same(route1.RouteId, result.Routes[0].RouteId);
        }

        [Fact]
        public async Task BuildConfigAsync_RouteValidationError_SkipsRoute()
        {
            var route1 = new ProxyRoute { RouteId = "route1", Match = { Hosts = new[] { "invalid host name" } }, Priority = 1, ClusterId = "cluster1" };
            var configBuilder = CreateConfigBuilder(new TestClustersRepo(), new TestRoutesRepo(new[] { route1 }), NullLoggerFactory.Instance);

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.Empty(result.Routes);
        }

        [Fact]
        public async Task BuildConfigAsync_ConfigFilterRouteActions_CanFixBrokenRoute()
        {
            var route1 = new ProxyRoute { RouteId = "route1", Match = { Hosts = new[] { "invalid host name" } }, Priority = 1, ClusterId = "cluster1" };
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(new TestClustersRepo(), new TestRoutesRepo(new[] { route1 }), factory,
                proxyBuilder =>
                {
                    proxyBuilder.AddProxyConfigFilter<FixRouteHostFilter>();
                });

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.Single(result.Routes);
            var builtRoute = result.Routes[0];
            Assert.Same(route1.RouteId, builtRoute.RouteId);
            var host = Assert.Single(builtRoute.Hosts);
            Assert.Equal("example.com", host);
        }

        private class FixRouteHostFilter : IProxyConfigFilter
        {
            public Task ConfigureClusterAsync(Cluster cluster, CancellationToken cancel)
            {
                return Task.CompletedTask;
            }

            public Task ConfigureRouteAsync(ProxyRoute route, CancellationToken cancel)
            {
                route.Match.Hosts = new[] { "example.com" };
                return Task.CompletedTask;
            }
        }

        private class ClusterAndRouteFilter : IProxyConfigFilter
        {
            public Task ConfigureClusterAsync(Cluster cluster, CancellationToken cancel)
            {
                cluster.HealthCheckOptions = new HealthCheckOptions() { Enabled = true, Interval = TimeSpan.FromSeconds(12) };
                return Task.CompletedTask;
            }

            public Task ConfigureRouteAsync(ProxyRoute route, CancellationToken cancel)
            {
                route.Priority = 12;
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task BuildConfigAsync_ConfigFilterConfiguresCluster_Works()
        {
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(CreateOneCluster(), new TestRoutesRepo(), factory,
                proxyBuilder =>
                {
                    proxyBuilder.AddProxyConfigFilter<ClusterAndRouteFilter>();
                });

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(result);
            Assert.Single(result.Clusters);
            var cluster = result.Clusters["cluster1"];
            Assert.NotNull(cluster);
            Assert.True(cluster.HealthCheckOptions.Enabled);
            Assert.Equal(TimeSpan.FromSeconds(12), cluster.HealthCheckOptions.Interval);
            Assert.Single(cluster.Destinations);
            var destination = cluster.Destinations["d1"];
            Assert.NotNull(destination);
            Assert.Equal(TestAddress, destination.Address);
        }

        private class ClusterAndRouteThrows : IProxyConfigFilter
        {
            public Task ConfigureClusterAsync(Cluster cluster, CancellationToken cancel)
            {
                throw new NotFiniteNumberException("Test exception");
            }

            public Task ConfigureRouteAsync(ProxyRoute route, CancellationToken cancel)
            {
                throw new NotFiniteNumberException("Test exception");
            }
        }

        [Fact]
        public async Task BuildConfigAsync_ConfigFilterClusterActionThrows_ClusterSkipped()
        {
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(CreateOneCluster(), new TestRoutesRepo(), factory,
                proxyBuilder =>
                {
                    proxyBuilder.AddProxyConfigFilter<ClusterAndRouteThrows>();
                    proxyBuilder.AddProxyConfigFilter<ClusterAndRouteThrows>();
                });

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.NotEmpty(factory.Logger.Errors);
            Assert.IsType<NotFiniteNumberException>(factory.Logger.Errors.Single().exception);
        }

        [Fact]
        public async Task BuildConfigAsync_ConfigFilterRouteActions_Works()
        {
            var route1 = new ProxyRoute { RouteId = "route1", Match = { Hosts = new[] { "example.com" } }, Priority = 1, ClusterId = "cluster1" };
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(new TestClustersRepo(), new TestRoutesRepo(new[] { route1 }), factory,
                proxyBuilder =>
                {
                    proxyBuilder.AddProxyConfigFilter<ClusterAndRouteFilter>();
                });

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            Assert.Empty(factory.Logger.Errors);
            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.Single(result.Routes);
            Assert.Same(route1.RouteId, result.Routes[0].RouteId);
            Assert.Equal(12, route1.Priority);
        }

        [Fact]
        public async Task BuildConfigAsync_ConfigFilterRouteActionThrows_SkipsRoute()
        {
            var route1 = new ProxyRoute { RouteId = "route1", Match = { Hosts = new[] { "example.com" } }, Priority = 1, ClusterId = "cluster1" };
            var route2 = new ProxyRoute { RouteId = "route2", Match = { Hosts = new[] { "example2.com" } }, Priority = 1, ClusterId = "cluster2" };
            var factory = new TestLoggerFactory();
            var configBuilder = CreateConfigBuilder(new TestClustersRepo(), new TestRoutesRepo(new[] { route1, route2 }), factory,
                proxyBuilder =>
                {
                    proxyBuilder.AddProxyConfigFilter<ClusterAndRouteThrows>();
                    proxyBuilder.AddProxyConfigFilter<ClusterAndRouteThrows>();
                });

            var result = await configBuilder.BuildConfigAsync(CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result.Clusters);
            Assert.Empty(result.Routes);
            Assert.Equal(2, factory.Logger.Errors.Count());
            Assert.IsType<NotFiniteNumberException>(factory.Logger.Errors.First().exception);
            Assert.IsType<NotFiniteNumberException>(factory.Logger.Errors.Skip(1).First().exception);
        }
    }
}
