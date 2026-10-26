using MaintenancePlanner.Domain.Entities;

namespace MaintenancePlanner.Application.Validation;

public sealed record FeasibilityViolation(string Code, string Message);

public sealed record MaintenanceFeasibilityResult(
    bool IsPotentiallyFeasible,
    IReadOnlyList<FeasibilityViolation> Violations);

public sealed class MaintenanceFeasibilityValidator
{
    public MaintenanceFeasibilityResult Validate(
        IReadOnlyCollection<MaintenanceTaskDefinition> tasks,
        MaintenanceWindow window,
        IReadOnlyCollection<SparePart> parts,
        IReadOnlyCollection<Technician> technicians)
    {
        var violations = new List<FeasibilityViolation>();
        var orderedTasks = OrderTaskChain(tasks);
        var totalDurationMinutes = orderedTasks.Sum(task => task.DurationMinutes);
        var windowMinutes = (window.End - window.Start).TotalMinutes;

        if (totalDurationMinutes > windowMinutes)
        {
            violations.Add(new FeasibilityViolation(
                "WINDOW_EXCEEDED",
                $"The sequential task chain requires {totalDurationMinutes} minutes but the window provides {windowMinutes:0} minutes."));
        }

        var partsById = parts.ToDictionary(part => part.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in orderedTasks
                     .Where(task => task.RequiredPartId is not null && task.RequiredPartQuantity > 0)
                     .GroupBy(task => task.RequiredPartId!, StringComparer.OrdinalIgnoreCase))
        {
            var requiredQuantity = requirement.Sum(task => task.RequiredPartQuantity);
            var availableQuantity = partsById.TryGetValue(requirement.Key, out var part)
                ? part.AvailableQuantity
                : 0;

            if (availableQuantity < requiredQuantity)
            {
                violations.Add(new FeasibilityViolation(
                    "PART_UNAVAILABLE",
                    $"{requirement.Key} has {availableQuantity} available but {requiredQuantity} is required."));
            }
        }

        var taskStart = window.Start;
        foreach (var task in orderedTasks)
        {
            var taskEnd = taskStart.AddMinutes(task.DurationMinutes);
            var requiredSkills = task.RequiredSkills
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var availableCrew = technicians.Count(technician =>
                requiredSkills.All(requiredSkill => technician.TechnicalSkills.Any(skill =>
                    string.Equals(skill.SkillCode, requiredSkill, StringComparison.OrdinalIgnoreCase))) &&
                technician.TechnicianAvailabilities.Any(availability =>
                    availability.AvailableFrom <= taskStart && availability.AvailableTo >= taskEnd));

            if (availableCrew < task.RequiredCrewSize)
            {
                violations.Add(new FeasibilityViolation(
                    "TECHNICIANS_UNAVAILABLE",
                    $"Task {task.Id} requires {task.RequiredCrewSize} qualified technicians, but {availableCrew} are available for its scheduled interval."));
            }

            taskStart = taskEnd;
        }

        return new MaintenanceFeasibilityResult(violations.Count == 0, violations);
    }

    private static IReadOnlyList<MaintenanceTaskDefinition> OrderTaskChain(
        IReadOnlyCollection<MaintenanceTaskDefinition> tasks)
    {
        var tasksById = tasks.ToDictionary(task => task.Id, StringComparer.OrdinalIgnoreCase);
        var orderedTasks = new List<MaintenanceTaskDefinition>(tasks.Count);
        var currentTask = tasks.SingleOrDefault(task => task.PredecessorTaskId is null);

        while (currentTask is not null && !orderedTasks.Contains(currentTask))
        {
            orderedTasks.Add(currentTask);
            currentTask = tasks.SingleOrDefault(task =>
                string.Equals(task.PredecessorTaskId, orderedTasks[^1].Id, StringComparison.OrdinalIgnoreCase));
        }

        if (orderedTasks.Count != tasks.Count || tasksById.Count != tasks.Count)
        {
            throw new ArgumentException("Tasks must form one unambiguous predecessor chain.", nameof(tasks));
        }

        return orderedTasks;
    }
}