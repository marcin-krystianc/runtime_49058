﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using CommandLine;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace GcTesting
{
    class Program
    {
        private const string USAGE_IN_BYTES = "/sys/fs/cgroup/memory/memory.usage_in_bytes";
        private const string OOM_CONTROL = "/sys/fs/cgroup/memory/memory.oom_control";
        private const string LIMIT_IN_BYTES = "/sys/fs/cgroup/memory/memory.limit_in_bytes";
        private static long _fullGcCompleted = -1;
        private static List<byte[]> _managedBlocks;
        private static long _allocatedManagedMemory = 0;
        private static long _allocatedUnmanagedMemory = 0;
        public class Options
        {
            [Option(Required = false, Default = "0b", HelpText = "Used for GC.AddMemoryPressure()")]
            public string GcAddMemoryPressure { get; set; }   
            
            [Option(Required = false, Default = true)]
            public bool? MemoryPressureTask { get; set; }   

            [Option(Required = false, Default = false,
                HelpText = "Runs the task for testing unmanaged memory allocations.")]
            public bool? UnmanagedMemoryPressureTask { get; set; }

            [Option(Required = false, Default = true, HelpText = "Register for full-GC notifications and counts them.")]
            public bool? GcNotificationsTask { get; set; }

            [Option(Required = false, Default = true)]
            public bool? FilePressureTask { get; set; }

            [Option(Required = false, Default = "1kb")]
            public string AllocationUnitSize { get; set; }

            [Option(Required = false, Default = "10mb", HelpText = "How much of new memory to allocate per second")]
            public string MemoryPressureRate { get; set; }

            [Option(Required = false, Default = "10mb",
                HelpText = "Amount of new unmanaged memory to allocate per second")]
            public string UnmanagedMemoryPressureRate { get; set; }

            [Option(Required = false, Default = "1gb",
                HelpText = "Memory to be referenced and allocated at program start.")]
            public string MinimumMemoryUsage { get; set; }

            [Option(Required = false, Default = "1gb", HelpText = "Unmanaged memory to be allocated at program start.")]
            public string MinimumUnmanagedMemoryUsage { get; set; }

            [Option(Required = false, Default = false)]
            public bool LeakMemory { get; set; }

            [Option(Required = false, Default = "1gb")]
            public string FilePressureSize { get; set; }
            internal long AllocationUnitSizeValue => FromSize(AllocationUnitSize);
            internal long MemoryPressureRateValue => FromSize(MemoryPressureRate);
            internal long UnmanagedMemoryPressureRateValue => FromSize(UnmanagedMemoryPressureRate);
            internal long MinimumMemoryUsageValue => FromSize(MinimumMemoryUsage);
            internal long MinimumUnmanagedMemoryUsageValue => FromSize(MinimumUnmanagedMemoryUsage);
            internal long FilePressureSizeValue => FromSize(FilePressureSize);
            internal long GcAddMemoryPressureValue => FromSize(GcAddMemoryPressure);
        }

        static async Task Main(string[] args)
        {
            try
            {
                Log.Logger = new LoggerConfiguration()
                    .WriteTo.Console(theme: ConsoleTheme.None)
                    .CreateLogger();

                Log.Information($"OSDescription:{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, " +
                                $"OSArchitecture:{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, " +
                                $"RuntimeIdentifier:{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}, " +
                                $"ProcessArchitecture:{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
                                $"FrameworkDescription:{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}, " +
                                "");

                await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(async options =>
                {
                    if (options.GcAddMemoryPressureValue != 0)
                    {
                        Log.Information($"Executing GC.AddMemoryPressure({ToSize(options.GcAddMemoryPressureValue)})");
                        GC.AddMemoryPressure(options.GcAddMemoryPressureValue);
                    }

                    var tasks = new List<Task>();
                    tasks.Add(GcStatsTask());

                    if (options.GcNotificationsTask == true)
                    {
                        tasks.Add(FullGcLoggerTask());
                    }

                    if (options.FilePressureTask == true)
                    {
                        tasks.Add(FilePressureTask(options.FilePressureSizeValue));
                    }

                    if (options.MemoryPressureTask == true)
                    {
                        tasks.Add(MemoryPressureTask(options.AllocationUnitSizeValue, options.MemoryPressureRateValue,
                            options.MinimumMemoryUsageValue, options.LeakMemory));
                    }

                    if (options.UnmanagedMemoryPressureTask == true)
                    {
                        tasks.Add(UnmanagedMemoryPressureTask(1048576, options.UnmanagedMemoryPressureRateValue,
                            options.MinimumUnmanagedMemoryUsageValue));
                    }

                    await await Task.WhenAny(tasks);
                });
            }
            catch (OutOfMemoryException e)
            {
                Log.Information(e.ToString() + Environment.NewLine);

                var gcInfo = GC.GetGCMemoryInfo();
                string usageInBytes = "N/A";
                if (File.Exists(USAGE_IN_BYTES))
                {
                    usageInBytes = ToSize(Convert.ToInt64(File.ReadLines(USAGE_IN_BYTES).First()));
                }
                
                var process = Process.GetCurrentProcess();
                
                Log.Information($"Gen012:{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}, " +
                                $"Total:{ToSize(GC.GetTotalMemory(false))}, " +
                                $"Allocated:{ToSize(GC.GetTotalAllocatedBytes())}, " +
                                $"HeapSize:{ToSize(gcInfo.HeapSizeBytes)}, " +
                                $"MemoryLoad:{ToSize(gcInfo.MemoryLoadBytes)}, " +
                                $"Committed:{ToSize(gcInfo.TotalCommittedBytes)}, " +
                                $"Available:{ToSize(gcInfo.TotalAvailableMemoryBytes)}, " +
                                $"HighMemoryLoadThreshold:{ToSize(gcInfo.HighMemoryLoadThresholdBytes)}, " +
                                $"WorkingSet:{ToSize(process.WorkingSet64)}, " +
                                $"PrivateMemorySize:{ToSize(process.PrivateMemorySize64)}, " +
                                $"PagedMemorySize:{ToSize(process.PagedMemorySize64)}, " +
                                $"VirtualMemorySize:{ToSize(process.VirtualMemorySize64)}, " +
                                $"CGroupUsageInBytes:{usageInBytes}, " +
                                $"AllocatedManaged:{ToSize(Interlocked.Read(ref _allocatedManagedMemory))}, " +
                                $"AllocatedUnmanaged:{ToSize(Interlocked.Read(ref _allocatedUnmanagedMemory))}, " +
                                $"FullGcCompleted:{Interlocked.Read(ref _fullGcCompleted)}, " +
                                "");
                throw;
            }
        }

        static async Task GcStatsTask()
        {
            Log.Information("Starting GcStatsTask, " +
                            $"UtcNow:{DateTime.UtcNow}, " +
                            $"IsServerGC:{GCSettings.IsServerGC}, " +
                            $"LatencyMode:{GCSettings.LatencyMode}, " +
                            $"LOHCompactionMode:{GCSettings.LargeObjectHeapCompactionMode}, " +
                            "");

            await Task.Delay(TimeSpan.FromSeconds(1));

            if (File.Exists(OOM_CONTROL))
            {
                Log.Information($"{OOM_CONTROL}:");
                Log.Information(await File.ReadAllTextAsync(OOM_CONTROL));
            }

            if (File.Exists(LIMIT_IN_BYTES))
            {
                Log.Information($"{LIMIT_IN_BYTES}:{ToSize(Convert.ToInt64(File.ReadLines(LIMIT_IN_BYTES).First()))}");
            }

            var stats = new Queue<GCMemoryInfo>();
            var swGlobal = Stopwatch.StartNew();
            while (true)
            {
                var sw = Stopwatch.StartNew();

                var gcInfo = GC.GetGCMemoryInfo();
                var gcRate = "N/A";

                // Keep probes from last 60 seconds
                if (stats.Count >= 60)
                {
                    var oldGcInfo = stats.Dequeue();
                    gcRate = Convert.ToString(gcInfo.Index - oldGcInfo.Index);
                }

                stats.Enqueue(gcInfo);

                string usageInBytes = "N/A";
                if (File.Exists(USAGE_IN_BYTES))
                {
                    usageInBytes = ToSize(Convert.ToInt64(File.ReadLines(USAGE_IN_BYTES).First()));
                }

                var process = Process.GetCurrentProcess();
        
                Log.Information($"Elapsed:{(int) swGlobal.Elapsed.TotalSeconds,3:N0}s, " +
                                $"GC-Rate:{gcRate}, " +
                                $"Gen012:{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}, " +
                                $"Total:{ToSize(GC.GetTotalMemory(false))}, " +
                                $"Allocated:{ToSize(GC.GetTotalAllocatedBytes())}, " +
                                $"HeapSize:{ToSize(gcInfo.HeapSizeBytes)}, " +
                                $"MemoryLoad:{ToSize(gcInfo.MemoryLoadBytes)}, " +
                                $"Committed:{ToSize(gcInfo.TotalCommittedBytes)}, " +
                                $"Available:{ToSize(gcInfo.TotalAvailableMemoryBytes)}, " +
                                $"HighMemoryLoadThreshold:{ToSize(gcInfo.HighMemoryLoadThresholdBytes)}, " +
                                $"WorkingSet:{ToSize(process.WorkingSet64)}, " +
                                $"PrivateMemorySize:{ToSize(process.PrivateMemorySize64)}, " +
                                $"PagedMemorySize:{ToSize(process.PagedMemorySize64)}, " +
                                $"VirtualMemorySize:{ToSize(process.VirtualMemorySize64)}, " +
                                $"CGroupUsageInBytes:{usageInBytes}, " +
                                $"AllocatedManaged:{ToSize(Interlocked.Read(ref _allocatedManagedMemory))}, " +
                                $"AllocatedUnmanaged:{ToSize(Interlocked.Read(ref _allocatedUnmanagedMemory))}, " +
                                $"FullGcCompleted:{Interlocked.Read(ref _fullGcCompleted)}, " +
                                "");

                var elapsed = sw.Elapsed;
                if (elapsed < TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1) - elapsed);
                }
            }
        }

        static async Task MemoryPressureTask(long allocationUnitSize, long memoryPressureRate, long minimumMemoryUsage,
            bool leakMemory)
        {
            Log.Information($"Starting MemoryPressureTask(allocationUnitSize={ToSize(allocationUnitSize)}, " +
                            $"memoryPressureRate={ToSize(memoryPressureRate)}, " +
                            $"minimumMemoryUsage={ToSize(minimumMemoryUsage)}, " +
                            $"leakMemory={leakMemory}");

            await Task.Delay(TimeSpan.FromSeconds(1));

            var rnd = new Random();
            Func<byte[]> allocate = () =>
            {
                var bytes = new byte[allocationUnitSize];
                Interlocked.Add(ref _allocatedManagedMemory, allocationUnitSize);

                // Write anything to the new memory block to force-commit it.  
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = 42;
                }

                return bytes;
            };

            var initialSize = Convert.ToInt32(minimumMemoryUsage / allocationUnitSize);
            _managedBlocks = new List<byte[]>(initialSize);
            _managedBlocks.AddRange(Enumerable.Range(0, initialSize).Select(_ => allocate()));

            var allocatedMemoryInCycle = 0L;
            var cycleSw = Stopwatch.StartNew();
            var idx = 0;
            while (true)
            {
                if (allocatedMemoryInCycle >= memoryPressureRate)
                {
                    var elapsed = cycleSw.Elapsed;
                    var delay = elapsed >= TimeSpan.FromSeconds(1)
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(1) - elapsed;

                    await Task.Delay(delay);
                    cycleSw = Stopwatch.StartNew();
                    allocatedMemoryInCycle = 0;
                }
                else
                {
                    if (leakMemory)
                    {
                        _managedBlocks.Add(allocate());
                    }
                    else
                    {
                        _managedBlocks[idx] = allocate();
                        if (++idx >= _managedBlocks.Count)
                            idx = 0;
                    }

                    allocatedMemoryInCycle += allocationUnitSize;
                }
            }
        }

        static async Task UnmanagedMemoryPressureTask(long unmanagedAllocationUnitSize,
            long unmanagedMemoryPressureRate, long minimumUnmanagedMemoryUsage)
        {
            Log.Information(
                $"Starting UnmanagedMemoryPressureTask({nameof(unmanagedAllocationUnitSize)}={ToSize(unmanagedAllocationUnitSize)}, " +
                $"{nameof(unmanagedMemoryPressureRate)}={ToSize(unmanagedMemoryPressureRate)}, " +
                $"{nameof(minimumUnmanagedMemoryUsage)}={ToSize(minimumUnmanagedMemoryUsage)}, " +
                "");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Func<IntPtr> allocate = () =>
            {
                var bytes = Marshal.AllocHGlobal((int) unmanagedAllocationUnitSize);
                Interlocked.Add(ref _allocatedUnmanagedMemory, unmanagedAllocationUnitSize);

                // Write anything to the new memory block to force-commit it.  
                for (var i = 0; i < unmanagedAllocationUnitSize; i++)
                {
                    Marshal.WriteByte(bytes, i, 42);
                }

                return bytes;
            };
            
            var unmanagedMemory = new List<IntPtr>();
            unmanagedMemory.Add(allocate());
            
            var allocatedMemoryInCycle = 0L;
            var cycleSw = Stopwatch.StartNew();

            while (true)
            {
                if (allocatedMemoryInCycle >= unmanagedMemoryPressureRate)
                {
                    var elapsed = cycleSw.Elapsed;
                    var delay = elapsed >= TimeSpan.FromSeconds(1)
                        ? TimeSpan.Zero
                        : TimeSpan.FromSeconds(1) - elapsed;

                    await Task.Delay(delay);
                    cycleSw = Stopwatch.StartNew();
                    allocatedMemoryInCycle = 0;
                }
                else
                {
                    unmanagedMemory.Add(allocate());
                    allocatedMemoryInCycle += unmanagedAllocationUnitSize;
                }
            }
        }

        static async Task FilePressureTask(long filePressureSize)
        {
            Log.Information($"Starting FilePressureTask(filePressureSize={ToSize(filePressureSize)}");
            await Task.Delay(TimeSpan.FromSeconds(1));

            var rnd = new Random();
            var tmpPath = Path.Combine(Path.GetTempPath(), "GcTesting");
            var bytes = new byte[65536];
            await using var f = File.Open(tmpPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            rnd.NextBytes(bytes);

            for (var i = 0; i < filePressureSize / bytes.Length; i++)
            {
                f.Write(bytes);
            }

            Log.Information($"Written {tmpPath}");

            while (true)
            {
                f.Seek(0, SeekOrigin.Begin);

                for (var i = 0; i < filePressureSize / bytes.Length; i++)
                {
                    f.Read(bytes);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(1));
            }
        }

        static async Task FullGcLoggerTask()
        {
            Log.Information($"Starting FullGCLoggerTask");
            await Task.Delay(TimeSpan.FromSeconds(1));

            GC.RegisterForFullGCNotification(1, 1);
            _fullGcCompleted = 0;

            while (true)
            {
                if (GC.WaitForFullGCApproach() == GCNotificationStatus.NotApplicable)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1));
                    _fullGcCompleted = -2;
                }

                GC.WaitForFullGCComplete();
                Interlocked.Increment(ref _fullGcCompleted);
            }
        }

        // Parse a file size.
        static long FromSize(string v)
        {
            var suffixes = new[] {"b", "kb", "mb", "gb", "tb"};
            var multipliers = Enumerable.Range(0, suffixes.Length)
                .ToDictionary(i => suffixes[i], i => 1L << (10 * i), StringComparer.OrdinalIgnoreCase);

            var suffix = suffixes
                .Select(suffix => (v.EndsWith(suffix, StringComparison.OrdinalIgnoreCase), suffix))
                .Reverse()
                .Where(x => x.Item1 == true)
                .Select(x => x.suffix)
                .FirstOrDefault();

            if (suffix != null)
            {
                v = v.Substring(0, v.Length - suffix.Length);
            }

            var result = long.Parse(v);

            if (suffix != null)
            {
                result *= multipliers[suffix];
            }

            return result;
        }

        static string ToSize(long bytes)
        {
            const int scale = 1024;
            string[] orders = new string[] {"GB", "MB", "KB", "Bytes"};
            long max = (long) Math.Pow(scale, orders.Length - 1);

            foreach (string order in orders)
            {
                if (bytes >= max)
                    return string.Format("{0:###.#0} {1}", decimal.Divide(bytes, max), order);

                max /= scale;
            }

            return "0 Bytes";
        }
    }
}