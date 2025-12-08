using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nest;

class Program
{
    static async Task Main(string[] args)
    {
        var counter = new ParallelUniqueFileCounter("");

        var startDate = DateTime.UtcNow.Date;
        var endDate = startDate.AddDays(-14);

        Console.WriteLine("╔════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         PARALLEL UNIQUE SHA1 FILE COUNTER - 1 WEEKS                        ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"Start Date:      {startDate:yyyy-MM-dd}");
        Console.WriteLine($"End Date:        {endDate:yyyy-MM-dd}");
        Console.WriteLine($"Total Days:      {(startDate - endDate).Days}");
        Console.WriteLine();

        var totalStopwatch = Stopwatch.StartNew();
        var uniqueCount = await counter.CountUniqueFilesAsync(startDate, endDate);
        totalStopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine("╔════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                           FINAL RESULTS                                    ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine($"Total Unique SHA1 Hashes:  {uniqueCount:N0}");
        Console.WriteLine($"Total Processing Time:     {totalStopwatch.Elapsed.TotalHours:F2}h ({totalStopwatch.Elapsed.TotalMinutes:F1}m)");
        Console.WriteLine();

        // Print statistics
        counter.PrintStatistics();

        // Save detailed report to file
        await SaveReport(counter, startDate, endDate, uniqueCount, totalStopwatch.Elapsed);
    }

    static async Task SaveReport(ParallelUniqueFileCounter counter, DateTime startDate, DateTime endDate,
        long uniqueCount, TimeSpan totalTime)
    {
        var resultFileName = $"UniqueFiles_6Months_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";
        var sb = new StringBuilder();

        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("                     UNIQUE FILE COUNT REPORT - 6 MONTHS");
        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine($"Generated:              {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Date Range:             {endDate:yyyy-MM-dd} to {startDate:yyyy-MM-dd}");
        sb.AppendLine($"Total Days Processed:   {(startDate - endDate).Days}");
        sb.AppendLine($"Total Processing Time:  {totalTime.TotalHours:F2}h ({totalTime.TotalMinutes:F1}m)");
        sb.AppendLine();

        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("                              FINAL RESULT");
        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine($"Total Unique SHA1 Hashes: {uniqueCount:N0}");

        var fileInfo = counter.GetFileInfo();
        var totalSize = fileInfo.Values.Where(s => s > 0).Sum();
        var filesWithSize = fileInfo.Count(kvp => kvp.Value > 0);
        var avgSize = filesWithSize > 0 ? fileInfo.Values.Where(s => s > 0).Average() : 0;

        sb.AppendLine($"Total Size of All Files:  {FormatBytes(totalSize)}");
        sb.AppendLine($"Average File Size:        {FormatBytes((long)avgSize)}");
        sb.AppendLine($"Files with Size Info:     {filesWithSize:N0} ({(filesWithSize * 100.0 / fileInfo.Count):F1}%)");
        sb.AppendLine();

        // Daily statistics
        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("                           DAILY STATISTICS");
        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("Date         | New Files    | Total Files  | Processing Time");
        sb.AppendLine("-------------+--------------+--------------+------------------");

        var dailyStats = counter.GetDailyStatistics();
        foreach (var day in dailyStats.OrderByDescending(x => x.Key))
        {
            sb.AppendLine($"{day.Key:yyyy-MM-dd} | {day.Value.NewFiles,12:N0} | {day.Value.TotalFiles,12:N0} | {day.Value.ProcessingTime.TotalMinutes,10:F1}m");
        }
        sb.AppendLine();

        // Failed chunks
        var failedChunks = counter.GetFailedChunks();
        if (failedChunks.Count > 0)
        {
            sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine("                            FAILED CHUNKS");
            sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine($"⚠ WARNING: {failedChunks.Count} chunks failed after all retries:");
            sb.AppendLine();
            foreach (var kvp in failedChunks.OrderBy(x => x.Key))
            {
                sb.AppendLine($"  - {kvp.Key} (failed {kvp.Value} attempts)");
            }
            sb.AppendLine();
        }

        // All unique SHA1s with sizes
        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
        sb.AppendLine("                    ALL UNIQUE FILES (SHA1 + Size)");
        sb.AppendLine("════════════════════════════════════════════════════════════════════════════════");
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
        Console.WriteLine($"✓ Detailed report saved to: {resultFileName}");
    }

    static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
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
    public TimeSpan ProcessingTime { get; set; }
}

public class ParallelUniqueFileCounter
{
    private readonly IElasticClient _client;
    private readonly ConcurrentDictionary<string, long> _fileInfo = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _failedChunks = new ConcurrentDictionary<string, int>();
    private readonly ConcurrentDictionary<DateTime, FileStatistics> _dailyStatistics = new ConcurrentDictionary<DateTime, FileStatistics>();
    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(60); // Max 60 concurrent tasks (1 hour)

    private long _totalChunksProcessed = 0;
    private long _totalChunksFailed = 0;
    private long _totalCompositeIterations = 0;

    public ParallelUniqueFileCounter(string elasticsearchUrl)
    {
        var settings = new ConnectionSettings(new Uri(elasticsearchUrl))
            .BasicAuthentication("", "")
            .ServerCertificateValidationCallback((o, certificate, chain, errors) => true)
            .RequestTimeout(TimeSpan.FromMinutes(5))
            .MaximumRetries(3)
            .ThrowExceptions(false);

        _client = new ElasticClient(settings);
    }

    public async Task<long> CountUniqueFilesAsync(DateTime startDate, DateTime endDate)
    {
        var currentDate = startDate;
        var totalDays = (startDate - endDate).Days;
        var processedDays = 0;

        Console.WriteLine("Starting parallel processing...\n");

        while (currentDate >= endDate)
        {
            var nextDate = currentDate.AddDays(-1);
            processedDays++;

            var dayStopwatch = Stopwatch.StartNew();
            var beforeCount = _fileInfo.Count;

            Console.WriteLine($"[{processedDays,4}/{totalDays}] {currentDate:yyyy-MM-dd}");

            try
            {
                await ProcessDayParallel(currentDate);
                dayStopwatch.Stop();

                var afterCount = _fileInfo.Count;
                var newUnique = afterCount - beforeCount;
                var percentage = (processedDays * 100.0) / totalDays;

                // Store daily statistics
                _dailyStatistics[currentDate] = new FileStatistics
                {
                    NewFiles = newUnique,
                    TotalFiles = afterCount,
                    ProcessingTime = dayStopwatch.Elapsed
                };

                Console.WriteLine($"     ✓ New: {newUnique,10:N0} | Total: {afterCount,11:N0} ({percentage,5:F1}%) | Time: {dayStopwatch.Elapsed.TotalMinutes,6:F1}m");
            }
            catch (Exception ex)
            {
                dayStopwatch.Stop();
                Console.WriteLine($"     ✗ ERROR: {ex.Message}");
            }

            currentDate = nextDate;

            // Memory check every 30 days
            if (processedDays % 30 == 0)
            {
                var memoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                Console.WriteLine($"     └─ Memory: {memoryMB:N0} MB | Unique SHA1s: {_fileInfo.Count:N0} | Composite Iterations: {_totalCompositeIterations:N0}");
            }
        }

        // Report failed chunks
        if (_failedChunks.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"⚠ WARNING: {_failedChunks.Count} chunks failed after all retries");
        }

        return _fileInfo.Count;
    }

    private async Task ProcessDayParallel(DateTime date)
    {
        // Process 24 hours in parallel 
        var hourTasks = new List<Task>();

        for (int hour = 0; hour < 24; hour++)
        {
            var hourStart = date.AddHours(hour);
            hourTasks.Add(ProcessHourWithRetry(hourStart, hour + 1));
        }

        await Task.WhenAll(hourTasks);
    }

    private async Task ProcessHourWithRetry(DateTime hourStart, int hourNum)
    {
        const int maxRetries = 10;

        await _semaphore.WaitAsync();
        try
        {
            var hourStopwatch = Stopwatch.StartNew();

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await ProcessHourChunk(hourStart);
                    hourStopwatch.Stop();

                    Console.WriteLine($"     Hour #{hourNum,2} completed in {hourStopwatch.Elapsed}.");
                    Interlocked.Increment(ref _totalChunksProcessed);
                    return; // Success
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries)
                    {
                        var delayMs = 1000 * (int)Math.Pow(2, attempt - 1);
                        delayMs = Math.Min(delayMs, 10000); // Max 10 seconds
                        await Task.Delay(delayMs);
                    }
                    else
                    {
                        // All retries failed
                        hourStopwatch.Stop();
                        var chunkKey = $"{hourStart:yyyy-MM-dd HH:00}";
                        _failedChunks.TryAdd(chunkKey, attempt);
                        Interlocked.Increment(ref _totalChunksFailed);
                        Console.WriteLine($"     Hour #{hourNum,2} FAILED after {attempt} attempts: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task ProcessHourChunk(DateTime hourStart)
    {
        var hourEnd = hourStart.AddHours(1);
        CompositeKey afterKey = null;
        int iteration = 0;
        const int maxIterations = 1000;

        do
        {
            iteration++;
            Interlocked.Increment(ref _totalCompositeIterations);

            var response = await _client.SearchAsync<object>(s => s
                .Index("")
                .Size(0)
                .Query(q => q
                    .Bool(b => b
                        .Must(
                            m => m.DateRange(dr => dr
                                .Field("")
                                .GreaterThanOrEquals(hourStart)
                                .LessThan(hourEnd)
                            ),
                            m => m.Bool(bb => bb
                                .Should(
                                    sh => sh.Exists(e => e.Field("")),
                                    sh => sh.Exists(e => e.Field(""))
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
                                          '',
                                          ''          
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
                                          '',                             
                                          ''                          
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

                    // Store SHA1 with size (only if not already present)
                    _fileInfo.TryAdd(sha1, fileSize);
                }
            }

            // Get the after_key for next iteration
            afterKey = composite.AfterKey;

            // Safety check
            if (iteration >= maxIterations)
            {
                Console.WriteLine($"     ⚠ Hour {hourStart:HH:mm} reached max iterations ({maxIterations})");
                break;
            }

        } while (afterKey != null && afterKey.Count > 0);
    }

    public ConcurrentDictionary<string, int> GetFailedChunks()
    {
        return _failedChunks;
    }

    public ConcurrentDictionary<string, long> GetFileInfo()
    {
        return _fileInfo;
    }

    public ConcurrentDictionary<DateTime, FileStatistics> GetDailyStatistics()
    {
        return _dailyStatistics;
    }

    public void PrintStatistics()
    {
        Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("                            PROCESSING STATISTICS");
        Console.WriteLine("════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"Total Hours Processed:       {_totalChunksProcessed:N0}");
        Console.WriteLine($"Total Hours Failed:          {_totalChunksFailed:N0}");
        Console.WriteLine($"Total Composite Iterations:  {_totalCompositeIterations:N0}");
        Console.WriteLine($"Success Rate:                {(_totalChunksProcessed * 100.0 / (_totalChunksProcessed + _totalChunksFailed)):F2}%");
        Console.WriteLine();

        var fileInfo = _fileInfo;
        var filesWithSize = fileInfo.Count(kvp => kvp.Value > 0);

        Console.WriteLine($"Files with Size Info:    {filesWithSize:N0} / {fileInfo.Count:N0} ({(filesWithSize * 100.0 / fileInfo.Count):F1}%)");

        if (filesWithSize > 0)
        {
            var totalSize = fileInfo.Values.Where(s => s > 0).Sum();
            var avgSize = fileInfo.Values.Where(s => s > 0).Average();
            var maxSize = fileInfo.Values.Max();
            var minSize = fileInfo.Values.Where(s => s > 0).Min();

            Console.WriteLine($"Total Size:              {FormatBytes(totalSize)}");
            Console.WriteLine($"Average Size:            {FormatBytes((long)avgSize)}");
            Console.WriteLine($"Largest File:            {FormatBytes(maxSize)}");
            Console.WriteLine($"Smallest File:           {FormatBytes(minSize)}");
        }
        Console.WriteLine();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
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