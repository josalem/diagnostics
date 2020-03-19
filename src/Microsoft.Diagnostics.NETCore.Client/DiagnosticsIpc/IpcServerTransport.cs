// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;

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
                var remoteEP = PidIpcEndpoint.CreateUnixDomainSocketEndPoint(ipcTransportAddress);

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
                    var clientSocket = socket.Accept();
                    return new NetworkStream(clientSocket);
                case NamedPipeServerStream pipe:
                    pipe.WaitForConnection();
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

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
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
                disposedValue = true;
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
        }
    }
}