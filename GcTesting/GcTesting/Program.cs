using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
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
        private const string MEMORY_CURRENT = "/sys/fs/cgroup/memory.current";
        private const string OOM_CONTROL = "/sys/fs/cgroup/memory/memory.oom_control";
        private const string LIMIT_IN_BYTES = "/sys/fs/cgroup/memory/memory.limit_in_bytes";
        private const string MEMORY_STAT_v1 = "/sys/fs/cgroup/memory/memory.stat";
        private const string MEMORY_STAT_v2 = "/sys/fs/cgroup/memory.stat";
        private static long _fullGcCompleted = -1;
        private static List<byte[]> _managedBlocks;
        private static long _allocatedManagedBlocks = 0;
        private static long _allocatedUnmanagedBlocks = 0;

        public class Options
        {
            [Option(Required = false, Default = "0b", HelpText = "Used for GC.AddMemoryPressure()")]
            public string GcAddMemoryPressure { get; set; }

            [Option(Required = false, Default = true, HelpText = "Runs the task performing managed memory allocations.")]
            public bool? MemoryPressureTask { get; set; }

            [Option(Required = false, Default = false, HelpText = "Runs the task performing unmanaged memory allocations.")]
            public bool? UnmanagedMemoryPressureTask { get; set; }

            [Option(Required = false, Default = true, HelpText = "Register for full-GC notifications and counts occurrences of them.")]
            public bool? GcNotificationsTask { get; set; }

            [Option(Required = false, Default = "80kb", HelpText = "Size of the byte array allocated by the managed memory task.")]
            public string AllocationUnitSize { get; set; }

            [Option(Required = false, Default = "10mb", HelpText = "Amount of new managed memory allocated per second.")]
            public string MemoryPressureRate { get; set; }

            [Option(Required = false, Default = "10mb",  HelpText = "Amount of new unmanaged allocated per second.")]
            public string UnmanagedMemoryPressureRate { get; set; }

            [Option(Required = false, Default = "1gb",  HelpText = "Managed memory to be referenced and allocated at program start.")]
            public string MinimumMemoryUsage { get; set; }

            [Option(Required = false, Default = "1gb", HelpText = "Unmanaged memory to be allocated at program start.")]
            public string MinimumUnmanagedMemoryUsage { get; set; }

            [Option(Required = false, Default = false, HelpText = "Keep references to allocated managed memory to prevent from collecting it.")]
            public bool LeakManagedMemory { get; set; }

            [Option(Required = false, Default = false, HelpText = "Runs the task populating the I/O cache (page cache).")]
            public bool? FilePressureTask { get; set; }

            [Option(Required = false, Default = "1gb", HelpText = "Size of the file used by the file pressure task.")]
            public string FilePressureSize { get; set; }
            
            [Option(Required = false, Default = false, HelpText = "Perform writes instead of reads in the file pressure task.")]
            public bool? FilePressureWriting { get; set; }
            
            [Option(Required = false, Default = false, HelpText = "Use memory mapped files")]
            public bool? FilePressureUseMemoryMaps { get; set; }
            
            [Option(Required = false, Default = null, HelpText = "Name of the memory map to create (can be null).")]
            public string FilePressureMemoryMapName { get; set; }

            internal long AllocationUnitSizeValue => FromSize(AllocationUnitSize);
            internal long MemoryPressureRateValue => FromSize(MemoryPressureRate);
            internal long UnmanagedMemoryPressureRateValue => FromSize(UnmanagedMemoryPressureRate);
            internal long MinimumMemoryUsageValue => FromSize(MinimumMemoryUsage);
            internal long MinimumUnmanagedMemoryUsageValue => FromSize(MinimumUnmanagedMemoryUsage);
            internal long FilePressureSizeValue => FromSize(FilePressureSize);
            internal long GcAddMemoryPressureValue => FromSize(GcAddMemoryPressure);
        }
        
        [StructLayout(LayoutKind.Sequential)]
        internal class MEMORYSTATUSEX
        {
            internal uint dwLength;
            internal uint dwMemoryLoad;
            internal ulong ullTotalPhys;
            internal ulong ullAvailPhys;
            internal ulong ullTotalPageFile;
            internal ulong ullAvailPageFile;
            internal ulong ullTotalVirtual;
            internal ulong ullAvailVirtual;
            internal ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }
        
        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern bool GlobalMemoryStatusEx( [In,Out] MEMORYSTATUSEX lpBuffer); 
        //Used to use ref with comment below
        // but ref doesn't work.(Use of [In, Out] instead of ref
        //causes access violation exception on windows xp
        //comment: most probably caused by MEMORYSTATUSEX being declared as a class
        //(at least at pinvoke.net). On Win7, ref and struct work.

        
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
                                $"Environment.ProcessorCount:{Environment.ProcessorCount}, " +
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
                        tasks.Add(FilePressureTask(options.FilePressureSizeValue, options.FilePressureWriting == true,
                            options.FilePressureUseMemoryMaps == true, options.FilePressureMemoryMapName));
                    }

                    if (options.MemoryPressureTask == true)
                    {
                        tasks.Add(MemoryPressureTask(options.AllocationUnitSizeValue, options.MemoryPressureRateValue,
                            options.MinimumMemoryUsageValue, options.LeakManagedMemory));
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
                
                if (File.Exists(USAGE_IN_BYTES))
                {
                    usageInBytes = ToSize(Convert.ToInt64(File.ReadLines(USAGE_IN_BYTES).First()));
                }
                
                Log.Information($"Gen012Full:{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{Interlocked.Read(ref _fullGcCompleted)} " +
                                $"Total:{ToSize(GC.GetTotalMemory(false))}, " +
                                $"Allocated:{ToSize(GC.GetTotalAllocatedBytes())}, " +
                                $"HeapSize:{ToSize(gcInfo.HeapSizeBytes)}, " +
                                $"MemoryLoad:{ToSize(gcInfo.MemoryLoadBytes)}, " +
                                $"Committed:{ToSize(gcInfo.TotalCommittedBytes)}, " +
                                $"CGroup:{usageInBytes}, " +
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
                            $"TotalAvailableMemory:{ToSize(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)}, " +
                            $"HighMemoryLoadThreshold:{ToSize(GC.GetGCMemoryInfo().HighMemoryLoadThresholdBytes)}, " +
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
                string calcUsage1 = "N/A";
                string calcUsage2 = "N/A";
                string memoryLoad = "N/A";
                Dictionary<string, long> memoryStatV1 = null;
                Dictionary<string, long> memoryStatV2 = null;
                if (File.Exists(USAGE_IN_BYTES))
                {
                    usageInBytes = ToSize(Convert.ToInt64(File.ReadLines(USAGE_IN_BYTES).First()));
                }
                
                if (File.Exists(MEMORY_CURRENT))
                {
                    usageInBytes = ToSize(Convert.ToInt64(File.ReadLines(MEMORY_CURRENT).First()));
                }
                
                if (File.Exists(MEMORY_STAT_v1))
                {
                    memoryStatV1 = File.ReadLines(MEMORY_STAT_v1).Select(x => x.Split()).ToDictionary(x => x[0], x => Convert.ToInt64(x[1]));
                }
                
                if (File.Exists(MEMORY_STAT_v2))
                {
                    memoryStatV2 = File.ReadLines(MEMORY_STAT_v2).Select(x => x.Split()).ToDictionary(x => x[0], x => Convert.ToInt64(x[1]));
                }
                
                var statEx = new MEMORYSTATUSEX();
                if (Environment.OSVersion.Platform != PlatformID.Unix)
                {
                    GlobalMemoryStatusEx(statEx);
                    memoryLoad = statEx.dwMemoryLoad.ToString();
                }

                if (memoryStatV1 != null)
                {
                    calcUsage1 = ToSize(Convert.ToInt64(memoryStatV1["total_inactive_anon"] +
                                                        memoryStatV1["total_active_anon"] +
                                                        memoryStatV1["total_dirty"] +
                                                        memoryStatV1["total_unevictable"]));

                    calcUsage2 = ToSize(Convert.ToInt64(File.ReadLines(USAGE_IN_BYTES).First())
                                        - memoryStatV1["total_active_file"]
                                        - memoryStatV1["total_inactive_file"]
                                        + memoryStatV1["total_dirty"]);
                }
                
                if (memoryStatV2 != null)
                {
                    calcUsage1 = ToSize(Convert.ToInt64(memoryStatV2["active_anon"] +
                                                        memoryStatV2["inactive_anon"] +
                                                        memoryStatV2["file_dirty"] +
                                                        memoryStatV2["unevictable"]));

                    calcUsage2 = ToSize(Convert.ToInt64(File.ReadLines(MEMORY_CURRENT).First())
                                        - memoryStatV2["inactive_file"]
                                        - memoryStatV2["active_file"]
                                        + memoryStatV2["file_dirty"]);
                }
                
                Log.Information($"Elapsed:{(int) swGlobal.Elapsed.TotalSeconds,3:N0}s, " +
                                $"GC-Rate:{gcRate}, " +
                                $"Gen012Full:{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)},{Interlocked.Read(ref _fullGcCompleted)} " +
                                $"Total:{ToSize(GC.GetTotalMemory(false))}, " +
                                $"GcAllocated:{ToSize(GC.GetTotalAllocatedBytes())}, " +
                                $"HeapSize:{ToSize(gcInfo.HeapSizeBytes)}, " +
                                $"MemoryLoad:{ToSize(gcInfo.MemoryLoadBytes)}, " +
                                $"Committed:{ToSize(gcInfo.TotalCommittedBytes)}, " +
                                $"CGroup:{usageInBytes}, " +
                                /*
                                $"statEx.dwMemoryLoad:{memoryLoad}, " +
                                $"ManagedBlocks:{(Interlocked.Read(ref _allocatedManagedBlocks))}, " +
                                $"UnmanagedBlocks:{(Interlocked.Read(ref _allocatedUnmanagedBlocks))}, " +
                                $"CalcUsage1:{calcUsage1}, " +
                                $"CalcUsage2:{calcUsage2}, " +
                                */
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
                Interlocked.Increment(ref _allocatedManagedBlocks);

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
                        var allocatedBlock = allocate();
                        if (_managedBlocks.Count != 0)
                        {
                            _managedBlocks[idx] = allocatedBlock;
                            if (++idx >= _managedBlocks.Count)
                                idx = 0;
                        }
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
                Interlocked.Increment(ref _allocatedUnmanagedBlocks);

                // Write anything to the new memory block to force-commit it.  
                for (var i = 0; i < unmanagedAllocationUnitSize; i++)
                {
                    Marshal.WriteByte(bytes, i, 42);
                }

                return bytes;
            };
              
            var initialSize = Convert.ToInt32(minimumUnmanagedMemoryUsage / unmanagedAllocationUnitSize);
            var unmanagedMemory = new List<IntPtr>(initialSize);
            unmanagedMemory.AddRange(Enumerable.Range(0, initialSize).Select(_ => allocate()));

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

        static async Task FilePressureTask(long filePressureSize, bool writing, bool useFileMapping, string mapName)
        {
            Log.Information($"Starting FilePressureTask(filePressureSize={ToSize(filePressureSize)}, writing={writing}, useFileMapping={useFileMapping}, mapName={mapName})");
            await Task.Delay(TimeSpan.FromSeconds(1));

            var rnd = new Random();
            var tmpPath = Path.Combine(Path.GetTempPath(), "GcTesting");
            var bytes = new byte[65536];
            await using (var f = File.Open(tmpPath, FileMode.OpenOrCreate, FileAccess.Write))
            {
                rnd.NextBytes(bytes);

                for (var i = 0; i < filePressureSize / bytes.Length; i++)
                {
                    f.Write(bytes);
                }

                Log.Information($"Written {tmpPath}");
            }

            if (useFileMapping)
            {
                // Create the memory-mapped file.
                using (var mmf = MemoryMappedFile.CreateFromFile(tmpPath, FileMode.Open, mapName))
                {
                    // Create a random access view
                    using (var accessor = mmf.CreateViewAccessor(0, filePressureSize))
                    {
                        while (true)
                        {
                            for (var i = 0l; i < filePressureSize / bytes.Length; i++)
                            {
                                if (writing)
                                {
                                    accessor.WriteArray(i * bytes.Length, bytes, 0, bytes.Length);
                                }
                                else
                                {
                                    accessor.ReadArray(i * bytes.Length, bytes, 0, bytes.Length);
                                }
                            }
                            
                            await Task.Delay(TimeSpan.FromSeconds(1));
                        }
                    }
                }
            }
            else
            {
                await using (var f = File.Open(tmpPath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
                {
                    while (true)
                    {
                        f.Seek(0, SeekOrigin.Begin);

                        for (var i = 0; i < filePressureSize / bytes.Length; i++)
                        {
                            if (writing)
                            {
                                f.Write(bytes);
                            }
                            else
                            {
                                f.Read(bytes);
                            }
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1));
                    }
                }
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