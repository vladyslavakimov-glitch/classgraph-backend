namespace ClassGraph.SampleDomain;

public interface IAssignable
{
    void AssignTo(Project project);
}

public enum ProjectStatus
{
    Planned,
    Active,
    Paused,
    Completed
}

public abstract class Person
{
    public string Name { get; set; } = string.Empty;

    public string? Email { get; set; }

    public virtual string GetDisplayName() => string.IsNullOrWhiteSpace(Name) ? GetType().Name : Name;
}

public class Employee : Person, IAssignable
{
    public Department? Department { get; set; }

    public ICollection<Project> Projects { get; } = new List<Project>();

    public virtual void AssignTo(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Projects.Contains(project))
        {
            Projects.Add(project);
        }
    }

    public Project? FindProject(ProjectStatus status) => Projects.FirstOrDefault(project => project.Status == status);
}

public class Manager : Employee
{
    public Department? ManagedDepartment { get; set; }

    public Employee? FindSubstitute(Employee candidate) => candidate;
}

public class Department
{
    public string Name { get; set; } = string.Empty;

    public Manager? Head { get; set; }

    public ICollection<Employee> Employees { get; } = new List<Employee>();

    public void AddEmployee(Employee employee)
    {
        ArgumentNullException.ThrowIfNull(employee);
        if (!Employees.Contains(employee))
        {
            Employees.Add(employee);
        }
    }
}

public class Project
{
    public string Title { get; set; } = string.Empty;

    public ProjectStatus Status { get; set; } = ProjectStatus.Planned;

    public Employee? Lead { get; set; }

    public ICollection<Employee> Members { get; } = new List<Employee>();

    public bool CanAccept(Employee employee) => employee is not null && Status is not ProjectStatus.Completed;
}
