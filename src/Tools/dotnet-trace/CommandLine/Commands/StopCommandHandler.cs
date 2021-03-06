﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.Tools.RuntimeClient;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal static class StopCommandHandler
    {
        public static async Task<int> Stop(IConsole console, int processId, ulong sessionId)
        {
            try
            {
                EventPipeClient.StopTracing(processId, sessionId);

                await Task.FromResult(0);
                return sessionId != 0 ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.ToString()}");
                return 1;
            }
        }

        public static Command StopCommand() =>
            new Command(
                name: "stop",
                description: "Stops an EventPipe session.",
                symbols: new Option[] {
                    CommonOptions.ProcessIdOption(),
                    SessionIdOption(),
                },
                handler: CommandHandler.Create<IConsole, int, ulong>(Stop),
                isHidden: true);

        private static Option SessionIdOption() =>
            new Option(
                new[] { "--session-id" },
                @"Session Id being recorded.",
                new Argument<ulong> { Name = "SessionId" });
    }
}
