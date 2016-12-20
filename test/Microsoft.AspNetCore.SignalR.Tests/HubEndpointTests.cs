﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;
using Xunit;

namespace Microsoft.AspNetCore.SignalR.Tests
{
    public class HubEndpointTests
    {
        [Fact]
        public async Task HubsAreDisposed()
        {
            var trackDispose = new TrackDispose();
            var serviceProvider = CreateServiceProvider(s => s.AddSingleton(trackDispose));
            var endPoint = serviceProvider.GetService<HubEndPoint<TestHub>>();

            using (var connectionWrapper = new ConnectionWrapper())
            {
                var endPointTask = endPoint.OnConnectedAsync(connectionWrapper.Connection);

                await connectionWrapper.HttpConnection.Input.ReadingStarted;

                // kill the connection
                connectionWrapper.Connection.Channel.Dispose();

                await endPointTask;

                Assert.Equal(2, trackDispose.DisposeCount);
            }
        }

        [Fact]
        public async Task OnDisconnectedCalledWithExceptionIfHubMethodNotFound()
        {
            var hub = Mock.Of<Hub>();

            var endPointType = GetEndPointType(hub.GetType());
            var serviceProvider = CreateServiceProvider(s =>
            {
                s.AddSingleton(endPointType);
                s.AddTransient(hub.GetType(), sp => hub);
            });

            dynamic endPoint = serviceProvider.GetService(endPointType);

            using (var connectionWrapper = new ConnectionWrapper())
            {
                var endPointTask = endPoint.OnConnectedAsync(connectionWrapper.Connection);

                await connectionWrapper.HttpConnection.Input.ReadingStarted;

                var buffer = connectionWrapper.HttpConnection.Input.Alloc();
                buffer.Write(Encoding.UTF8.GetBytes("0xdeadbeef"));
                await buffer.FlushAsync();

                connectionWrapper.Connection.Channel.Dispose();

                await endPointTask;

                Mock.Get(hub).Verify(h => h.OnDisconnectedAsync(It.IsNotNull<Exception>()), Times.Once());
            }
        }

        private static Type GetEndPointType(Type hubType)
        {
            var endPointType = typeof(HubEndPoint<>);
            return endPointType.MakeGenericType(hubType);
        }

        private class TestHub : Hub
        {
            private TrackDispose _trackDispose;

            public TestHub(TrackDispose trackDispose)
            {
                _trackDispose = trackDispose;
            }

            protected override void Dispose(bool dispose)
            {
                if (dispose)
                {
                    _trackDispose.DisposeCount++;
                }
            }
        }

        private class TrackDispose
        {
            public int DisposeCount = 0;
        }

        private IServiceProvider CreateServiceProvider(Action<ServiceCollection> addServices = null)
        {
            var services = new ServiceCollection();
            services.AddOptions()
                .AddLogging()
                .AddSignalR();

            addServices?.Invoke(services);

            return services.BuildServiceProvider();
        }

        private class ConnectionWrapper : IDisposable
        {
            private PipelineFactory _factory;
            private HttpConnection _httpConnection;

            public Connection Connection;
            public HttpConnection HttpConnection => (HttpConnection)Connection.Channel;

            public ConnectionWrapper(string format = "json")
            {
                _factory = new PipelineFactory();
                _httpConnection = new HttpConnection(_factory);

                var connectionManager = new ConnectionManager();

                Connection = connectionManager.AddNewConnection(_httpConnection).Connection;
                Connection.Metadata["formatType"] = format;
                Connection.User = new ClaimsPrincipal(new ClaimsIdentity());
            }

            public void Dispose()
            {
                Connection.Channel.Dispose();
                _httpConnection.Dispose();
                _factory.Dispose();
            }
        }
    }
}
