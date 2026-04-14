namespace ProcessingSystem.Models;

public class JobHandle
{
    public Guid Id { get; set; }
    public Task<int> Result { get; set; } = Task.FromResult(0);
}
