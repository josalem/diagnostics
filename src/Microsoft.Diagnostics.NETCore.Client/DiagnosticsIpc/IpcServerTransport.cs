// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Microsoft.Diagnostics.NETCore.Client
{
    internal class IpcServerTransport : IDisposable
    {
        private string _ipcTransportAddress = null;

        // _server ::= Socket | NamedPipeServerStream
        private object _server = null;

        /// <summary>
        /// Creates a reference to a .NET process's IPC Transport
        /// by getting a reference to the Named Pipe or Socket
        /// specified in ipcTransportPath
        /// </summary>
        /// <param name="ipcTransportAddress">The fully qualified path representing a transport</param>
        /// <returns>A reference to the IPC Transport</returns>
        public IpcServerTransport(string ipcTransportAddress)
        {
            _ipcTransportAddress = ipcTransportAddress;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _server = GetNewNamedPipeServer();
            }
            else
            {
                var remoteEP = IpcTransport.CreateUnixDomainSocketEndPoint(ipcTransportAddress);

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Bind(remoteEP);
                socket.Listen(255);
                socket.LingerState.Enabled = false;
                _server = socket;
            }
        }

        public Stream Listen()
        {
            switch (_server)
            {
                case Socket socket: 
                    Console.WriteLine("Accepting");
                    var clientSocket = socket.Accept();
                    Console.WriteLine("Accept");
                    return new NetworkStream(clientSocket);
                case NamedPipeServerStream pipe:
                    Console.WriteLine("Waiting for connection...");
                    pipe.WaitForConnection();
                    Console.WriteLine("Got a connection!");
                    _server = GetNewNamedPipeServer();
                    return pipe;
                default:
                    throw new DiagnosticsClientException("Unable to listen on Diagnostics Agent Transport");
            };
        }

        private NamedPipeServerStream GetNewNamedPipeServer()
        {
            var normalizedPath = _ipcTransportAddress.StartsWith(@"\\.\pipe\") ? _ipcTransportAddress.Substring(9) : _ipcTransportAddress;
            return new NamedPipeServerStream(normalizedPath, PipeDirection.InOut, 10);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    switch (_server)
                    {
                        case Socket socket:
                            try
                            {
                                socket.Shutdown(SocketShutdown.Both);
                            }
                            catch {}
                            finally
                            {
                                socket.Close(0);
                            }
                            socket.Dispose();
                            if (File.Exists(_ipcTransportAddress))
                                File.Delete(_ipcTransportAddress);
                            break;
                        case NamedPipeServerStream stream: stream.Dispose(); break;
                        default: break;
                    }
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        void IDisposable.Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }
        #endregion
    }
}