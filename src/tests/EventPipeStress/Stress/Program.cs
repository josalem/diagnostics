// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common;

namespace Stress
{
    class MySource : EventSource
    {
        public static MySource Log = new MySource();
        public static string s_SmallPayload = new String('a', 100);
        public static string s_BigPayload = new String('a', 10000);
        public static string s_Payload = new String('a', 100);

        public void FireSmallEvent() { WriteEvent(1, s_SmallPayload); }
        public void FireBigEvent() { WriteEvent(1, s_BigPayload); }
        public void FireEvent() => WriteEvent(1, s_Payload);
    }

    class Program
    {
        private static bool finished = false;
        private static int eventRate = -1;
        private static BurstPattern burstPattern = BurstPattern.NONE;
        private static Action threadProc = null;
        private static Action makeThreadProc(int eventCount)
        {
            Func<long> burst = BurstPatternMethods.Burst(burstPattern, eventRate, MySource.Log.FireEvent, BurstPatternMethods.BusySleepAction);
            if (eventCount != -1)
            {
                return () => { 
                    long messagesSent = 0; 
                    while (!finished && messagesSent < eventCount)
                        messagesSent += burst();
                };
            }
            else
                return () => { while (!finished) { burst(); } };
        }

        private delegate Task<int> RootCommandHandler(IConsole console, CancellationToken ct, int eventSize, int eventRate, BurstPattern burstPattern, int threads, int duration, int eventCount, int recycle);

        private static CommandLineBuilder BuildCommandLine()
        {
            var rootCommand = new RootCommand("EventPipe Stress Tester - Stress")
            {
                CommandLineOptions.EventSizeOption,
                CommandLineOptions.EventRateOption,
                CommandLineOptions.BurstPatternOption,
                CommandLineOptions.DurationOption,
                CommandLineOptions.ThreadsOption,
                CommandLineOptions.EventCountOption,
                CommandLineOptions.RecycleOption
            };


            rootCommand.Handler = CommandHandler.Create((RootCommandHandler)Run);
            return new CommandLineBuilder(rootCommand);
        }

        static async Task<int> Main(string[] args)
        {
            return await BuildCommandLine()
                .UseDefaults()
                .Build()
                .InvokeAsync(args);
        }

        private static async Task<int> Run(IConsole console, CancellationToken ct, int eventSize, int eventRate, BurstPattern burstPattern, int threads, int duration, int eventCount, int recycle)
        {
            TimeSpan durationTimeSpan = TimeSpan.FromSeconds(duration);

            MySource.s_Payload = new String('a', eventSize);

            threadProc = makeThreadProc(eventCount);

            Thread[] threadArray = new Thread[threads];
            TaskCompletionSource<bool>[] tcsArray = new TaskCompletionSource<bool>[threads];

            for (int i = 0; i < threads; i++)
            {
                var tcs = new TaskCompletionSource<bool>();
                threadArray[i] = new Thread(() => { threadProc(); tcs.TrySetResult(true); });
                tcsArray[i] = tcs;
            }

            Console.WriteLine($"SUBPROCESS :: Running - Threads: {threads}, EventSize: {eventSize * sizeof(char):N} bytes, EventCount: {(eventCount == -1 ? -1 : eventCount * threads)}, EventRate: {(eventRate == -1 ? -1 : eventRate * threads)} events/sec, duration: {durationTimeSpan.TotalSeconds}s, recycle: {recycle}");
            Console.ReadLine();

            for (int i = 0; i < threads; i++)
            {
                threadArray[i].Start();
            }

            if (eventCount != -1)
                Console.WriteLine($"SUBPROCESSS :: Sleeping for {durationTimeSpan.TotalSeconds} seconds or until {eventCount} events have been sent on each thread, whichever happens first");
            else
                Console.WriteLine($"SUBPROCESSS :: Sleeping for {durationTimeSpan.TotalSeconds} seconds");

            if (recycle > 0)
            {
                var trueThreadTotal = threads;
                // TODO: make recycle and eventcount mutually exclusive
                var durationTask = Task.Delay(durationTimeSpan);
                while (!durationTask.IsCompleted)
                {
                    // wait recycle seconds
                    await Task.Delay(TimeSpan.FromSeconds(recycle));

                    // tell all the threads to stop writing events
                    finished = true;
                    await Task.WhenAll(tcsArray.Select(tcs => tcs.Task));

                    // recreate the threads and the tcs array
                    finished = false;
                    for (int i = 0; i < threads; i++)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        threadArray[i] = new Thread(() => { threadProc(); tcs.TrySetResult(true); });
                        tcsArray[i] = tcs;
                        threadArray[i].Start();
                    }
                    trueThreadTotal += threads;
                    Console.WriteLine($"SUBPROCESS :: recycling all threads. True thread count is now {trueThreadTotal}");
                }

                finished = true;
                await Task.WhenAll(tcsArray.Select(tcs => tcs.Task));
            }
            else
            {
                Task threadCompletionTask = Task.WhenAll(tcsArray.Select(tcs => tcs.Task));
                Task result = await Task.WhenAny(Task.Delay(durationTimeSpan), threadCompletionTask);
                finished = true;
                await threadCompletionTask;
            }

            Console.WriteLine("SUBPROCESSS :: Done. Goodbye!");
            return 0;
        }
    }
}
