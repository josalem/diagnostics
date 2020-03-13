// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Tools.Common;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Rendering;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Reverse
{
    internal static class CollectCommandHandler
    {
        static ConcurrentDictionary<int, (Task, EventPipeSession)> sessionDict = new ConcurrentDictionary<int, (Task, EventPipeSession)>();
        delegate Task<int> CollectDelegate(CancellationToken ct, IConsole console, string transportPath, int processId, FileInfo output, uint buffersize, string providers, string profile, TraceFileFormat format, TimeSpan duration);

        /// <summary>
        /// Collects a diagnostic trace from a currently running process.
        /// </summary>
        /// <param name="ct">The cancellation token</param>
        /// <param name="console"></param>
        /// <param name="transportPath">A path to a named pipe on Windows or a Unix Domain Socket on Linux based systems</param>
        /// <param name="processId">The process to collect the trace from.</param>
        /// <param name="output">The output path for the collected trace data.</param>
        /// <param name="buffersize">Sets the size of the in-memory circular buffer in megabytes.</param>
        /// <param name="providers">A list of EventPipe providers to be enabled. This is in the form 'Provider[,Provider]', where Provider is in the form: 'KnownProviderName[:Flags[:Level][:KeyValueArgs]]', and KeyValueArgs is in the form: '[key1=value1][;key2=value2]'</param>
        /// <param name="profile">A named pre-defined set of provider configurations that allows common tracing scenarios to be specified succinctly.</param>
        /// <param name="format">The desired format of the created trace file.</param>
        /// <returns></returns>
        private static async Task<int> Collect(CancellationToken ct, IConsole console, string transportPath, int processId, FileInfo output, uint buffersize, string providers, string profile, TraceFileFormat format, TimeSpan duration)
        {
            DiagnosticsAgent agent = null;
            try
            {
                var clientDict = new ConcurrentDictionary<int, (int, DiagnosticsClient)>();
                using (agent = new DiagnosticsAgent(transportPath))
                {
                    agent.OnDiagnosticsConnection += (sender, eventArgs) =>
                    {
                        clientDict[eventArgs.ProcessId] = (eventArgs.ClrInstanceId, eventArgs.Client);
                        Console.WriteLine($"== New Connection: instanceId: {eventArgs.ClrInstanceId}, ProcessId: {eventArgs.ProcessId}");
                    };
                    var serverTask = agent.Connect();

                    bool shouldExit = false;
                    while (!ct.IsCancellationRequested && !shouldExit)
                    {
                        Console.Write("> ");
                        string response = Console.ReadLine().ToLowerInvariant();
                        switch(response)
                        {
                            case "list": ListConnections(clientDict); break;
                            case "trace": TraceOnConnection(clientDict); break;
                            case "stop": StopTrace(clientDict); break;
                            case "exit": shouldExit = true; break;
                            default: Console.WriteLine("Invalid command"); break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
            finally
            {
                Console.WriteLine("Exiting...");
                // agent?.Disconnect();
                // await serverTask;
                Console.WriteLine("Exited!");
            }

            return await Task.FromResult(0);
        }

        private static void ListConnections(ConcurrentDictionary<int, (int, DiagnosticsClient)> clientDict)
        {
            foreach (var (instanceId, (pid, _)) in clientDict)
                Console.WriteLine($"Connection: {{ InstanceId: {instanceId}, pid: {pid} }}");
        }

        private static void TraceOnConnection(ConcurrentDictionary<int, (int, DiagnosticsClient)> clientDict)
        {
            Console.Write("Enter instance id to trace: ");
            string input = Console.ReadLine();
            int pid = int.Parse(input);
            if (clientDict.TryGetValue(pid, out var value))
            {
                Console.WriteLine("tracing!");
                var (instanceId, client) = value;
                EventPipeSession session = client.StartEventPipeSession(new List<EventPipeProvider> { new EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Verbose) }, true);
                Stream stream = session.EventStream;
                var readTask = Task.Run(async () =>
                {
                    using (var file = File.Create($"trace_{pid}.nettrace"))
                    {
                        await stream.CopyToAsync(file);
                        // await Task.Delay(TimeSpan.FromSeconds(10));
                        // session.Stop();
                        // await copyTask;
                        Console.WriteLine("Done Tracing");
                    }
                });
                sessionDict[pid] = (readTask, session);
            }
            else
            {
                Console.WriteLine("Invalid instance id");
            }
        }

        private static async void StopTrace(ConcurrentDictionary<int, (int, DiagnosticsClient)> clientDict)
        {
            Console.Write("Enter instance id to stop: ");
            string input = Console.ReadLine();
            int instanceId = int.Parse(input);
            if (sessionDict.TryGetValue(instanceId, out var entry))
            {
                var (task, session) = entry;
                var (pid, client) = clientDict[instanceId];
                Console.WriteLine("Stopping");
                client.StopEventPipeSession(session);
                Console.WriteLine("Called stop");
                await task;
                Console.WriteLine("Stopped");
            }
            else
            {
                Console.WriteLine("Invalid instance id");
            }
        }

        private static void PrintProviders(IReadOnlyList<EventPipeProvider> providers, Dictionary<string, string> enabledBy)
        {
            Console.Out.WriteLine("");
            Console.Out.Write(String.Format("{0, -40}","Provider Name"));  // +4 is for the tab
            Console.Out.Write(String.Format("{0, -20}","Keywords"));
            Console.Out.Write(String.Format("{0, -20}","Level"));
            Console.Out.Write("Enabled By\n");
            foreach (var provider in providers)
            {
                Console.Out.WriteLine(String.Format("{0, -80}", $"{GetProviderDisplayString(provider)}") + $"{enabledBy[provider.Name]}");
            }
            Console.Out.WriteLine();
        }
        private static string GetProviderDisplayString(EventPipeProvider provider) =>
            String.Format("{0, -40}", provider.Name) + String.Format("0x{0, -18}", $"{provider.Keywords:X16}") + String.Format("{0, -8}", provider.EventLevel.ToString() + $"({(int)provider.EventLevel})");

        private static int prevBufferWidth = 0;
        private static string clearLineString = "";
        private static int lineToClear = 0;

        private static void ResetCurrentConsoleLine(bool isVTerm)
        {
            if (isVTerm)
            {
                // ANSI escape codes:
                //  [2K => clear current line
                //  [{lineToClear};0H => move cursor to column 0 of row `lineToClear`
                Console.Out.Write($"\u001b[2K\u001b[{lineToClear};0H");
            }
            else
            {
                if (prevBufferWidth != Console.BufferWidth)
                {
                    prevBufferWidth = Console.BufferWidth;
                    clearLineString = new string(' ', Console.BufferWidth - 1);
                }
                Console.SetCursorPosition(0, lineToClear);
                Console.Out.Write(clearLineString);
                Console.SetCursorPosition(0, lineToClear);
            }
        }

        private static string GetSize(long length)
        {
            if (length > 1e9)
                return String.Format("{0,-8} (GB)", $"{length / 1e9:0.00##}");
            else if (length > 1e6)
                return String.Format("{0,-8} (MB)", $"{length / 1e6:0.00##}");
            else if (length > 1e3)
                return String.Format("{0,-8} (KB)", $"{length / 1e3:0.00##}");
            else
                return String.Format("{0,-8} (B)", $"{length / 1.0:0.00##}");
        }

        public static Command CollectCommand() =>
            new Command(
                name: "collect",
                description: "Collects a diagnostic trace from a currently running process") 
            {
                // Handler
                HandlerDescriptor.FromDelegate((CollectDelegate)Collect).GetCommandHandler(),
                // Options
                CommonOptions.ProcessIdOption(),
                TransportPathOption(),
                CircularBufferOption(),
                OutputPathOption(),
                ProvidersOption(),
                ProfileOption(),
                CommonOptions.FormatOption(),
                DurationOption()
            };

        private static uint DefaultCircularBufferSizeInMB => 256;

        private static Option CircularBufferOption() =>
            new Option(
                alias: "--buffersize",
                description: $"Sets the size of the in-memory circular buffer in megabytes. Default {DefaultCircularBufferSizeInMB} MB.")
            {
                Argument = new Argument<uint>(name: "size", defaultValue: DefaultCircularBufferSizeInMB)
            };

        private static Option TransportPathOption() =>
            new Option(
                alias: "--transport-path",
                description: "A fully qualified path and filename for the OS transport to communicate over.  Supersedes the pid argument if provided.")
            {
                Argument = new Argument<string>(name: "transportPath")
            };

        public static string DefaultTraceName => "trace.nettrace";

        private static Option OutputPathOption() =>
            new Option(
                aliases: new[] { "-o", "--output" },
                description: $"The output path for the collected trace data. If not specified it defaults to '{DefaultTraceName}'.")
            {
                Argument = new Argument<FileInfo>(name: "trace-file-path", defaultValue: new FileInfo(DefaultTraceName))
            };

        private static Option ProvidersOption() =>
            new Option(
                alias: "--providers",
                description: @"A list of EventPipe providers to be enabled. This is in the form 'Provider[,Provider]', where Provider is in the form: 'KnownProviderName[:Flags[:Level][:KeyValueArgs]]', and KeyValueArgs is in the form: '[key1=value1][;key2=value2]'. These providers are in addition to any providers implied by the --profile argument. If there is any discrepancy for a particular provider, the configuration here takes precedence over the implicit configuration from the profile.")
            {
                Argument = new Argument<string>(name: "list-of-comma-separated-providers", defaultValue: "") // TODO: Can we specify an actual type?
            };

        private static Option ProfileOption() =>
            new Option(
                alias: "--profile",
                description: @"A named pre-defined set of provider configurations that allows common tracing scenarios to be specified succinctly.")
            {
                Argument = new Argument<string>(name: "profile-name", defaultValue: "")
            };

        private static Option DurationOption() =>
            new Option(
                alias: "--duration",
                description: @"When specified, will trace for the given timespan and then automatically stop the trace. Provided in the form of dd:hh:mm:ss.")
            {
                Argument = new Argument<TimeSpan>(name: "duration-timespan", defaultValue: default),
                IsHidden = true
            };
    }
}
