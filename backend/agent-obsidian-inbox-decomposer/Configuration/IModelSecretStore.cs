namespace TaskSandbox.Configuration;

/// <summary>Stores provider credentials outside settings.json.</summary>
public interface IModelSecretStore
{
    string? Get(string target);
    void Set(string target, string secret);
    void Delete(string target);
}
