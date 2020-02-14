// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Tools.Common;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Binding;
using System.CommandLine.Rendering;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Reverse
{
    internal static class CollectCommandHandler
    {
        delegate Task<int> CollectDelegate(CancellationToken ct, IConsole console, string pipeName);

        private static async Task<int> Collect(CancellationToken ct, IConsole console, string pipeName)
        {
            try
            {

                bool hasConsole = console.GetTerminal() != null;

                if (hasConsole)
                    Console.Clear();

                TimeSpan duration = default(TimeSpan);

                var shouldExit = new ManualResetEvent(false);
                var shouldStopAfterDuration = duration != default(TimeSpan);
                var failed = false;
                var terminated = false;
                var rundownRequested = false;
                System.Timers.Timer durationTimer = null;

                ct.Register(() => shouldExit.Set());

                var diagnosticsClient = new DiagnosticsClient(pipeName);
                var (session, sessionId) = diagnosticsClient.StartReversedEventPipe();
                using (VirtualTerminalMode vTermMode = VirtualTerminalMode.TryEnable())
                {
                    if (shouldStopAfterDuration)
                    {
                        durationTimer = new System.Timers.Timer(duration.TotalMilliseconds);
                        durationTimer.Elapsed += (s, e) => shouldExit.Set();
                        durationTimer.AutoReset = false;
                    }

                    var collectingTask = new Task(() =>
                    {
                        try
                        {
                            var stopwatch = new Stopwatch();
                            durationTimer?.Start();
                            stopwatch.Start();

                            using (var fs = new FileStream("reverse.nettrace", FileMode.Create, FileAccess.Write))
                            {
                                Console.Out.WriteLine($"Output File    : {fs.Name}");
                                if (shouldStopAfterDuration)
                                    Console.Out.WriteLine($"Trace Duration : {duration.ToString(@"dd\:hh\:mm\:ss")}");

                                Console.Out.WriteLine("\n\n");
                                var buffer = new byte[16 * 1024];

                                while (true)
                                {
                                    int nBytesRead = session.Read(buffer, 0, buffer.Length);
                                    if (nBytesRead <= 0)
                                        break;
                                    fs.Write(buffer, 0, nBytesRead);

                                    if (!rundownRequested)
                                    {
                                        if (hasConsole)
                                        {
                                            lineToClear = Console.CursorTop - 1;
                                            ResetCurrentConsoleLine(vTermMode.IsEnabled);
                                        }

                                        Console.Out.WriteLine($"[{stopwatch.Elapsed.ToString(@"dd\:hh\:mm\:ss")}]\tRecording trace {GetSize(fs.Length)}");
                                        Console.Out.WriteLine("Press <Enter> or <Ctrl+C> to exit...");
                                        Debug.WriteLine($"PACKET: {Convert.ToBase64String(buffer, 0, nBytesRead)} (bytes {nBytesRead})");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failed = true;
                            Console.Error.WriteLine($"[ERROR] {ex.ToString()}");
                        }
                        finally
                        {
                            terminated = true;
                            shouldExit.Set();
                        }
                    });
                    collectingTask.Start();

                    do
                    {
                        while (!Console.KeyAvailable && !shouldExit.WaitOne(250)) { }
                    } while (!shouldExit.WaitOne(0) && Console.ReadKey(true).Key != ConsoleKey.Enter);

                    if (!terminated)
                    {
                        durationTimer?.Stop();
                        if (hasConsole)
                        {
                            lineToClear = Console.CursorTop;
                            ResetCurrentConsoleLine(vTermMode.IsEnabled);
                        }
                        Console.Out.WriteLine("Stopping the trace. This may take up to minutes depending on the application being traced.");
                        rundownRequested = true;
                        diagnosticsClient.StopReversedEventPipe(sessionId);
                    }
                    await collectingTask;
                }

                Console.Out.WriteLine();
                Console.Out.WriteLine("Trace completed.");

                return failed ? 1 : 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.ToString()}");
                return 1;
            }
        }

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
                PipeNameOption()
            };
        
        private static Option PipeNameOption() =>
            new Option(
                alias: "--pipe-name",
                description: "bahahaha")
            {
                Argument = new Argument<string>(name: "pipe-name", defaultValue: "MyAwesomePipe")
            };
    }
}
