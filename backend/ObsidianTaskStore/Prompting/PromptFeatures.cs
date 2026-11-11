namespace TaskSandbox.Prompting;

[Flags]
public enum PromptFeatures : byte
{
    None = 0,
    Memory = 1 << 0,
    UserExplanation = 1 << 1,
    ProtocolOutput = 1 << 2
}
