// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal enum TraceFileFormat { NetTrace, Speedscope, Cachegrind };

    internal static class TraceFileFormatConverter
    {
        private static Dictionary<TraceFileFormat, string> TraceFileFormatExtensions = new Dictionary<TraceFileFormat, string>() {
            { TraceFileFormat.NetTrace,     "nettrace" },
            { TraceFileFormat.Speedscope,   "speedscope.json" },
            { TraceFileFormat.Cachegrind,   "callgrind" }
        };

        public static void ConvertToFormat(TraceFileFormat format, string fileToConvert, string outputFilename = "")
        {
            if (string.IsNullOrWhiteSpace(outputFilename))
                outputFilename = fileToConvert;

            outputFilename = Path.ChangeExtension(outputFilename, TraceFileFormatExtensions[format]);
            Console.Out.WriteLine($"Writing:\t{outputFilename}");

            switch (format)
            {
                case TraceFileFormat.NetTrace:
                    break;
                case TraceFileFormat.Speedscope:
                    ConvertToSpeedscope(fileToConvert, outputFilename);
                    break;
                case TraceFileFormat.Cachegrind:
                    ConvertToCachegrind(fileToConvert, outputFilename);
                    break;
                default:
                    // Validation happened way before this, so we should never reach this...
                    throw new ArgumentException($"Invalid TraceFileFormat \"{format}\"");
            }
            Console.Out.WriteLine("Conversion complete");
        }

        private static void ConvertToCachegrind(string fileToConvert, string outputFilename)
        {
            var etlxFilePath = TraceLog.CreateFromEventPipeDataFile(fileToConvert);
            using (var symbolReader = new SymbolReader(System.IO.TextWriter.Null) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath })
            using (var eventLog = new TraceLog(etlxFilePath))
            {
                var stackSource = new MutableTraceEventStackSource(eventLog)
                {
                    OnlyManagedCodeStacks = true // EventPipe currently only has managed code stacks.
                };

                var computer = new SampleProfilerThreadTimeComputer(eventLog, symbolReader);
                computer.GenerateThreadTimeStacks(stackSource);

                CallgrindStackSourceWriter.WriteStackViewAsCallgrind(stackSource, outputFilename);
            }

            if (File.Exists(etlxFilePath))
            {
                File.Delete(etlxFilePath);
            }
        }

        private static void ConvertToSpeedscope(string fileToConvert, string outputFilename)
        {
            var etlxFilePath = TraceLog.CreateFromEventPipeDataFile(fileToConvert);
            using (var symbolReader = new SymbolReader(System.IO.TextWriter.Null) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath })
            using (var eventLog = new TraceLog(etlxFilePath))
            {
                var stackSource = new MutableTraceEventStackSource(eventLog)
                {
                    OnlyManagedCodeStacks = true // EventPipe currently only has managed code stacks.
                };

                var computer = new SampleProfilerThreadTimeComputer(eventLog, symbolReader);
                computer.GenerateThreadTimeStacks(stackSource);

                SpeedScopeStackSourceWriter.WriteStackViewAsJson(stackSource, outputFilename);
            }

            if (File.Exists(etlxFilePath))
            {
                File.Delete(etlxFilePath);
            }
        }

        internal static class CallgrindStackSourceWriter
        {
            /// <summary>
            /// Exports provided StackSource to the Callgrind format.
            /// Format documentation: http://valgrind.org/docs/manual/cl-format.html
            /// </summary>
            public static void WriteStackViewAsCallgrind(StackSource source, string filePath)
            {
                if (File.Exists(filePath))
                    File.Delete(filePath);

                using (var writeStream = File.CreateText(filePath))
                    Export(source, writeStream, Path.GetFileNameWithoutExtension(filePath));
            }

            #region private
            internal static void Export(StackSource source, StreamWriter writeStream, string name)
            {
                var callTree = new CallTree(ScalingPolicyKind.ScaleToData);
                callTree.StackSource = source;
                var startNode = callTree.Root;

                // The Cachegrind format expects inclusive cost for all entries
                // DFS through the calltree to write out the expected data

                // Write the header...
                WriteHeader(writeStream);

                var fringe = new Stack<CallTreeNode>();
                var seen = new List<CallTreeNodeIndex>();
                Dictionary<string, int> callCounts = null;
                fringe.Push(startNode);
                seen.Add(startNode.Index);
                while (fringe.Count != 0)
                {
                    var node = fringe.Pop();
                    if (node.Name.StartsWith("Thread ("))
                        callCounts = CalculateStackTransitionCounts(node);
                    WriteEntryForNode(node, writeStream, callCounts);
                    if (node.HasChildren)
                    {
                        foreach (var callee in node.Callees)
                        {
                            seen.Add(callee.Index);
                            fringe.Push(callee);
                        }
                    }
                }
            }

            private static Dictionary<string, int> CalculateStackTransitionCounts(CallTreeNode node)
            {
                var counts = new Dictionary<string, int>();
                var stackSource = node.CallTree.StackSource;
                var samples = new List<StackSourceSample>();
                node.GetSamples(false, (index) =>
                {
                    var sample = stackSource.GetSampleByIndex(index);
                    samples.Add(sample);
                    return true;
                });
                samples.Sort((x, y) => x.TimeRelativeMSec.CompareTo(y.TimeRelativeMSec));

                StackSourceFrameIndex? previousFrame = null;
                foreach (var sample in samples)
                {
                    var stackIndex = sample.StackIndex;
                    var frameIndex = stackSource.GetFrameIndex(stackIndex);
                    // throw out the UNMANAGED_CODE frame...
                    if (stackSource.GetFrameName(frameIndex, false) == "UNMANAGED_CODE_TIME")
                        frameIndex = stackSource.GetFrameIndex(stackSource.GetCallerIndex(stackIndex));
                    if (!previousFrame.HasValue)
                        previousFrame = frameIndex;
                    
                    if (previousFrame == frameIndex)
                        continue;

                    // We'll over count since this will count for entering and leaving frames
                    // but we will only be checking in one direction when we use the dict
                    var key = $"{stackSource.GetFrameName(previousFrame.Value, false)}:{stackSource.GetFrameName(frameIndex, false)}";

                    if (counts.TryGetValue(key, out var value))
                        counts[key] = value++;
                    else
                        counts[key] = 1;

                    previousFrame = frameIndex;
                }
                return counts;
            }

            private static (string, string) GetModuleAndMethodName(CallTreeNode node)
            {
                var names = node.Name.Split('!', StringSplitOptions.RemoveEmptyEntries);
                if (names.Count() == 1)
                    return (node.Name, null);
                
                var methodName = names.Skip(1).Aggregate((x, y) => x + y);
                return (names[0], methodName);
            }

            private static void WriteEntryForNode(CallTreeNode node, StreamWriter writeStream, Dictionary<string, int> callCounts = null)
            {
                var (moduleName, methodName) = GetModuleAndMethodName(node);

                if (methodName != null)
                {
                    writeStream.Write($"ob={moduleName}\n");
                    writeStream.Write($"fn={methodName}\n");
                    writeStream.Write($"* {(int)node.InclusiveCount}\n");
                }
                else
                {
                    writeStream.Write($"fn={node.Name}\n");
                    writeStream.Write($"* {(int)node.InclusiveCount}\n");
                }

                if (node.HasChildren)
                {
                    foreach (var callee in node.Callees)
                    {
                        var callCount = 1;
                        if (callCounts != null)
                        {
                            if (callCounts.TryGetValue($"{node.Name}:{callee.Name}", out var count))
                                callCount = count;
                        }

                        var (calleeModuleName, calleeMethodName) = GetModuleAndMethodName(callee);

                        if (calleeMethodName != null)
                        {
                            writeStream.Write($"cob={calleeModuleName}\n");
                            writeStream.Write($"cfn={calleeMethodName}\n");
                            writeStream.Write($"calls={callCount} *\n");
                            writeStream.Write($"* {(int)callee.InclusiveCount}\n");
                        }
                        else
                        {
                            writeStream.Write($"cfn={callee.Name}\n");
                            writeStream.Write($"calls={callCount} *\n");
                            writeStream.Write($"* {(int)callee.InclusiveCount}\n");
                        }
                    }
                }

                writeStream.Write("\n");
            }

            internal static void WriteHeader(StreamWriter writeStream)
            {
                // Update this function for more complex headers
                writeStream.Write("events: samples\n\n");
            }
            #endregion
        }
    }
}
