using SqlFlow.Delivery.Model;
using SqlFlow.Yaml;

namespace SqlFlow.Delivery.Documents;

/// <summary>
/// The repository documents (<c>documentType: repository</c>) the runs of a repository's flows take their code from, as the
/// platform sees them: the CLI validates one under its own type, and the repository sync and the proposal preflight read one
/// instead of reporting it as a flow document with no <c>flowType</c>.
/// </summary>
public sealed class RepositoryDocumentKind : ICompanionDocumentKind
{
    private readonly DeliveryDocumentLoader _loader;

    public RepositoryDocumentKind(DeliveryDocumentLoader loader)
    {
        ArgumentNullException.ThrowIfNull(loader);
        _loader = loader;
    }

    public string DocumentType => RepositoryDefinition.DocumentTypeName;

    public string Description => "the code a repository's runs carry in the correlation id of their OSDU requests";

    public string ParseCompanion(string yaml, string source)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        return _loader.ParseRepository(yaml, source).Code;
    }
}
