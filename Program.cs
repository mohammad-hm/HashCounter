using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nest;

class Program
{
    static async Task Main(string[] args)
    {
        var counter = new UniqueFileCounter("https://:9200");

        var startDate = DateTime.UtcNow.Date;
        var endDate = startDate.AddYears(-3);

        Console.WriteLine($"Counting ACTUAL unique SHA1 hashes from {startDate:yyyy-MM-dd} back to {endDate:yyyy-MM-dd}");
        Console.WriteLine("This will collect ALL unique SHA1s in memory to deduplicate across days.\n");

        var uniqueCount = await counter.CountUniqueFilesAsync(startDate, endDate);

        Console.WriteLine($"\n================ FINAL RESULT ================");
        Console.WriteLine($"Total ACTUAL Unique SHA1 Hashes: {uniqueCount:N0}");
        Console.WriteLine($"==============================================");

        // Print timing statistics
        counter.PrintTimingStatistics();

        // Save detailed result to file
        var resultFileName = $"UniqueFilesCount_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";
        var sb = new StringBuilder();

        sb.AppendLine("================================================================================");
        sb.AppendLine("                        UNIQUE FILE COUNT REPORT");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Date Range: {endDate:yyyy-MM-dd} to {startDate:yyyy-MM-dd}");
        sb.AppendLine($"Total Days Processed: {(startDate - endDate).Days}");
        sb.AppendLine();
        sb.AppendLine("================================================================================");
        sb.AppendLine("                             FINAL RESULT");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Total Unique SHA1 Hashes: {uniqueCount:N0}");

        // Get file info
        var fileInfo = counter.GetFileInfo();
        var totalSize = fileInfo.Values.Where(s => s > 0).Sum();
        var filesWithSize = fileInfo.Count(kvp => kvp.Value > 0);
        var avgSize = filesWithSize > 0 ? fileInfo.Values.Where(s => s > 0).Average() : 0;

        sb.AppendLine($"Total Size of All Files: {FormatBytes(totalSize)}");
        sb.AppendLine($"Average File Size: {FormatBytes((long)avgSize)}");
        sb.AppendLine($"Files with Size Info: {filesWithSize:N0} ({(filesWithSize * 100.0 / fileInfo.Count):F1}%)");
        sb.AppendLine();

        // Add daily statistics
        sb.AppendLine("================================================================================");
        sb.AppendLine("                          DAILY STATISTICS");
        sb.AppendLine("================================================================================");
        var dailyStats = counter.GetDailyStatistics();
        foreach (var day in dailyStats.OrderByDescending(x => x.Key))
        {
            sb.AppendLine($"{day.Key:yyyy-MM-dd}: {day.Value.NewFiles:N0} new files, Total so far: {day.Value.TotalFiles:N0}");
        }
        sb.AppendLine();

        // Add timing statistics to file
        var timings = counter.GetChunkTimings();
        if (timings.Count > 0)
        {
            var avgTime = timings.Values.Average(t => t.TotalSeconds);
            var maxTime = timings.Values.Max(t => t.TotalSeconds);
            var minTime = timings.Values.Min(t => t.TotalSeconds);
            var totalTime = TimeSpan.FromSeconds(timings.Values.Sum(t => t.TotalSeconds));

            sb.AppendLine("================================================================================");
            sb.AppendLine("                          TIMING STATISTICS");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Total processing time: {totalTime.TotalHours:F2}h ({totalTime.TotalMinutes:F1}m)");
            sb.AppendLine($"Average chunk time: {avgTime:F2}s");
            sb.AppendLine($"Fastest chunk: {minTime:F2}s");
            sb.AppendLine($"Slowest chunk: {maxTime:F2}s");
            sb.AppendLine($"Total chunks processed: {timings.Count}");
            sb.AppendLine();

            sb.AppendLine("Top 20 Slowest Chunks:");
            foreach (var kvp in timings.OrderByDescending(x => x.Value.TotalSeconds).Take(20))
            {
                sb.AppendLine($"  {kvp.Key}: {kvp.Value.TotalSeconds:F2}s");
            }
            sb.AppendLine();
        }

        var failedChunks = counter.GetFailedChunks();
        if (failedChunks.Count > 0)
        {
            sb.AppendLine("================================================================================");
            sb.AppendLine("                            FAILED CHUNKS");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"WARNING: {failedChunks.Count} time chunks failed after all retries:");
            sb.AppendLine();
            foreach (var kvp in failedChunks.OrderBy(x => x.Key))
            {
                sb.AppendLine($"  - {kvp.Key} (failed {kvp.Value} attempts)");
            }
            sb.AppendLine();
        }

