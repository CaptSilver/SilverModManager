using System.Runtime.CompilerServices;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Backend.Jobs;

public class JobTask<TJobDefinition, TJobResult> : IJobTask<TJobDefinition, TJobResult> 
    where TJobDefinition : IJobDefinition<TJobResult> where TJobResult : notnull
{
    private readonly JobContext<TJobDefinition, TJobResult> _job;
    internal JobTask(JobContext<TJobDefinition, TJobResult> job)
    {
        _job = job;
    }
    
    public IJob Job => _job;
    
    public TJobDefinition JobDefinition => (TJobDefinition)_job.Definition;
    public TaskAwaiter<TJobResult> GetAwaiter()
    {
        return _job.WaitForResult().GetAwaiter();
    }
    public TJobResult Result => _job.Result;
}
