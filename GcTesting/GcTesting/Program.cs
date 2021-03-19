using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime;
using System.Threading;
using CommandLine;

namespace GcTesting
{
    class Program
    {
        private const string USAGE_IN_BYTES = "/sys/fs/cgroup/memory/memory.usage_in_bytes";
        private const string OOM_CONTROL = "/sys/fs/cgroup/memory/memory.oom_control";
        private const string LIMIT_IN_BYTES = "/sys/fs/cgroup/memory/memory.limit_in_bytes";
        private static long _blocksAllocated = 0;

        public class Options
        {
            [Option(Required = false, Default = true)]
            public bool? MemoryPressureTask { get; set; }

            [Option(Required = false, Default = true)]
            public bool? FilePressureTask { get; set; }

            [Option(Required = false, Default = "1kb")]
            public string AllocationUnitSize { get; set; }

            [Option(Required = false, Default = "10mb", HelpText = "How much of new memory to allocate per second")]
            public string MemoryPressureRate { get; set; }

            [Option(Required = false, Default = "1gb")]
            public string MinimumMemoryUsage { get; set; }

            [Option(Required = false, Default = "1gb")]
            public string FilePressureSize { get; set; }

            internal long AllocationUnitSizeValue => FromSize(AllocationUnitSize);
            internal long MemoryPressureRateValue => FromSize(MemoryPressureRate);
            internal long MinimumMemoryUsageValue => FromSize(MinimumMemoryUsage);
            internal long FilePressureSizeValue => FromSize(FilePressureSize);
        }

        static async Task Main(string[] args)
        {
            try
        {
            await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(async options =>
            {
                var tasks = new List<Task>();
                tasks.Add(GcStatsTask());

                if (options.FilePressureTask == true)
                {
                    tasks.Add(FilePressureTask(options.FilePressureSizeValue));
                }

                if (options.MemoryPressureTask == true)
                {
                    tasks.Add(MemoryPressureTask(options.AllocationUnitSizeValue, options.MemoryPressureRateValue,
                        options.MinimumMemoryUsageValue));
                }

                await await Task.WhenAny(tasks);
            });
        }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        static async Task GcStatsTask()
        {
            Console.WriteLine("Starting GcStatsTask, " +
                              $"UtcNow:{DateTime.UtcNow}, " +
                              $"IsServerGC:{GCSettings.IsServerGC}, " +
                              $"LatencyMode:{GCSettings.LatencyMode}, " +
                              $"LOHCompactionMode:{GCSettings.LargeObjectHeapCompactionMode}, " +
                              "");

            await Task.Delay(TimeSpan.FromSeconds(1));
            
            if (File.Exists(OOM_CONTROL))
            {
                Console.WriteLine($"{OOM_CONTROL}:");
                Console.WriteLine(await File.ReadAllTextAsync(OOM_CONTROL));
            }
            
            if (File.Exists(LIMIT_IN_BYTES))
            {
                Console.WriteLine($"{LIMIT_IN_BYTES}:");
                Console.WriteLine(await File.ReadAllTextAsync(LIMIT_IN_BYTES));
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
                
                Console.WriteLine($"Elapsed:{(int) swGlobal.Elapsed.TotalSeconds,3:N0}s, " +
                                  $"GC-Rate:{gcRate}, " +
                                  $"Gen012:{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}, " +
                                  $"Total:{ToSize(GC.GetTotalMemory(false))}, " +
                                  $"Allocated:{ToSize(GC.GetTotalAllocatedBytes())}, " +
                                  $"HeapSize:{ToSize(gcInfo.HeapSizeBytes)}, " +
                                  $"MemoryLoad:{ToSize(gcInfo.MemoryLoadBytes)}, " +
                                  $"Committed:{ToSize(gcInfo.TotalCommittedBytes)}, " +
                                  $"Available:{ToSize(gcInfo.TotalAvailableMemoryBytes)}, " +
                                  $"HighMemoryLoadThreshold:{ToSize(gcInfo.HighMemoryLoadThresholdBytes)}, " +
                                  $"CGroupUsageInBytes:{usageInBytes}, " +
                                  $"BlockAllocations:{Interlocked.Read(ref _blocksAllocated)}, " +
                                  "");

                var elapsed = sw.Elapsed;
                if (elapsed < TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1) - elapsed);
                }
            }
        }

        static async Task MemoryPressureTask(long allocationUnitSize, long memoryPressureRate, long minimumMemoryUsage)
        {
            Console.WriteLine($"Starting MemoryPressureTask(allocationUnitSize={ToSize(allocationUnitSize)}, " +
                              $"memoryPressureRate={ToSize(memoryPressureRate)}, " +
                              $"minimumMemoryUsage={ToSize(minimumMemoryUsage)})");

            await Task.Delay(TimeSpan.FromSeconds(1));

            var rnd = new Random();
            Func<byte[]> allocate = () =>
            {
                var bytes = new byte[allocationUnitSize];
                // Write anything to the new memory block to force-commit it.  
                var seed = rnd.Next(256);
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = Convert.ToByte(seed % 256);
                }

                Interlocked.Increment(ref _blocksAllocated);

                return bytes;
            };

            var list = new List<byte[]>(Enumerable.Range(0, Convert.ToInt32(minimumMemoryUsage / allocationUnitSize))
                .Select(x => allocate()));

            var allocatedMemoryInCycle = 0l;
            var cycleSw = Stopwatch.StartNew();
            var idx = 0;
            while (true)
            {
                list[idx] = allocate();
                if (++idx >= list.Count)
                    idx = 0;

                allocatedMemoryInCycle += allocationUnitSize;
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
            }
        }

        static async Task FilePressureTask(long filePressureSize)
        {
            Console.WriteLine($"Starting FilePressureTask(filePressureSize={ToSize(filePressureSize)}");
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

            Console.WriteLine($"Written {tmpPath}");

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

        // Parse a file size.
        static long FromSize(string v)
        {
            var suffixes = new[] {"b", "kb", "mb", "gb", "tb"};
            var multipliers = Enumerable.Range(0, suffixes.Length)
                .ToDictionary(i => suffixes[i], i => 1l << (10 * i), StringComparer.OrdinalIgnoreCase);

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