        // Add all unique SHA1s with file sizes
        sb.AppendLine("================================================================================");
        sb.AppendLine("                     ALL UNIQUE FILES (SHA1 + Size)");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Total Files: {fileInfo.Count:N0}");
        sb.AppendLine();
        sb.AppendLine("SHA1                                     | File Size");
        sb.AppendLine("--------------------------------------------------------------------------------");

        foreach (var kvp in fileInfo.OrderBy(x => x.Key))
        {
            var sizeStr = kvp.Value > 0 ? FormatBytes(kvp.Value) : "Unknown";
            sb.AppendLine($"{kvp.Key} | {sizeStr}");
        }

        await File.WriteAllTextAsync(resultFileName, sb.ToString());
        Console.WriteLine($"\n✓ Result saved to: {resultFileName}");
    }

    static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

public class FileStatistics
{
    public int NewFiles { get; set; }
    public int TotalFiles { get; set; }
}

public class UniqueFileCounter
{
    private readonly IElasticClient _client;
    private readonly Dictionary<string, long> _fileInfo = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failedChunks = new Dictionary<string, int>();
    private readonly Dictionary<string, TimeSpan> _chunkTimings = new Dictionary<string, TimeSpan>();
    private readonly Dictionary<DateTime, FileStatistics> _dailyStatistics = new Dictionary<DateTime, FileStatistics>();

    public UniqueFileCounter(string elasticsearchUrl)
    {
        var settings = new ConnectionSettings(new Uri(elasticsearchUrl))
            .BasicAuthentication("", "")
            .ServerCertificateValidationCallback((o, certificate, chain, errors) => true)
            .RequestTimeout(TimeSpan.FromMinutes(2))
            .MaximumRetries(3)
            .ThrowExceptions(false);

        _client = new ElasticClient(settings);
    }

