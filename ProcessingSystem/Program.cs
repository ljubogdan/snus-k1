using ProcessingSystem.Config;
using ProcessingSystem.Core;
using ProcessingSystem.Models;

// -- Učitavanje konfiguracije --
var config = ConfigLoader.Load("SystemConfig.xml");
var system = new ProcessingSystem.Core.ProcessingSystem(config.WorkerCount, config.MaxQueueSize);

// -- Pretplate na evente --
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

// -- Inicijalni poslovi iz XML --
foreach (var job in config.InitialJobs)
{
    var handle = system.Submit(job);
    if (handle == null)
        Console.WriteLine($"[REJECTED] {job.Id} (queue full or duplicate)");
}

// -- Producer niti --
var random = new Random();
var jobTypes = Enum.GetValues<JobType>();
var producers = new List<Thread>();

for (int i = 0; i < config.WorkerCount; i++)
{
    var thread = new Thread(() =>
    {
        while (true)
        {
            try
            {
                var type = jobTypes[random.Next(jobTypes.Length)];
                var payload = type == JobType.Prime
                    ? $"numbers:{random.Next(1000, 50000)},threads:{random.Next(1, 9)}"
                    : $"delay:{random.Next(100, 5000)}";

                var job = new Job
                {
                    Id = Guid.NewGuid(),
                    Type = type,
                    Payload = payload,
                    Priority = random.Next(1, 6)
                };

                var handle = system.Submit(job);
                if (handle == null)
                    Console.WriteLine($"[REJECTED] {job.Id} (queue full or duplicate)");

                Thread.Sleep(random.Next(200, 1000));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Producer: {ex.Message}");
            }
        }
    }) { IsBackground = true };

    producers.Add(thread);
    thread.Start();
}

Console.WriteLine("ProcessingSystem pokrenut. Pritisni Enter za kraj.");
Console.ReadLine();
system.Shutdown();
