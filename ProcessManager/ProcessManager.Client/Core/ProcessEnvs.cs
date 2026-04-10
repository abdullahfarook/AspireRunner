namespace ProcessManager.Client.Core;

public static class ProcessEnvs
{
    public static string? ToEnvString(this IDictionary<string, string?>? envs)
    {
        if (envs is null) return null;
        if (envs.Count == 0)
        {
            return null;
        }

        return string.Join(";", envs.Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }

    public static IDictionary<string, string> ToEnvDictionary(this string envs)
    {
        if (string.IsNullOrWhiteSpace(envs))
        {
            return new Dictionary<string, string>();
        }

        return envs.Split(';')
            .Select(s => s.Split('='))
            .Where(s => s.Length == 2)
            .ToDictionary(s => s[0], s => s[1]);
    }
}