    public async Task<long> CountUniqueFilesAsync(DateTime startDate, DateTime endDate)
    {
        var currentDate = startDate;
        var totalDays = (startDate - endDate).Days;
        var processedDays = 0;

        Console.WriteLine("Processing daily batches and deduplicating...\n");

        while (currentDate >= endDate)
        {
            var nextDate = currentDate.AddDays(-1);
            processedDays++;

            Console.Write($"[{processedDays}/{totalDays}] {currentDate:yyyy-MM-dd} ");

            try
            {
                var beforeCount = _fileInfo.Count;

                // Process day in chunks with retry logic
                var (successCount, failCount) = await ProcessDayInChunks(currentDate);

                var afterCount = _fileInfo.Count;
                var newUnique = afterCount - beforeCount;
                var percentage = (processedDays * 100.0) / totalDays;

                // Store daily statistics
                _dailyStatistics[currentDate] = new FileStatistics
                {
                    NewFiles = newUnique,
                    TotalFiles = afterCount
                };

                if (failCount > 0)
                {
                    Console.WriteLine($" ⚠ New: {newUnique:N0} | Total: {afterCount:N0} ({percentage:F1}%) | Failed chunks: {failCount}/24");
                }
                else
                {
                    Console.WriteLine($" ✓ New: {newUnique:N0} | Total: {afterCount:N0} ({percentage:F1}%)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($" ✗ ERROR: {ex.Message}");
            }

            currentDate = nextDate;
            await Task.Delay(100);

            // Memory check every 100 days
            if (processedDays % 100 == 0)
            {
                var memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                Console.WriteLine($"  └─ Memory usage: {memoryMB:N0} MB");
            }
        }

        // Report failed chunks at the end
        if (_failedChunks.Count > 0)
        {
            Console.WriteLine($"\n⚠ WARNING: {_failedChunks.Count} time chunks failed after all retries:");
            foreach (var kvp in _failedChunks.OrderByDescending(x => x.Value).Take(20))
            {
                Console.WriteLine($"  - {kvp.Key} (failed {kvp.Value} times)");
            }
        }

        return _fileInfo.Count;
    }

    private async Task<(int successCount, int failCount)> ProcessDayInChunks(DateTime date)
    {
        var nextDate = date.AddDays(1);
        int successCount = 0;
        int failCount = 0;
        var dayStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Process day in 1-hour chunks (changed from 4-hour)
        for (int hour = 0; hour < 24; hour += 1)
        {
            var chunkStart = date.AddHours(hour);
            var chunkEnd = date.AddHours(hour + 1);

            var success = await ProcessTimeChunkWithRetry(chunkStart, chunkEnd);

            if (success)
            {
                successCount++;
            }
            else
            {
                failCount++;
            }
        }

        dayStopwatch.Stop();
        Console.Write($" | Day: {dayStopwatch.Elapsed.TotalMinutes:F1}m");

        return (successCount, failCount);
    }

    private async Task<bool> ProcessTimeChunkWithRetry(DateTime startTime, DateTime endTime)
    {
        const int maxRetries = 10;
        const int baseDelaySeconds = 5;

        var chunkKey = $"{startTime:yyyy-MM-dd HH:mm}-{endTime:HH:mm}";
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await ProcessTimeChunk(startTime, endTime);
                stopwatch.Stop();
                _chunkTimings[chunkKey] = stopwatch.Elapsed;

                // Log timing after successful chunk (show less detail for 24 chunks)
                if (stopwatch.Elapsed.TotalSeconds > 60)
                {
                    Console.Write($" [{stopwatch.Elapsed.TotalMinutes:F1}m]");
                }
                else
                {
                    Console.Write($" [{stopwatch.Elapsed.TotalSeconds:F0}s]");
                }

                return true; // Success!
            }
            catch (Exception ex)
            {
                if (attempt < maxRetries)
                {
                    // Exponential backoff: 5s, 10s, 20s, 40s, etc.
                    var delaySeconds = baseDelaySeconds * (int)Math.Pow(2, attempt - 1);
                    delaySeconds = Math.Min(delaySeconds, 120); // Max 2 minutes

                    Console.Write($".");
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
                else
                {
                    // All retries failed
                    stopwatch.Stop();
                    _failedChunks[chunkKey] = attempt;
                    _chunkTimings[chunkKey] = stopwatch.Elapsed;
                    return false;
                }
            }
        }

        return false;
    }

    private async Task ProcessTimeChunk(DateTime startTime, DateTime endTime)
    {
        CompositeKey afterKey = null;
        int iteration = 0;
        const int maxIterations = 500; 

        do
        {
            iteration++;

            var response = await _client.SearchAsync<object>(s => s
                .Index("apxdr-repository")
                .Size(0)
                .Query(q => q
                    .Bool(b => b
                        .Must(
                            m => m.DateRange(dr => dr
                                .Field("@timestamp")
                                .GreaterThanOrEquals(startTime)
                                .LessThan(endTime)
                            ),
                            m => m.Bool(bb => bb
                                .Should(
                                    sh => sh.Exists(e => e.Field("To.File.Hash.SHA1")),
                                    sh => sh.Exists(e => e.Field("To.Application.File.Hash.SHA1"))
                                )
                                .MinimumShouldMatch(1)
                            )
                        )
                    )
                )
                .Aggregations(a => a
                    .Composite("sha1_size_pagination", comp => comp
                        .Size(10000)
                        .After(afterKey)
                        .Sources(src => src
                            .Terms("sha1", t => t
                                .Script(sc => sc
                                    .Source(@"
                                        def paths = [
                                          'To.File.Hash.SHA1.keyword',
                                          'To.Application.File.Hash.SHA1.keyword'          
                                        ];
                            
                                        for (path in paths) {
                                          try {
                                            if (doc.containsKey(path) && doc[path].size() > 0) {
                                              def value = doc[path].value;
                                              if (value != null && !value.isEmpty()) {
                                                return value;
                                              }
                                            }
                                          } catch (Exception e) {}
                                        }
                            
                                        return 'MISSING';
                                    ")
                                    .Lang("painless")
                                )
                            )
                            .Terms("size", t => t
                                .Script(sc => sc
                                    .Source(@"
                                        def sizePaths = [
                                          'To.File.Size',                             
                                          'To.Application.File.Size'                          
                                        ];
                            
                                        for (path in sizePaths) {
                                          try {
                                            if (doc.containsKey(path) && doc[path].size() > 0) {
                                              return doc[path].value;
                                            }
                                          } catch (Exception e) {}
                                        }
                            
                                        return 0;
                                    ")
                                    .Lang("painless")
                                )
                            )
                        )
                    )
                )
            );

            // Check for errors
            if (!response.IsValid)
            {
                var errorMessage = "Unknown error";

                if (response.ServerError != null)
                {
                    errorMessage = response.ServerError.Error?.Reason ?? "Server error";
                }
                else if (response.OriginalException != null)
                {
                    errorMessage = response.OriginalException.Message;
                }
                else if (response.ApiCall?.HttpStatusCode != null)
                {
                    errorMessage = $"HTTP {response.ApiCall.HttpStatusCode}";
                }

                throw new Exception($"ES error: {errorMessage}");
            }

            var composite = response.Aggregations.Composite("sha1_size_pagination");

            if (composite == null || composite.Buckets.Count == 0)
                break;

            // Add SHA1s with file sizes to dictionary
            foreach (var bucket in composite.Buckets)
            {
                string sha1 = null;
                long fileSize = 0;

                // Get SHA1
                if (bucket.Key.TryGetValue("sha1", out string sha1Value))
                {
                    sha1 = sha1Value;
                }

                // Get Size
                if (bucket.Key.TryGetValue("size", out object sizeValue))
                {
                    if (sizeValue is long longSize)
                    {
                        fileSize = longSize;
                    }
                    else if (sizeValue is int intSize)
                    {
                        fileSize = intSize;
                    }
                    else if (sizeValue is double doubleSize)
                    {
                        fileSize = (long)doubleSize;
                    }
                    else if (sizeValue != null)
                    {
                        long.TryParse(sizeValue.ToString(), out fileSize);
                    }
                }

                // Store in dictionary
                if (!string.IsNullOrEmpty(sha1) && sha1 != "MISSING" && sha1 != "null")
                {
                    sha1 = sha1.ToUpperInvariant();

                    // Store SHA1 with size (keep the first size we encounter)
                    if (!_fileInfo.ContainsKey(sha1))
                    {
                        _fileInfo[sha1] = fileSize;
                    }
                }
            }

            // Get the after_key for next iteration
            afterKey = composite.AfterKey;

            // Safety check
            if (iteration >= maxIterations)
            {
                Console.Write($"⚠MAX");
                break;
            }

        } while (afterKey != null && afterKey.Count > 0);
    }

    public int GetFailedChunksCount()
    {
        return _failedChunks.Count;
    }

    public Dictionary<string, int> GetFailedChunks()
    {
        return _failedChunks;
    }

    public Dictionary<string, TimeSpan> GetChunkTimings()
    {
        return _chunkTimings;
    }

    public Dictionary<string, long> GetFileInfo()
    {
        return _fileInfo;
    }

    public Dictionary<DateTime, FileStatistics> GetDailyStatistics()
    {
        return _dailyStatistics;
    }

    public void PrintTimingStatistics()
    {
        if (_chunkTimings.Count == 0)
            return;

        Console.WriteLine("\n================ TIMING STATISTICS ================");

        var avgTime = _chunkTimings.Values.Average(t => t.TotalSeconds);
        var maxTime = _chunkTimings.Values.Max(t => t.TotalSeconds);
        var minTime = _chunkTimings.Values.Min(t => t.TotalSeconds);
        var totalTime = TimeSpan.FromSeconds(_chunkTimings.Values.Sum(t => t.TotalSeconds));

        Console.WriteLine($"Total processing time: {totalTime.TotalHours:F2}h ({totalTime.TotalMinutes:F1}m)");
        Console.WriteLine($"Average chunk time: {avgTime:F2}s");
        Console.WriteLine($"Fastest chunk: {minTime:F2}s");
        Console.WriteLine($"Slowest chunk: {maxTime:F2}s");
        Console.WriteLine($"Total chunks: {_chunkTimings.Count}");

        // Show slowest chunks
        Console.WriteLine("\nTop 10 Slowest Chunks:");
        foreach (var kvp in _chunkTimings.OrderByDescending(x => x.Value.TotalSeconds).Take(10))
        {
            Console.WriteLine($"  {kvp.Key}: {kvp.Value.TotalSeconds:F2}s");
        }
    }
}