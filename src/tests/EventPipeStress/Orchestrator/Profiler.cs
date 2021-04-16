// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Common;
using Microsoft.Diagnostics.Tracing.Session;

namespace Orchestrator
{
    // Currently only work on Windows.  Will need to extend to use Perf or similar on Linux.
    public class ProfilerSession : IDisposable
    {
        private bool disposedValue;
        private string filename;

        public ProfilerSession(string filename)
        {
            this.filename = filename;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }

    public class NullProfilerSession : ProfilerSession
    {
        public NullProfilerSession(string filename)
            : base(filename)
        { }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }

    public class ETWProfilerSession : ProfilerSession
    {
        private const string sessionName = "EventPipeETWProfilingSession";
        private bool disposedValue;
        private TraceEventSession session;

        public ETWProfilerSession(string filename)
            : base(filename)
        {
            session = new TraceEventSession(sessionName, filename)
            {
                CircularBufferMB = 1024,
                CpuSampleIntervalMSec = 1,
                StopOnDispose = true
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                    session.Dispose();

                disposedValue = true;
            }
        }
    }

    public class Profiler
    {
        private DirectoryInfo profileDirectory;
        public Profiler(
            int eventSize,
            int eventRate,
            BurstPattern burstPattern,
            ReaderType readerType,
            int slowReader,
            int duration,
            int cores,
            int threads,
            int eventCount,
            bool rundown,
            int bufferSize,
            int iterations)
        {
            // TODO: error if already exists...
            profileDirectory = Directory.CreateDirectory(Path.Join(Environment.CurrentDirectory, $"EventPipeProfiles_{DateTime.Now.ToString("yyyy_MM_dd_hhmm")}"));
            
            // write summary of experiment
            var sb = new StringBuilder();
            sb.AppendLine($"Experiment Summary:");
            sb.AppendLine($"Event Size: {eventSize}");
            sb.AppendLine($"Event Rate: {eventRate}");
            sb.AppendLine($"Burst Pattern: {burstPattern}");
            sb.AppendLine($"Reader Type: {readerType}");
            sb.AppendLine($"Slow Reader: {slowReader}");
            sb.AppendLine($"Duration: {duration}");
            sb.AppendLine($"Cores: {cores}");
            sb.AppendLine($"Threads: {threads}");
            sb.AppendLine($"Event Count: {eventCount}");
            sb.AppendLine($"Rundown: {rundown}");
            sb.AppendLine($"Buffer Size: {bufferSize}");
            sb.AppendLine($"iterations: {iterations}");

            File.WriteAllText(Path.Join(profileDirectory.FullName, "ExperimentSummary.txt"), sb.ToString());
        }

        public ProfilerSession Profile(int iteration)
        {
            string filename = $"iteration_{iteration}";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new ETWProfilerSession(filename);
            }
            else
            {
                return new NullProfilerSession(filename);
            }
        }

        public void SaveExecutionSummary(TestResults testResults)
        {
            File.WriteAllText(Path.Join(profileDirectory.FullName, "ExecutionSummary.txt"), testResults.GenerateSummary());
            File.WriteAllText(Path.Join(profileDirectory.FullName, "ExecutionStatistics.txt"), testResults.GenerateStatisticsTable());
        }
    }
}