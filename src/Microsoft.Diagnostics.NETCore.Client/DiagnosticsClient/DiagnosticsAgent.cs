// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.NETCore.Client
{
    public sealed class DiagnosticsConnectionEventArgs : EventArgs
    {
        public int ProcessId { get; set; }
        public int ClrInstanceId { get; set; }
        public DiagnosticsClient Client { get; set; }
    }

    /// <summary>
    /// This class allows you to create a transport for .NET applications to connect to.
    /// </summary>
    public class DiagnosticsAgent : IDisposable
    {
        private IpcServerTransport _server;
        private CancellationTokenSource _cts;

        /// <summary>
        /// </summary>
        /// <param name="transportAddress"> The path to where the IPC transport should be created </param>
        public DiagnosticsAgent(string transportAddress)
        {
            // create server transport
            // register events
            // - connection made event
            // - connection filter event
            _server = new IpcServerTransport(transportAddress);
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// This event is raised every time a .NET application connects to the
        /// IPC Transport.
        /// </summary>
        public event EventHandler<DiagnosticsConnectionEventArgs> OnDiagnosticsConnection;

        public async Task Connect()
        {
            // block until someone calls Disconnect
            CancellationToken token = _cts.Token;
            Task serverLoopTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        Stream connectionStream = _server.Listen();
                        Console.WriteLine("Got a Connection");
                        IpcAdvertise advertise = IpcAdvertise.Parse(connectionStream);
                        Console.WriteLine($"Parsed an advertise header {advertise}");
                        var client = new DiagnosticsClient(connectionStream);
                        var diagnosticsConnectionEventArgs = new DiagnosticsConnectionEventArgs()
                        {
                            ProcessId = (int)advertise.ProcessId,
                            ClrInstanceId = (int)advertise.ClrInstanceId,
                            Client = client
                        };
                        OnRaiseDiagnosticsConnectionEvent(diagnosticsConnectionEventArgs);
                    }
                    catch
                    {
                        // TODO
                    }
                }
            }, token);

            try
            {
                await serverLoopTask;
            }
            catch (OperationCanceledException)
            {
                // ignore cancellation
            }
        }

        public void Disconnect()
        {
            _cts.Cancel();
        }

        public void Dispose()
        {
            ((IDisposable)_server).Dispose();
            ((IDisposable)_cts).Dispose();
        }

        protected virtual void OnRaiseDiagnosticsConnectionEvent(DiagnosticsConnectionEventArgs e)
        {
            EventHandler<DiagnosticsConnectionEventArgs> handler = OnDiagnosticsConnection;

            if (handler != null)
            {
                handler(this, e);
            }
        }
    }
}