// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using Microsoft.AspNet.Server.Kestrel.Networking;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Microsoft.AspNet.Server.Kestrel.Http
{
    /// <summary>
    /// A secondary listener is delegated requests from a primary listener via a named pipe or 
    /// UNIX domain socket.
    /// </summary>
    public abstract class ListenerSecondary<T> : ListenerContext, IListenerSecondary where T : UvStreamHandle
    {
        UvPipeHandle DispatchPipe { get; set; }

        protected ListenerSecondary(IMemoryPool memory)
        {
            Memory = memory;
        }

        public Task StartAsync(
            string pipeName,
            KestrelThread thread,
            Func<Frame, Task> application)
        {
            Thread = thread;
            Application = application;

            DispatchPipe = new UvPipeHandle();

            var tcs = new TaskCompletionSource<int>();
            Thread.Post(_ =>
            {
                try
                {
                    DispatchPipe.Init(Thread.Loop, true);
                    var connect = new UvConnectRequest();
                    connect.Init(Thread.Loop);
                    connect.Connect(
                        DispatchPipe,
                        pipeName,
                        (connect2, status, error, state) =>
                        {
                            connect.Dispose();
                            if (error != null)
                            {
                                tcs.SetException(error);
                                return;
                            }

                            try
                            {
                                var ptr = Marshal.AllocHGlobal(4);
                                var buf = Thread.Loop.Libuv.buf_init(ptr, 4);

                                DispatchPipe.ReadStart(
                                    (_1, _2, _3) => buf,
                                    (_1, status2, error2, state2) =>
                                    {
                                        if (status2 == 0)
                                        {
                                            DispatchPipe.Dispose();
                                            Marshal.FreeHGlobal(ptr);
                                            return;
                                        }

                                        var acceptSocket = CreateAcceptSocket();

                                        try
                                        {
                                            DispatchPipe.Accept(acceptSocket);
                                        }
                                        catch (Exception ex)
                                        {
                                            Trace.WriteLine("DispatchPipe.Accept " + ex.Message);
                                            acceptSocket.Dispose();
                                            return;
                                        }

                                        var connection = new Connection(this, acceptSocket);
                                        connection.Start();
                                    },
                                    null);

                                tcs.SetResult(0);
                            }
                            catch (Exception ex)
                            {
                                DispatchPipe.Dispose();
                                tcs.SetException(ex);
                            }
                        },
                        null);
                }
                catch (Exception ex)
                {
                    DispatchPipe.Dispose();
                    tcs.SetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        /// <summary>
        /// Creates a socket which can be used to accept an incoming connection
        /// </summary>
        protected abstract T CreateAcceptSocket();

        public void Dispose()
        {
            // Ensure the event loop is still running.
            // If the event loop isn't running and we try to wait on this Post
            // to complete, then KestrelEngine will never be disposed and
            // the exception that stopped the event loop will never be surfaced.
            if (Thread.FatalError == null)
            {
                Thread.Send(_ => DispatchPipe.Dispose(), null);
            }
        }
    }
}
