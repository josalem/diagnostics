// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.Tools.RuntimeClient;
using System;
using System.IO;
using System.CommandLine;
using System.CommandLine.Builder;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal static class GCDumpCommandHandler
    {
        public static int GCDump(IConsole console, int processId, FileInfo output)
        {
            var memoryGraph = new Graphs.MemoryGraph(50_000);
            var heapInfo = new DotNetHeapInfo();
            DotNetHeapDumper.DumpEventPipe(processId, memoryGraph, Console.Out, heapInfo);
            memoryGraph.AllowReading();
            GCHeapDump.WriteMemoryGraph(memoryGraph, output.Name, "dotnet-trace");
            // memoryGraph.WriteAsBinaryFile(output.Name);
            return 0;
        }

        public static Command GCDumpCommand() =>
            new Command(
                name: "GCDump",
                description: "Dump GC",
                symbols: new Option[] {
                    CommonOptions.ProcessIdOption(),
                    OutputOption()
                },
                handler: System.CommandLine.Invocation.CommandHandler.Create<IConsole, int, FileInfo>(GCDump),
                isHidden: false
            );

        public static Option OutputOption() =>
            new Option(
                aliases: new [] { "-o", "--output" },
                description: "Output filename. Extension of target format will be added.",
                argument: new Argument<FileInfo>() { Name = "output-filename" },
                isHidden: false
            );
    }
}
