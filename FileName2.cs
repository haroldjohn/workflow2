// ExecutionCommand and ExecutionWorkflow Classes (Generic Command)

public class ExecutionCommand<T> where T : IHardwareComponent
{
    public int CommandId { get; set; }
    public T HardwareComponent { get; set; }
    public int ExecutionOrder { get; set; }  // Specifies the sequence order of the command in the workflow
    public Guid SequenceId { get; set; }  // Unique identifier for the sequence this command belongs to
    public Dictionary<string, object> Parameters { get; set; } // Command parameters
    public DateTime StartTime { get; set; }  // Specifies the earliest start time for the command
    public DateTime CompleteBy { get; set; }  // Specifies the deadline by which the command must be completed
}

public class ExecutionWorkflow
{
    public Guid SequenceId { get; set; }  // Unique identifier for the workflow
    public List<ExecutionCommand<IHardwareComponent>> Commands { get; set; } = new List<ExecutionCommand<IHardwareComponent>>();
    public int CurrentCommandIndex { get; set; } = 0; // Tracks progress
    public int PendingParallelCommands { get; set; } = 0;  // Tracks how many parallel commands are still in progress
}

// IAgentObserver Interface (Non-Generic)

public interface IAgentObserver : IGrainObserver
{
    void OnTaskCompleted(int commandId, Guid sequenceId, bool success, string message);
}

// Agent<T> Class

public class Agent<T> : Grain, IAgent<T> where T : IHardwareComponent
{
    private readonly List<IAgentObserver> _observers = new List<IAgentObserver>();

    public async Task OnDequeueCommand(ExecutionCommand<T> command)
    {
        // Execute the command
        bool success = await command.HardwareComponent.ActivateAsync(command.Parameters);
        string message = success ? "Command completed successfully." : "Command failed.";

        // Notify all observers (e.g., Supervisor) that the command is completed
        foreach (var observer in _observers)
        {
            observer.OnTaskCompleted(command.CommandId, command.SequenceId, success, message);  // Fire-and-forget
        }
    }

    public Task Subscribe(IAgentObserver observer)
    {
        if (!_observers.Contains(observer))
        {
            _observers.Add(observer);
        }
        return Task.CompletedTask;
    }

    public Task Unsubscribe(IAgentObserver observer)
    {
        if (_observers.Contains(observer))
        {
            _observers.Remove(observer);
        }
        return Task.CompletedTask;
    }
}

// ISupervisor Interface

public interface ISupervisor : IGrainWithIntegerKey
{
    Task StartWorkflow(ExecutionWorkflow workflow);
    Task CommandCompleted(int commandId, Guid sequenceId);
}

// Supervisor Class (Handles Multiple Agents and Multiple Workflows)

public class Supervisor : Grain, ISupervisor, IAgentObserver
{
    // Dictionary to manage multiple workflows, keyed by SequenceId
    private readonly Dictionary<Guid, ExecutionWorkflow> _workflows = new Dictionary<Guid, ExecutionWorkflow>();
    private readonly Dictionary<Type, IAgent<IHardwareComponent>> _agents = new Dictionary<Type, IAgent<IHardwareComponent>>();

    public Task StartWorkflow(ExecutionWorkflow workflow)
    {
        // Store the workflow by its SequenceId
        _workflows[workflow.SequenceId] = workflow;
        return QueueNextCommand(workflow.SequenceId);  // Queue the first command(s)
    }

    private IAgent<IHardwareComponent> GetAgentForComponent(IHardwareComponent hardwareComponent)
    {
        // Retrieve the correct agent for the hardware component's type
        var componentType = hardwareComponent.GetType();
        if (!_agents.ContainsKey(componentType))
        {
            // Get the agent for this hardware component type and store it for future use
            var agent = GrainFactory.GetGrain<IAgent<IHardwareComponent>>(this.GetPrimaryKey());
            _agents[componentType] = agent;
        }

        return _agents[componentType];
    }

    public async Task QueueNextCommand(Guid sequenceId)
    {
        // Get the correct workflow from the dictionary
        if (_workflows.TryGetValue(sequenceId, out var workflow))
        {
            if (workflow.CurrentCommandIndex < workflow.Commands.Count)
            {
                // Collect commands that can be executed in parallel
                var commandsToExecute = new List<ExecutionCommand<IHardwareComponent>>();
                var currentCommand = workflow.Commands[workflow.CurrentCommandIndex];

                while (currentCommand.ExecutionOrder == workflow.CurrentCommandIndex && workflow.CurrentCommandIndex < workflow.Commands.Count)
                {
                    commandsToExecute.Add(currentCommand);
                    workflow.CurrentCommandIndex++;

                    if (workflow.CurrentCommandIndex < workflow.Commands.Count)
                    {
                        currentCommand = workflow.Commands[workflow.CurrentCommandIndex];
                    }
                }

                if (commandsToExecute.Count > 0)
                {
                    workflow.PendingParallelCommands = commandsToExecute.Count;

                    foreach (var command in commandsToExecute)
                    {
                        var agent = GetAgentForComponent(command.HardwareComponent);  // Get the agent for the command's hardware component

                        var observer = this.AsReference<IAgentObserver>();
                        await agent.Subscribe(observer);

                        await agent.OnDequeueCommand(command);  // Send the command to the correct Agent
                    }
                }
                else
                {
                    var agent = GetAgentForComponent(currentCommand.HardwareComponent);

                    var observer = this.AsReference<IAgentObserver>();
                    await agent.Subscribe(observer);

                    await agent.OnDequeueCommand(currentCommand);  // Send the command to the correct Agent
                }
            }
        }
    }

    public void OnTaskCompleted(int commandId, Guid sequenceId, bool success, string message)
    {
        Console.WriteLine($"Command {commandId} in workflow {sequenceId} completed: {message}");

        if (_workflows.TryGetValue(sequenceId, out var workflow))
        {
            if (workflow.PendingParallelCommands > 0)
            {
                workflow.PendingParallelCommands--;
            }

            if (workflow.PendingParallelCommands == 0)
            {
                _ = QueueNextCommand(sequenceId);  // Queue the next command(s) for this workflow
            }
        }
    }
}
