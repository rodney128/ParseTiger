using ParseTiger.Models;

namespace ParseTiger.Reset;

public interface IProjectDefaultTemplate
{
    string ProjectKind { get; }

    void Restore(ProjectInfo project);
}
