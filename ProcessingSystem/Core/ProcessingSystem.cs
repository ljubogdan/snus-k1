using ProcessingSystem.Models;

namespace ProcessingSystem.Core;

public class ProcessingSystem
{
    private readonly int _maxQueueSize;
    private readonly int _workerCount;

    private readonly PriorityQueue<(Job job, TaskCompletionSource<int> tcs), int> _queue = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _queueSignal = new(0);

    private readonly HashSet<Guid> _seenIds = [];
    private readonly object _seenLock = new();

    private readonly List<(Job job, int result, TimeSpan duration, bool failed)> _completed = [];
    private readonly object _statsLock = new();

    public event Func<Job, int, Task>? JobCompleted;
    public event Func<Job, Task>? JobFailed;

    private readonly Timer _reportTimer;
    private int _reportIndex = 0;
    private const int MaxReports = 10;

    private readonly CancellationTokenSource _cts = new();

    public ProcessingSystem(int workerCount, int maxQueueSize)
    {
        _workerCount = workerCount;
        _maxQueueSize = maxQueueSize;

        for (int i = 0; i < _workerCount; i++)
            Task.Run(() => WorkerLoop(_cts.Token));

        _reportTimer = new Timer(_ => GenerateReport(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public JobHandle? Submit(Job job)
    {
        lock (_seenLock)
        {
            if (_seenIds.Contains(job.Id))
                return null;
            _seenIds.Add(job.Id);
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_queueLock)
        {
            if (_queue.Count >= _maxQueueSize)
            {
                lock (_seenLock) _seenIds.Remove(job.Id);
                return null;
            }
            _queue.Enqueue((job, tcs), job.Priority);
        }

        _queueSignal.Release();

        return new JobHandle { Id = job.Id, Result = tcs.Task };
    }

    private async Task WorkerLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(ct);

            (Job job, TaskCompletionSource<int> tcs) item;
            lock (_queueLock)
            {
                if (!_queue.TryDequeue(out item, out _))
                    continue;
            }

            await ProcessWithRetry(item.job, item.tcs);
        }
    }

    private async Task ProcessWithRetry(Job job, TaskCompletionSource<int> tcs)
    {
        const int maxAttempts = 3;
        const int timeoutMs = 2000;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                var result = await ExecuteJob(job, cts.Token).WaitAsync(cts.Token);
                sw.Stop();

                lock (_statsLock)
                    _completed.Add((job, result, sw.Elapsed, false));

                tcs.TrySetResult(result);

                if (JobCompleted != null)
                    _ = Task.Run(() => JobCompleted.Invoke(job, result));

                return;
            }
            catch (Exception)
            {
                sw.Stop();

                if (attempt == maxAttempts)
                {
                    lock (_statsLock)
                        _completed.Add((job, -1, sw.Elapsed, true));

                    if (JobFailed != null)
                        _ = Task.Run(() => JobFailed.Invoke(job));

                    await LogAbort(job);
                    tcs.TrySetException(new Exception($"Job {job.Id} aborted after {maxAttempts} attempts."));
                    return;
                }
            }
        }
    }

    private Task<int> ExecuteJob(Job job, CancellationToken ct)
    {
        return job.Type switch
        {
            JobType.Prime => ExecutePrime(job, ct),
            JobType.IO => ExecuteIO(job, ct),
            _ => throw new NotSupportedException($"Unknown job type: {job.Type}")
        };
    }

    private Task<int> ExecutePrime(Job job, CancellationToken ct)
    {
        var parts = job.Payload.Split(',');
        int limit = int.Parse(parts[0].Split(':')[1].Replace("_", ""));
        int threads = Math.Clamp(int.Parse(parts[1].Split(':')[1].Replace("_", "")), 1, 8);

        return Task.Run(() =>
        {
            int count = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct };

            Parallel.For(2, limit + 1, options, () => 0, (n, state, local) =>
            {
                if (IsPrime(n)) local++;
                return local;
            }, local => Interlocked.Add(ref count, local));

            return count;
        }, ct);
    }

    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        if (n == 2) return true;
        if (n % 2 == 0) return false;
        for (int i = 3; i * i <= n; i += 2)
            if (n % i == 0) return false;
        return true;
    }

    private Task<int> ExecuteIO(Job job, CancellationToken ct)
    {
        int delay = int.Parse(job.Payload.Split(':')[1].Replace("_", ""));

        return Task.Run(() =>
        {
            Thread.Sleep(delay);
            ct.ThrowIfCancellationRequested();
            return Random.Shared.Next(0, 101);
        }, ct);
    }

    public IEnumerable<Job> GetTopJobs(int n)
    {
        lock (_queueLock)
        {
            return _queue.UnorderedItems
                .OrderBy(x => x.Priority)
                .Take(n)
                .Select(x => x.Element.job)
                .ToList();
        }
    }

    public Job? GetJob(Guid id)
    {
        lock (_queueLock)
        {
            var inQueue = _queue.UnorderedItems.FirstOrDefault(x => x.Element.job.Id == id);
            if (inQueue.Element.job != null)
                return inQueue.Element.job;
        }

        lock (_statsLock)
            return _completed.FirstOrDefault(x => x.job.Id == id).job;
    }

    private static readonly SemaphoreSlim _logLock = new(1, 1);

    public static async Task LogEvent(string status, Guid jobId, int result)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{status}] {jobId}, {result}";
        await _logLock.WaitAsync();
        try { await File.AppendAllTextAsync("jobs.log", line + Environment.NewLine); }
        finally { _logLock.Release(); }
    }

    private static async Task LogAbort(Job job)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ABORT] {job.Id}, -1";
        await _logLock.WaitAsync();
        try { await File.AppendAllTextAsync("jobs.log", line + Environment.NewLine); }
        finally { _logLock.Release(); }
    }

    private void GenerateReport()
    {
        List<(Job job, int result, TimeSpan duration, bool failed)> snapshot;
        lock (_statsLock)
            snapshot = [.. _completed];

        var byType = snapshot.GroupBy(x => x.job.Type);

        var report = new System.Xml.Linq.XElement("Report",
            new System.Xml.Linq.XAttribute("GeneratedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            byType.Select(g => new System.Xml.Linq.XElement("JobType",
                new System.Xml.Linq.XAttribute("Type", g.Key),
                new System.Xml.Linq.XElement("Completed", g.Count(x => !x.failed)),
                new System.Xml.Linq.XElement("Failed", g.Count(x => x.failed)),
                new System.Xml.Linq.XElement("AvgDurationMs",
                    Math.Round(g.Where(x => !x.failed).Select(x => x.duration.TotalMilliseconds).DefaultIfEmpty(0).Average(), 2))
            )).OrderBy(x => x.Attribute("Type")!.Value)
        );

        string fileName = $"report_{_reportIndex % MaxReports}.xml";
        _reportIndex++;

        new System.Xml.Linq.XDocument(report).Save(fileName);
    }

    public void Shutdown()
    {
        _cts.Cancel();
        _reportTimer.Dispose();
    }
}
