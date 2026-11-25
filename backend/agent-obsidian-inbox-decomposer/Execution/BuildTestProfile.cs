using System.Text.Json;

namespace TaskSandbox.Execution;

public sealed record ProcessCommand(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Detects a project's build and test commands from its root manifests.</summary>
public sealed record BuildTestProfile(
    string Name,
    ProcessCommand? Build,
    ProcessCommand? Test,
    IReadOnlySet<string> SourceExtensions)
{
    public static BuildTestProfile? Detect(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        if (!Directory.Exists(projectDirectory))
            return null;

        string root = Path.GetFullPath(projectDirectory);
        string[] files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName).Where(static name => name is not null).Cast<string>().ToArray();
        bool IsSafeFile(string name) =>
            (File.GetAttributes(Path.Combine(root, name)) & FileAttributes.ReparsePoint) == 0;
        bool Has(string name) => files.Contains(name, StringComparer.OrdinalIgnoreCase) && IsSafeFile(name);

        bool hasDotNetProject = files.Any(file => IsSafeFile(file) &&
            (Path.GetExtension(file).Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(file).Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(file).Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(file).Equals(".vbproj", StringComparison.OrdinalIgnoreCase)));
        if (hasDotNetProject)
            return new BuildTestProfile(".NET",
                new ProcessCommand("dotnet", ["build", "--nologo", "--verbosity", "minimal"]),
                new ProcessCommand("dotnet", ["test", "--nologo", "--verbosity", "minimal"]),
                new HashSet<string>([".cs", ".fs", ".vb"], StringComparer.OrdinalIgnoreCase));

        if (Has("package.json"))
        {
            string manager = Has("pnpm-lock.yaml") ? "pnpm" : Has("yarn.lock") ? "yarn" : "npm";
            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
            JsonElement scripts = package.RootElement.ValueKind == JsonValueKind.Object &&
                                  package.RootElement.TryGetProperty("scripts", out JsonElement value) &&
                                  value.ValueKind == JsonValueKind.Object ? value : default;
            ProcessCommand? build = HasScript(scripts, "build")
                ? manager == "yarn" ? new ProcessCommand("yarn", ["build"]) : new ProcessCommand(manager, ["run", "build"])
                : null;
            ProcessCommand? test = HasScript(scripts, "test")
                ? manager == "yarn" ? new ProcessCommand("yarn", ["test"]) : new ProcessCommand(manager, ["run", "test"])
                : null;
            return new BuildTestProfile("Node.js", build, test,
                new HashSet<string>([".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"], StringComparer.OrdinalIgnoreCase));
        }

        if (Has("go.mod"))
            return new BuildTestProfile("Go",
                new ProcessCommand("go", ["build", "./..."]),
                new ProcessCommand("go", ["test", "./..."]),
                new HashSet<string>([".go"], StringComparer.OrdinalIgnoreCase));

        if (Has("Cargo.toml"))
            return new BuildTestProfile("Rust",
                new ProcessCommand("cargo", ["build"]),
                new ProcessCommand("cargo", ["test"]),
                new HashSet<string>([".rs"], StringComparer.OrdinalIgnoreCase));

        if (Has("pom.xml"))
            return new BuildTestProfile("Java (Maven)",
                new ProcessCommand("mvn", ["-B", "-DskipTests", "package"]),
                new ProcessCommand("mvn", ["-B", "test"]),
                new HashSet<string>([".java"], StringComparer.OrdinalIgnoreCase));

        if (Has("build.gradle") || Has("build.gradle.kts"))
            return new BuildTestProfile("Java (Gradle)",
                new ProcessCommand("gradle", ["build", "-x", "test"]),
                new ProcessCommand("gradle", ["test"]),
                new HashSet<string>([".java", ".kt", ".kts"], StringComparer.OrdinalIgnoreCase));

        if (Has("pyproject.toml") || Has("pytest.ini") || Has("tox.ini") || Has("setup.cfg") || Has("setup.py"))
        {
            bool hasPytest = Has("pytest.ini") ||
                (Has("pyproject.toml") && File.ReadAllText(Path.Combine(root, "pyproject.toml"))
                    .Contains("[tool.pytest.ini_options]", StringComparison.Ordinal)) ||
                (Has("setup.cfg") && File.ReadAllText(Path.Combine(root, "setup.cfg"))
                    .Contains("[tool:pytest]", StringComparison.Ordinal));
            bool hasTests = Directory.Exists(Path.Combine(root, "tests")) ||
                            Directory.EnumerateFiles(root, "test_*.py", SearchOption.TopDirectoryOnly).Any();
            ProcessCommand? test = hasTests
                ? hasPytest ? new ProcessCommand("python", ["-m", "pytest", "-q"])
                    : new ProcessCommand("python", ["-m", "unittest", "discover"])
                : null;
            return new BuildTestProfile("Python", null, test,
                new HashSet<string>([".py"], StringComparer.OrdinalIgnoreCase));
        }

        return null;
    }

    private static bool HasScript(JsonElement scripts, string name)
    {
        if (scripts.ValueKind != JsonValueKind.Object || !scripts.TryGetProperty(name, out JsonElement script) ||
            script.ValueKind != JsonValueKind.String)
            return false;
        string? command = script.GetString();
        return !string.IsNullOrWhiteSpace(command) &&
               !(name == "test" && command.Contains("Error: no test specified", StringComparison.OrdinalIgnoreCase));
    }
}
