using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Linq;


namespace GcTesting
{
    class Program
    {
        private static readonly long ELEMENT_SIZE = 128 * (1 << 10);
        private static readonly long MEMORY_USAGE = 1 * (1l << 30); // 1 GB
        private static readonly long MEMORY_PRESSURE = 100 * (1l << 20); // 10 MB / s
        private static readonly long FILE_SIZE = 4 * (1l << 30); // 4 GB

        static async Task Main(string[] args)
        {
            var tasks = new []{GcStatsTask(), MemoryPressureTask(), FilePressureTask()};
            await await Task.WhenAny(tasks);
        }

        static async Task GcStatsTask()
        {
            var stats = new Queue<GCMemoryInfo>();
            while (true)
            {
                var gcInfo = GC.GetGCMemoryInfo();
                var gcSpeed = "N/A";

                // Keep probes from last 60 seconds
                if (stats.Count >= 60)
                {
                    var oldGcInfo = stats.Dequeue();
                    gcSpeed = Convert.ToString(gcInfo.Index - oldGcInfo.Index);
                }
                
                stats.Enqueue(gcInfo);

                Console.WriteLine($"gc/min:{gcSpeed}, " +
                                  $"Gen012:{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}, " +
                                  $"Total:{FormatBytes(GC.GetTotalMemory(false))}, " +
                                  $"Allocated:{FormatBytes(GC.GetTotalAllocatedBytes())}, " +
                                  $"HeapSize:{FormatBytes(gcInfo.HeapSizeBytes)}, " +
                                  $"MemoryLoad:{FormatBytes(gcInfo.MemoryLoadBytes)}, " +
                                  $"Committed:{FormatBytes(gcInfo.TotalCommittedBytes)}, " +
                                  $"Available:{FormatBytes(gcInfo.TotalAvailableMemoryBytes)}, " +
                                  $"HighMemoryLoadThreshold:{FormatBytes(gcInfo.HighMemoryLoadThresholdBytes)}, "
                                  );

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        static async Task MemoryPressureTask()
        {
            var list = new List<object>(Enumerable.Range(0, Convert.ToInt32(MEMORY_USAGE / ELEMENT_SIZE))
                .Select(x => (object)null));
            var rnd = new Random();
            var allocatedMemoryInCycle = 0l;
            var cycleSw = Stopwatch.StartNew();
            while (true)
            {
                var idx = rnd.Next(list.Count);
                list[idx] = new byte[ELEMENT_SIZE];
                allocatedMemoryInCycle += ELEMENT_SIZE;
                if (allocatedMemoryInCycle >= MEMORY_PRESSURE)
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


        static async Task FilePressureTask()
        {
            var rnd = new Random();
            var tmpPath = Path.Combine(Path.GetTempPath(), "GcTesting");
            var bytes = new byte[1024];
            await using var f = File.Open(tmpPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            for (var i = 0; i < FILE_SIZE / bytes.Length; i++)
            {
                rnd.NextBytes(bytes);
                f.Write(bytes);
            }

            while (true)
            {
                f.Seek(0, SeekOrigin.Begin);

                for (var i = 0; i < FILE_SIZE / bytes.Length; i++)
                {
                    f.Read(bytes);
                }

                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }

        static string FormatBytes(long bytes)
        {
            const int scale = 1024;
            string[] orders = new string[] { "GB", "MB", "KB", "Bytes" };
            long max = (long)Math.Pow(scale, orders.Length - 1);

            foreach (string order in orders)
            {
                if ( bytes > max )
                    return string.Format("{0:##.###0} {1}", decimal.Divide( bytes, max ), order);

                max /= scale;
            }
            
            return "0 Bytes";
        }
    }
}