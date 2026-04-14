using System.Xml.Linq;
using ProcessingSystem.Models;

namespace ProcessingSystem.Config;

public class SystemConfig
{
    public int WorkerCount { get; set; }
    public int MaxQueueSize { get; set; }
    public List<Job> InitialJobs { get; set; } = [];
}

public static class ConfigLoader
{
    public static SystemConfig Load(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root!;

        var config = new SystemConfig
        {
            WorkerCount = int.Parse(root.Element("WorkerCount")!.Value),
            MaxQueueSize = int.Parse(root.Element("MaxQueueSize")!.Value),
        };

        foreach (var jobEl in root.Element("Jobs")?.Elements("Job") ?? [])
        {
            config.InitialJobs.Add(new Job
            {
                Id = Guid.NewGuid(),
                Type = Enum.Parse<JobType>(jobEl.Attribute("Type")!.Value),
                Payload = jobEl.Attribute("Payload")!.Value,
                Priority = int.Parse(jobEl.Attribute("Priority")!.Value)
            });
        }

        return config;
    }
}
