using ProcessingSystem.Models;

namespace ProcessingSystem.Core;

public class ProcessingSystem
{
    // -- Konfiguracija --
    private readonly int _maxQueueSize;
    private readonly int _workerCount;

    // -- Thread-safe priority queue --
    // PriorityQueue je NOT thread-safe, koristimo lock
    private readonly PriorityQueue<(Job job, TaskCompletionSource<int> tcs), int> _queue = new();
    private readonly object _queueLock = new();
    private readonly SemaphoreSlim _queueSignal = new(0);

    // -- Idempotency --
    private readonly HashSet<Guid> _seenIds = [];
    private readonly object _seenLock = new();

    // -- Statistika (za izveštaj) --
    private readonly List<(Job job, int result, TimeSpan duration, bool failed)> _completed = [];
    private readonly object _statsLock = new();

    // -- Events --
    public event Func<Job, int, Task>? JobCompleted;
    public event Func<Job, Task>? JobFailed;

    // -- Izveštaj (ring buffer 10 fajlova) --
    private readonly Timer _reportTimer;
    private int _reportIndex = 0;
    private const int MaxReports = 10;

    // -- CancellationToken za shutdown --
    private readonly CancellationTokenSource _cts = new();

    public ProcessingSystem(int workerCount, int maxQueueSize)
    {
        _workerCount = workerCount;
        _maxQueueSize = maxQueueSize;

        // Pokretanje worker niti
        for (int i = 0; i < _workerCount; i++)
            Task.Run(() => WorkerLoop(_cts.Token));

        // Minutni izveštaj
        _reportTimer = new Timer(_ => GenerateReport(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    // ----------------------------------------------------------------
    // Submit
    // ----------------------------------------------------------------
    public JobHandle? Submit(Job job)
    {
        // Idempotency check
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
                // Ukloni iz seen jer nije dodat
                lock (_seenLock) _seenIds.Remove(job.Id);
                return null;
            }
            _queue.Enqueue((job, tcs), job.Priority);
        }

        _queueSignal.Release();

        return new JobHandle { Id = job.Id, Result = tcs.Task };
    }

    // ----------------------------------------------------------------
    // Worker loop
    // ----------------------------------------------------------------
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

    // ----------------------------------------------------------------
    // Obrada sa retry logikom
    // ----------------------------------------------------------------
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
                var workTask = ExecuteJob(job, cts.Token);
                var result = await workTask.WaitAsync(cts.Token);
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
                // Naredni pokušaj
            }
        }
    }

    // ----------------------------------------------------------------
    // Izvršavanje job-a po tipu — TODO implementirati
    // ----------------------------------------------------------------
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
        // TODO: parsirati payload, paralelno izračunati broj prostih
        throw new NotImplementedException();
    }

    private Task<int> ExecuteIO(Job job, CancellationToken ct)
    {
        // TODO: parsirati payload (delay), Thread.Sleep, random 0-100
        throw new NotImplementedException();
    }

    // ----------------------------------------------------------------
    // Dodatne metode
    // ----------------------------------------------------------------
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

    // ----------------------------------------------------------------
    // Logging
    // ----------------------------------------------------------------
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

    // ----------------------------------------------------------------
    // Izveštaj
    // ----------------------------------------------------------------
    private void GenerateReport()
    {
        // TODO: LINQ izveštaj → XML, ring buffer
        throw new NotImplementedException();
    }

    public void Shutdown()
    {
        _cts.Cancel();
        _reportTimer.Dispose();
    }
}
