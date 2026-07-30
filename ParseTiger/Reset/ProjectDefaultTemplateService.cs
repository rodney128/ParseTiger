using ParseTiger.Models;

namespace ParseTiger.Reset;

public sealed class ProjectDefaultTemplateService
{
    private readonly IReadOnlyDictionary<string, IProjectDefaultTemplate> _templates;

    public ProjectDefaultTemplateService(
        IEnumerable<IProjectDefaultTemplate>? templates = null)
    {
        _templates = (templates ?? new IProjectDefaultTemplate[]
            {
                new WpfProjectDefaultTemplate()
            })
            .ToDictionary(template => template.ProjectKind,
                StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> SupportedProjectKinds =>
        _templates.Keys.ToArray();

    public void Restore(ProjectInfo project)
    {
        if (!_templates.TryGetValue(project.Kind, out IProjectDefaultTemplate? template))
        {
            throw new NotSupportedException(
                $"Visual Studio Default reset currently supports: " +
                $"{string.Join(", ", SupportedProjectKinds)}. " +
                $"The selected project is {project.Kind}.");
        }

        template.Restore(project);
    }
}
