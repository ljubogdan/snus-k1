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
        throw new NotImplementedException();
    }

    private Task<int> ExecuteIO(Job job, CancellationToken ct)
    {
        throw new NotImplementedException();
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
        throw new NotImplementedException();
    }

    public void Shutdown()
    {
        _cts.Cancel();
        _reportTimer.Dispose();
    }
}
