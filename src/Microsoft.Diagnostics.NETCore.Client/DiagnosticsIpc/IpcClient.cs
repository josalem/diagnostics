﻿// Licensed to the .NET Foundation under one or more agreements.
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
    internal class IpcClient
    {
        /// <summary>
        /// Sends a single DiagnosticsIpc Message to the dotnet process with PID processId.
        /// </summary>
        /// <param name="transport">The IPC transport of the dotnet process</param>
        /// <param name="message">The DiagnosticsIpc Message to be sent</param>
        /// <returns>The response DiagnosticsIpc Message from the dotnet process</returns>
        public static IpcMessage SendMessage(IIpcEndpoint transport, IpcMessage message)
        {
            using (var stream = transport.Connect())
            {
                Write(stream, message);
                return Read(stream);
            }
        }

        /// <summary>
        /// Sends a single DiagnosticsIpc Message to the dotnet process with PID processId
        /// and returns the Stream for reuse in Optional Continuations.
        /// </summary>
        /// <param name="transport">The IPC transport of the dotnet process</param>
        /// <param name="message">The DiagnosticsIpc Message to be sent</param>
        /// <param name="response">out var for response message</param>
        /// <returns>The response DiagnosticsIpc Message from the dotnet process</returns>
        public static Stream SendMessage(IIpcEndpoint transport, IpcMessage message, out IpcMessage response)
        {
            var stream = transport.Connect();
            Write(stream, message);
            response = Read(stream);
            return stream;
        }

        private static void Write(Stream stream, byte[] buffer)
        {
            stream.Write(buffer, 0, buffer.Length);
        }

        private static void Write(Stream stream, IpcMessage message)
        {
            Write(stream, message.Serialize());
        }

        private static IpcMessage Read(Stream stream)
        {
            return IpcMessage.Parse(stream);
        }
    }
}
