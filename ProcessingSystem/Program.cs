using ProcessingSystem.Config;
using ProcessingSystem.Models;

var config = ConfigLoader.Load("SystemConfig.xml");
var system = new ProcessingSystem.Core.ProcessingSystem(config.WorkerCount, config.MaxQueueSize);

system.JobCompleted += async (job, result) =>
{
    await ProcessingSystem.Core.ProcessingSystem.LogEvent("COMPLETED", job.Id, result);
    Console.WriteLine($"[COMPLETED] {job.Id} → {result}");
};

system.JobFailed += async (job) =>
{
    await ProcessingSystem.Core.ProcessingSystem.LogEvent("FAILED", job.Id, -1);
    Console.WriteLine($"[FAILED] {job.Id}");
};

foreach (var job in config.InitialJobs)
{
    var handle = system.Submit(job);
    if (handle == null)
        Console.WriteLine($"[REJECTED] {job.Id} (queue full or duplicate)");
}

var jobTypes = Enum.GetValues<JobType>();

for (int i = 0; i < config.WorkerCount; i++)
{
    new Thread(() =>
    {
        while (true)
        {
            try
            {
                var type = jobTypes[Random.Shared.Next(jobTypes.Length)];
                var payload = type == JobType.Prime
                    ? $"numbers:{Random.Shared.Next(1000, 50000)},threads:{Random.Shared.Next(1, 9)}"
                    : $"delay:{Random.Shared.Next(100, 5000)}";

                var job = new Job
                {
                    Id = Guid.NewGuid(),
                    Type = type,
                    Payload = payload,
                    Priority = Random.Shared.Next(1, 6)
                };

                var handle = system.Submit(job);
                if (handle == null)
                    Console.WriteLine($"[REJECTED] {job.Id} (queue full or duplicate)");

                Thread.Sleep(Random.Shared.Next(200, 1000));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Producer: {ex.Message}");
            }
        }
    }) { IsBackground = true }.Start();
}

Console.WriteLine("ProcessingSystem pokrenut. Pritisni Enter za kraj.");
Console.ReadLine();

Console.WriteLine("\n--- Top 3 poslova u redu ---");
foreach (var job in system.GetTopJobs(3))
    Console.WriteLine($"  [{job.Priority}] {job.Type} | {job.Id}");

system.Shutdown();
