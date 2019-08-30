// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;

namespace Microsoft.Diagnostics.Tools.Trace
{
    internal enum TraceFileFormat { NetTrace, Speedscope, TraceEventJson };

    internal static class TraceFileFormatConverter
    {
        private static Dictionary<TraceFileFormat, string> TraceFileFormatExtensions = new Dictionary<TraceFileFormat, string>() {
            { TraceFileFormat.NetTrace,     "nettrace" },
            { TraceFileFormat.Speedscope,   "speedscope.json" },
            { TraceFileFormat.TraceEventJson, "json" }
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
                case TraceFileFormat.TraceEventJson:
                    ConvertToTraceEventJson(fileToConvert, outputFilename);
                    break;
                default:
                    // Validation happened way before this, so we shoud never reach this...
                    throw new ArgumentException($"Invalid TraceFileFormat \"{format}\"");
            }
            Console.Out.WriteLine("Conversion complete");
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

        private static void ConvertToTraceEventJson(string fileToConvert, string outputFilename)
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

                TraceEventJsonStackSourceWriter.WriteStackSourceAsJson(stackSource, outputFilename);
            }

            if (File.Exists(etlxFilePath))
            {
                File.Delete(etlxFilePath);
            }

        }
    }
    public class TraceEventJsonStackSourceWriter
    {
        /// <summary>
        /// Converts a given StackSource to the TraceEventJson
        /// format used by chromium based browsers (Chrome, Edge, Brave, etc.)
        /// documented here: https://docs.google.com/document/d/1CvAClvFfyA5R-PhYUmn5OOQtYMH4h6I0nSsKchNAySU/preview#
        /// </summary>
        /// <param name="source"></param>
        public static void WriteStackSourceAsJson(StackSource source, string filePath)
        {
            if (File.Exists(filePath))
                File.Delete(filePath);

            using (var writeStream = File.CreateText(filePath))
                Export(source, writeStream, Path.GetFileNameWithoutExtension(filePath));
        }

        #region private
        internal static void Export(StackSource source, TextWriter writer, string name)
        {
            var samplesPerThread = GetSortedSamplesPerThread(source);
            int pid = GetPidFromSamples(samplesPerThread, source);

            foreach (var samples in samplesPerThread.Values)
                MakeSureSamplesDoNotOverlap(samples);

            var exportedFrameNameToExportedFrameId = new Dictionary<string, int>();
            var exportedFrameIdToFrameTuple = new Dictionary<int, FrameInfo>();
            var profileEventsPerThread = new Dictionary<string, IReadOnlyList<ProfileEvent>>();

            foreach (var pair in samplesPerThread)
            {
                var frameIdToSamples = WalkTheStackAndExpandSamples(source, pair.Value, exportedFrameNameToExportedFrameId, exportedFrameIdToFrameTuple);

                var sortedProfileEvents = GetAggregatedOrderedProfileEvents(frameIdToSamples);

                profileEventsPerThread.Add(pair.Key, sortedProfileEvents);
            };

            var orderedFrameNames = exportedFrameNameToExportedFrameId.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray();

            WriteToFile(exportedFrameIdToFrameTuple, profileEventsPerThread, orderedFrameNames, writer, name, pid);
        }

        internal static int GetPidFromSamples(IReadOnlyDictionary<string, List<Sample>> samplesPerThread, StackSource source)
        {
            var key = samplesPerThread.Keys.First();
            var sample = samplesPerThread[key].First();
            var stackIndex = sample.StackIndex;

            while (stackIndex != StackSourceCallStackIndex.Invalid)
            {
                var frameName = source.GetFrameName(source.GetFrameIndex(stackIndex), false);

                if (!frameName.StartsWith("Process"))
                {
                    stackIndex = source.GetCallerIndex(stackIndex);
                    continue;
                }

                var match = Regex.Match(frameName, @"\((\d+)\)");
                return match.Success ? int.Parse(match.Groups[1].Value) : 0;
            }

            return 0;

        }

        /// <summary>
        /// we want to identify the thread for every sample to prevent from 
        /// overlaping of samples for the concurrent code so we group the samples by Threads
        /// this method also sorts the samples by relative time (ascending)
        /// </summary>
        internal static IReadOnlyDictionary<string, List<Sample>> GetSortedSamplesPerThread(StackSource stackSource)
        {
            var samplesPerThread = new Dictionary<string, List<Sample>>();

            stackSource.ForEach(sample =>
            {
                var stackIndex = sample.StackIndex;

                while (stackIndex != StackSourceCallStackIndex.Invalid)
                {
                    var frameName = stackSource.GetFrameName(stackSource.GetFrameIndex(stackIndex), false);

                        // we walk the stack up until we find the Thread name
                        if (!frameName.StartsWith("Thread ("))
                    {
                        stackIndex = stackSource.GetCallerIndex(stackIndex);
                        continue;
                    }

                    if (!samplesPerThread.TryGetValue(frameName, out var samples))
                        samplesPerThread[frameName] = samples = new List<Sample>();

                    samples.Add(new Sample(sample.StackIndex, -1, sample.TimeRelativeMSec, sample.Metric, -1));

                    return;
                }

                throw new InvalidOperationException("Sample with no Thread assigned!");
            });

            foreach (var samples in samplesPerThread.Values)
            {
                // all samples in the StackSource should be sorted, but we want to ensure it
                samples.Sort((x, y) => x.RelativeTime.CompareTo(y.RelativeTime));
            }

            return samplesPerThread;
        }

        /// <summary>
        /// this method fixes the metrics of the samples to make sure they don't overlap
        /// it's very common that following samples overlap by a very small number like 0.0000000000156
        /// we can't allow for that to happen because the SpeedScope can't draw such samples
        /// </summary>
        internal static void MakeSureSamplesDoNotOverlap(List<Sample> samples)
        {
            for (int i = 0; i < samples.Count - 1; i++)
            {
                var current = samples[i];
                var next = samples[i + 1];

                if (current.RelativeTime + current.Metric > next.RelativeTime)
                {
                    // the difference between current.Metric and recalculatedMetric is typically
                    // a very small number like 0.0000000000156
                    double recalculatedMetric = next.RelativeTime - current.RelativeTime;
                    samples[i] = new Sample(current.StackIndex, -1, current.RelativeTime, recalculatedMetric, current.Depth);
                }
            }
            // we don't need to worry about the last sample
            // it can't overlap the next one because it is the last one and there is no next one
        }

        /// <summary>
        /// all the samples that we have are leafs (last sample in the call stack)
        /// this method expands those samples to full information 
        /// it walks the stack up to the begining and adds a sample for every method on the stack
        /// it's required to build full information
        /// </summary>
        internal static IReadOnlyDictionary<int, List<Sample>> WalkTheStackAndExpandSamples(StackSource stackSource, IEnumerable<Sample> leafs,
            Dictionary<string, int> exportedFrameNameToExportedFrameId, Dictionary<int, FrameInfo> exportedFrameIdToExportedNameAndCallerId)
        {
            var frameIdToSamples = new Dictionary<int, List<Sample>>();

            // we use stack here bacause we want a certain order: from the root to the leaf
            var stackIndexesToHandle = new Stack<StackSourceCallStackIndex>();

            foreach (var leafSample in leafs)
            {
                // walk the stack first
                var stackIndex = leafSample.StackIndex;
                while (stackIndex != StackSourceCallStackIndex.Invalid)
                {
                    stackIndexesToHandle.Push(stackIndex);

                    stackIndex = stackSource.GetCallerIndex(stackIndex);
                }

                // add sample for every method on the stack
                int depth = -1;
                int callerFrameId = -1;
                while (stackIndexesToHandle.Count > 0)
                {
                    stackIndex = stackIndexesToHandle.Pop();
                    depth++;

                    var frameIndex = stackSource.GetFrameIndex(stackIndex);
                    if (frameIndex == StackSourceFrameIndex.Broken || frameIndex == StackSourceFrameIndex.Invalid)
                        continue;

                    var frameModuleName = stackSource.GetFrameName(frameIndex, true);
                    if (string.IsNullOrEmpty(frameModuleName))
                        continue;

                    string[] names = frameModuleName.Split(new char[] { '!' }, options: StringSplitOptions.RemoveEmptyEntries);
                    var frameName = names.Length == 1 ? "foobar" : names.Skip(1).Aggregate((x, y) => x + y);
                    var moduleName = names[0];

                    if (!exportedFrameNameToExportedFrameId.TryGetValue(frameName, out int exportedFrameId))
                        exportedFrameNameToExportedFrameId.Add(frameName, exportedFrameId = exportedFrameNameToExportedFrameId.Count);

                    if (!frameIdToSamples.TryGetValue(exportedFrameId, out var samples))
                        frameIdToSamples.Add(exportedFrameId, samples = new List<Sample>());

                    // the time and metric are the same as for the leaf sample
                    // the difference is stack index (not really used from here), caller frame id and depth (used for sorting the exported data)
                    samples.Add(new Sample(stackIndex, callerFrameId, leafSample.RelativeTime, leafSample.Metric, depth));

                    if (!exportedFrameIdToExportedNameAndCallerId.TryGetValue(exportedFrameId, out var frameTuple))
                        exportedFrameIdToExportedNameAndCallerId.Add(exportedFrameId, new FrameInfo(callerFrameId, frameName, moduleName));

                    callerFrameId = exportedFrameId;
                }
            }

            return frameIdToSamples;
        }

        /// <summary>
        /// this method aggregates all the singular samples to continuous events
        /// example: samples for Main taken at time 0.1 0.2 0.3 0.4 0.5
        /// are gonna be translated to Main start at 0.1 stop at 0.5
        /// </summary>
        internal static IReadOnlyList<ProfileEvent> GetAggregatedOrderedProfileEvents(IReadOnlyDictionary<int, List<Sample>> frameIdToSamples)
        {
            List<ProfileEvent> profileEvents = new List<ProfileEvent>();

            foreach (var samplesInfo in frameIdToSamples)
            {
                var frameId = samplesInfo.Key;
                var samples = samplesInfo.Value;

                // this should not be required, but I prefer to be sure that the data is sorted
                samples.Sort((x, y) => x.RelativeTime.CompareTo(y.RelativeTime));

                Sample openSample = samples[0]; // samples are never empty
                for (int i = 1; i < samples.Count; i++)
                {
                    if (AreNotContinuous(samples[i - 1], samples[i]))
                    {
                        AddEvents(profileEvents, openSample, samples[i - 1], frameId);

                        openSample = samples[i];
                    }
                }

                // we need to handle the last (or the only one) profile event
                AddEvents(profileEvents, openSample, samples[samples.Count - 1], frameId);
            }

            // MUST HAVE!!! the tool expects the profile events in certain order!!
            return OrderForExport(profileEvents).ToArray();
        }

        /// <summary>
        /// this method checks if both samples do NOT belong to the same profile event
        /// </summary>
        private static bool AreNotContinuous(Sample left, Sample right)
        {
            if (left.Depth != right.Depth)
                return true;
            if (left.CallerFrameId != right.CallerFrameId)
                return true;

            // 1.2 is a magic number based on some experiments ;)
            return left.RelativeTime + (left.Metric * 1.2) < right.RelativeTime;
        }

        /// <summary>
        /// this method adds a new profile event for provided samples
        /// it also make sure that a profile event does not open and close at the same time (would be ignored by SpeedScope)
        /// </summary>
        private static void AddEvents(List<ProfileEvent> profileEvents, Sample openSample, Sample closeSample, int frameId)
        {
            if (openSample.Depth != closeSample.Depth)
                throw new ArgumentException("Invalid arguments, both samples must be of the same depth");
            if (openSample.RelativeTime == closeSample.RelativeTime + closeSample.Metric)
                throw new ArgumentException("Invalid samples, two samples can not happen at the same time.");

            profileEvents.Add(new ProfileEvent(ProfileEventType.Open, frameId, openSample.RelativeTime, openSample.Depth));
            profileEvents.Add(new ProfileEvent(ProfileEventType.Close, frameId, closeSample.RelativeTime + closeSample.Metric, closeSample.Depth));
        }

        /// <summary>
        /// this method orders the profile events in the order required by SpeedScope
        /// it's just the order of drawing the time graph
        /// </summary>
        internal static IEnumerable<ProfileEvent> OrderForExport(IEnumerable<ProfileEvent> profiles)
        {
            return profiles
                .GroupBy(@event => @event.RelativeTime)
                .OrderBy(group => group.Key)
                .SelectMany(group =>
                {
                        // MakeSureSamplesDoNotOverlap guarantees that samples do NOT overlap
                        // AddEvents guarantees us that there is no event which starts and end at the same time
                        // so we don't need to worry about this edge case here

                        // first of all, we need to emit close events, descending by depth (tool format requires that)
                        var closingDescendingByDepth = group.Where(@event => @event.Type == ProfileEventType.Close).OrderByDescending(@event => @event.Depth);
                        // then we can open new events, ascending by depth (tool format requires that)
                        var openingAscendingByDepth = group.Where(@event => @event.Type == ProfileEventType.Open).OrderBy(@event => @event.Depth);

                    return closingDescendingByDepth.Concat(openingAscendingByDepth);
                });
        }

        /// <summary>
        /// writes pre-calculated data to SpeedScope format
        /// </summary>
        internal static void WriteToFile(Dictionary<int, FrameInfo> frameIdToFrameTuple,
            IReadOnlyDictionary<string, IReadOnlyList<ProfileEvent>> sortedProfileEventsPerThread,
            IReadOnlyList<string> orderedFrameNames, TextWriter writer, string name, int pid)
        {
            writer.Write("{");
            writer.Write("\"traceEvents\": [");
            bool isFirst = true;
            foreach (var perThread in sortedProfileEventsPerThread.OrderBy(pair => pair.Value.First().RelativeTime))
            {

                var sortedProfileEvents = perThread.Value;
                var match = Regex.Match(perThread.Key, @"\d+");
                var tid = match.Success ? match.Value : "0000";

                foreach (var profileEvent in sortedProfileEvents)
                {
                    if (!isFirst)
                        writer.Write(", ");
                    else
                        isFirst = false;

                    writer.Write("{");
                    writer.Write($"\"name\": \"{frameIdToFrameTuple[profileEvent.FrameId].FrameName}\", ");
                    writer.Write($"\"cat\": \"sampleEvent\", ");
                    writer.Write($"\"ph\": \"{(profileEvent.Type == ProfileEventType.Open ? "B" : "E")}\", ");
                    writer.Write($"\"ts\": {profileEvent.RelativeTime.ToString("R", CultureInfo.InvariantCulture)}, ");
                    writer.Write($"\"pid\": {pid}, ");
                    writer.Write($"\"tid\": {tid}, ");
                    writer.Write($"\"sf\": {profileEvent.FrameId}");
                    writer.Write("}");
                }
            }
            writer.Write("], ");
            writer.Write("\"displayTimeUnit\": \"ms\", ");
            writer.Write("\"stackFrames\": {");
            isFirst = true;
            foreach (var frame in frameIdToFrameTuple)
            {
                if (!isFirst)
                    writer.Write(", ");
                else
                    isFirst = false;

                var frameId = frame.Key;
                var tuple = frame.Value;
                writer.Write($"{frameId}: {{");
                writer.Write($"\"name\": {tuple.FrameName}, ");
                writer.Write($"\"category\": \"{tuple.ModuleName}\"");
                if (tuple.ParentId != -1)
                    writer.Write($", \"parent\": {tuple.ParentId}");
                writer.Write("}");
            }
            writer.Write("}, ");
            writer.Write($"\"otherData\": {{ \"name\": \"{name}\" }}");
            writer.Write("}");
        }

        internal struct Sample
        {
            internal Sample(StackSourceCallStackIndex stackIndex, int callerFrameId, double relativeTime, double metric, int depth)
            {
                StackIndex = stackIndex;
                CallerFrameId = callerFrameId;
                RelativeTime = relativeTime;
                Metric = metric;
                Depth = depth;
            }

            public override string ToString() => RelativeTime.ToString(CultureInfo.InvariantCulture);

            #region private
            internal StackSourceCallStackIndex StackIndex { get; }
            internal int CallerFrameId { get; }
            internal double RelativeTime { get; }
            internal double Metric { get; }
            internal int Depth { get; }
            #endregion private
        }

        internal enum ProfileEventType : byte
        {
            Open = 0, Close = 1
        }

        internal struct ProfileEvent
        {
            public ProfileEvent(ProfileEventType type, int frameId, double relativeTime, int depth, int tid = 0, int pid = 0)
            {
                Type = type;
                FrameId = frameId;
                RelativeTime = relativeTime;
                Depth = depth;
                TID = tid;
                PID = pid;
            }

            public override string ToString() => $"{RelativeTime.ToString(CultureInfo.InvariantCulture)} {Type} {FrameId}";

            #region private
            internal ProfileEventType Type { get; }
            internal int FrameId { get; }
            internal double RelativeTime { get; }
            internal int Depth { get; }
            internal int TID { get; }
            internal int PID { get; }
            #endregion private
        }

        internal struct FrameInfo
        {
            public FrameInfo(int parentId, string frameName, string moduleName)
            {
                ParentId = parentId;
                FrameName = frameName;
                ModuleName = moduleName;
            }

            #region private
            internal int ParentId { get; }
            internal string FrameName { get; }
            internal string ModuleName { get; }
            #endregion private
        }
        #endregion private
    }
}


