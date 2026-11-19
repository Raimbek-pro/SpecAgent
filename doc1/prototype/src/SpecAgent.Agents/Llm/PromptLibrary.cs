namespace SpecAgent.Agents.Llm;

/// <summary>Prompts live as Markdown in /Prompts so they can be reviewed and tuned without touching C#.</summary>
public sealed class PromptLibrary
{
    private readonly string _dir;
    private readonly Dictionary<string, string> _cache = new();

    public PromptLibrary(string? dir = null) =>
        _dir = dir ?? Path.Combine(AppContext.BaseDirectory, "Prompts");

    public string Get(string name)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            var text = File.ReadAllText(Path.Combine(_dir, name + ".md"));
            return _cache[name] = text;
        }
    }

    /// <summary>Role prompt + shared rules. Shared rules go last so the role reads first.</summary>
    public string System(string role) => Get(role) + "\n\n" + Get("_shared");
}
