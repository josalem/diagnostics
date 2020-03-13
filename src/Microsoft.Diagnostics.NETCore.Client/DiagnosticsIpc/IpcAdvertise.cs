// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Diagnostics.NETCore.Client
{
    /**
     * ==ADVERTISE PROTOCOL==
     * Before standard IPC Protocol communication can occur on a client-mode connection
     * the runtime must advertise itself over the connection.  ALL SUBSEQUENT COMMUNICATION 
     * IS STANDARD DIAGNOSTICS IPC PROTOCOL COMMUNICATION.
     * 
     * The flow for Advertise is a one-way burst of 24 bytes consisting of
     * 6 bytes - "AD_V1\0" (ASCII chars + null byte)
     * 4 bytes - CLR Instance ID (little-endian)
     * 8 bytes - PID (little-endian)
     */

    internal class IpcAdvertise
    {
        public static int Size_V1 => 18;
        public static byte[] Magic_V1 => Encoding.ASCII.GetBytes("AD_V1" + '\0');
        public static int MagicSize_V1 => 6;

        public byte[] Magic = Magic_V1;
        public UInt64 ProcessId;
        public UInt16 ClrInstanceId;

        /// <summary>
        ///
        /// </summary>
        /// <returns> (pid, clrInstanceId) </returns>
        public static IpcAdvertise Parse(Stream stream)
        {
            var binaryReader = new BinaryReader(stream);
            var advertise = new IpcAdvertise()
            {
                Magic = binaryReader.ReadBytes(Magic_V1.Length),
                ClrInstanceId = binaryReader.ReadUInt16(),
                ProcessId = binaryReader.ReadUInt64()
            };

            for (int i = 0; i < Magic_V1.Length; i++)
                if (advertise.Magic[i] != Magic_V1[i])
                    throw new Exception("Invalid advertise message from client connection");

            // FUTURE: switch on incoming magic and change if version ever increments
            return advertise;
        }

        override public string ToString()
        {
            return $"{{ Magic={Magic}; ClrInstanceId={ClrInstanceId}; ProcessId={ProcessId}; }}";
        }
    }
}